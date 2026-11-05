/* =============================================================================================
   Value history (SQL Server system-versioned temporal tables)
   ---------------------------------------------------------------------------------------------
   Turns on automatic history for the tables that uploads and data entry change:
       core.BudgetLines, core.MidYearGlActualForecasts, core.HistoricalGlActuals, core.Items
   After this runs, SQL Server keeps every previous version of every row (who changed it is in
   the app's own columns / Upload History; WHEN and WHAT is kept here) in a matching
   core.<Table>_History table. Nothing in the application has to change: the two period
   columns are HIDDEN, so existing queries and Entity Framework are unaffected.

   Requirements : SQL Server 2016+ / Azure SQL, database compatibility level 130 or higher.
   Safe to run more than once (tables that are already versioned are skipped).
   Run it once per database (local, sandbox, production) with a login that can ALTER TABLE.
   Select the GovBudget database first.

   To turn history off again for one table (history rows are kept in the _History table):
       ALTER TABLE core.BudgetLines SET (SYSTEM_VERSIONING = OFF);
   ============================================================================================= */

SET NOCOUNT ON;

IF (SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()) < 130
BEGIN
    RAISERROR('Database compatibility level must be 130 or higher for temporal tables. Nothing was changed.', 16, 1);
    RETURN;
END;

DECLARE @tables TABLE (Name SYSNAME);
INSERT INTO @tables (Name) VALUES (N'BudgetLines'), (N'MidYearGlActualForecasts'), (N'HistoricalGlActuals'), (N'Items');

DECLARE @t SYSNAME, @full NVARCHAR(300), @sql NVARCHAR(MAX);
DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT Name FROM @tables;
OPEN c;
FETCH NEXT FROM c INTO @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @full = N'core.' + QUOTENAME(@t);

    IF OBJECT_ID(@full, N'U') IS NULL
        PRINT 'Skipped ' + @t + ': table not found.';
    ELSE IF OBJECTPROPERTY(OBJECT_ID(@full), 'TableTemporalType') = 2
        PRINT 'Skipped ' + @t + ': history is already on.';
    ELSE
    BEGIN
        BEGIN TRY
            BEGIN TRANSACTION;

            IF COL_LENGTH(@full, 'ValidFrom') IS NULL
            BEGIN
                SET @sql = N'ALTER TABLE ' + @full + N' ADD
                    ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START HIDDEN
                        CONSTRAINT ' + QUOTENAME(N'DF_' + @t + N'_ValidFrom') + N' DEFAULT SYSUTCDATETIME(),
                    ValidTo   DATETIME2 GENERATED ALWAYS AS ROW END HIDDEN
                        CONSTRAINT ' + QUOTENAME(N'DF_' + @t + N'_ValidTo') + N' DEFAULT CONVERT(DATETIME2, ''9999-12-31 23:59:59.9999999''),
                    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo);';
                EXEC sys.sp_executesql @sql;
            END;

            SET @sql = N'ALTER TABLE ' + @full + N' SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = core.'
                     + QUOTENAME(@t + N'_History') + N', DATA_CONSISTENCY_CHECK = ON));';
            EXEC sys.sp_executesql @sql;

            COMMIT TRANSACTION;
            PRINT 'History turned on for ' + @t + ' (core.' + @t + '_History).';
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
            PRINT 'FAILED for ' + @t + ': ' + ERROR_MESSAGE() + ' - this table was left unchanged.';
        END CATCH;
    END;

    FETCH NEXT FROM c INTO @t;
END;
CLOSE c;
DEALLOCATE c;
GO

/* ---------------------------------------------------------------------------------------------
   How to use the history (examples)
   ---------------------------------------------------------------------------------------------

   -- 1) Every version of one mid-year GL, newest first
   SELECT m.GLCode, m.ActualH1Amount, m.ForecastH2Amount, m.SourceFile, m.CreatedBy,
          m.ForecastUpdatedBy, m.ValidFrom, m.ValidTo
   FROM core.MidYearGlActualForecasts FOR SYSTEM_TIME ALL AS m
   JOIN core.Entities e ON e.EntityId = m.EntityId
   WHERE e.EntityCode = 'RGRA' AND m.BudgetYear = 2026 AND m.GLCode = '51101001'
   ORDER BY m.ValidFrom DESC;

   -- 2) The OPEX budget exactly as it stood at a point in time (UTC)
   SELECT b.*
   FROM core.BudgetLines FOR SYSTEM_TIME AS OF '2026-11-05T09:00:00' AS b
   JOIN core.Categories c ON c.CategoryId = b.CategoryId
   WHERE b.BudgetYear = 2027 AND b.EntityId = 4 AND c.CategoryCode = 'OPEX';

   -- 3) Budget lines changed or removed in a time window
   SELECT b.BudgetLineId, b.Description, b.Amount, b.ValidFrom, b.ValidTo
   FROM core.BudgetLines FOR SYSTEM_TIME BETWEEN '2026-11-01' AND '2026-11-06' AS b
   WHERE b.BudgetYear = 2027
   ORDER BY b.BudgetLineId, b.ValidFrom;
   --------------------------------------------------------------------------------------------- */
