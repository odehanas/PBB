/*
    Budget vs Actual (current-year actuals) - ADDITIVE schema migration
    -------------------------------------------------------------------
    Supports importing current-year ACTUALS exported from SAP (GL view),
    plus a manual "forecast-to-complete" for the remaining period, so the
    system can produce a full-year Budget vs Actual comparison.

    Grain: monthly (PeriodMonth 1-12).
    Reliable comparison levels: GL -> Category -> Item.
    Activity/Project/Department are DERIVED (budget-share) in the reporting
    layer, except HR which is exact (employee actual x budgeted allocation rate).

    SAFE / ADDITIVE: creates new tables in schema [core] only.
    Does NOT alter BudgetLines, HR, MidYearGlActualForecasts or any existing object.
    Idempotent - safe to run multiple times. No GO batch separators.
*/
-- Run against the target database (select it in the SSMS dropdown, or the
-- app connection string's Initial Catalog). Portable across local/hosted:
-- USE db_ac6910_govbudget;   -- hosted
-- USE GovBudgetDB;           -- local

IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'core')
BEGIN
    EXEC('CREATE SCHEMA [core]')
END

/* 1) Import batches - audit + overwrite scoping for each uploaded file */
IF OBJECT_ID(N'core.ActualImportBatches', N'U') IS NULL
BEGIN
    PRINT 'Creating core.ActualImportBatches...'
    CREATE TABLE core.ActualImportBatches (
        ActualImportBatchId BIGINT IDENTITY(1,1) PRIMARY KEY,
        BudgetYear          INT           NOT NULL,
        EntityId            INT           NOT NULL,
        Source              NVARCHAR(10)  NOT NULL,   -- SAP_GL | SAP_MM | HR
        PeriodFrom          TINYINT       NULL,        -- first month covered (1-12)
        PeriodTo            TINYINT       NULL,        -- last month covered (1-12)
        RowsImported        INT           NOT NULL DEFAULT(0),
        TotalAmount         DECIMAL(18,2) NOT NULL DEFAULT(0),
        SourceFile          NVARCHAR(260) NULL,
        ImportedAt          DATETIME2(0)  NOT NULL DEFAULT(SYSUTCDATETIME()),
        ImportedBy          NVARCHAR(100) NULL,
        CONSTRAINT FK_ActualImportBatches_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId)
    );
END
ELSE PRINT 'core.ActualImportBatches already exists.'

/* 2) Actual postings - the actuals fact table (monthly grain) */
IF OBJECT_ID(N'core.ActualPostings', N'U') IS NULL
BEGIN
    PRINT 'Creating core.ActualPostings...'
    CREATE TABLE core.ActualPostings (
        ActualPostingId BIGINT IDENTITY(1,1) PRIMARY KEY,
        BudgetYear      INT           NOT NULL,
        PeriodMonth     TINYINT       NOT NULL,   -- 1-12
        EntityId        INT           NOT NULL,
        GLCode          NVARCHAR(30)  NOT NULL,
        GLType          NVARCHAR(20)  NULL,        -- REVENUE|OPEX|CAPEX|HR (denormalised for reporting)
        ItemId          INT           NULL,        -- resolved from material/item code (NULL for GL-only lines)
        ItemCode        NVARCHAR(50)  NULL,        -- raw code from the file, kept for traceability
        Amount          DECIMAL(18,2) NOT NULL DEFAULT(0),
        Source          NVARCHAR(10)  NOT NULL,    -- SAP_GL | SAP_MM | HR
        ImportBatchId   BIGINT        NULL,
        SourceFile      NVARCHAR(260) NULL,
        CreatedAt       DATETIME2(0)  NOT NULL DEFAULT(SYSUTCDATETIME()),
        CreatedBy       NVARCHAR(100) NULL,
        CONSTRAINT FK_ActualPostings_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId),
        CONSTRAINT FK_ActualPostings_Item   FOREIGN KEY (ItemId)   REFERENCES core.Items(ItemId),
        CONSTRAINT FK_ActualPostings_Batch  FOREIGN KEY (ImportBatchId) REFERENCES core.ActualImportBatches(ActualImportBatchId)
    );
    CREATE INDEX IX_ActualPostings_Scope ON core.ActualPostings (BudgetYear, EntityId, GLCode, PeriodMonth);
    CREATE INDEX IX_ActualPostings_Item  ON core.ActualPostings (BudgetYear, EntityId, ItemId);
END
ELSE PRINT 'core.ActualPostings already exists.'

/* 3) Forecast-to-complete - manual forecast for the not-yet-actualised remainder.
      Reuses the mid-year forecast concept (one figure per GL/entity/year). */
IF OBJECT_ID(N'core.ActualForecasts', N'U') IS NULL
BEGIN
    PRINT 'Creating core.ActualForecasts...'
    CREATE TABLE core.ActualForecasts (
        ActualForecastId  BIGINT IDENTITY(1,1) PRIMARY KEY,
        BudgetYear        INT           NOT NULL,
        EntityId          INT           NOT NULL,
        GLCode            NVARCHAR(30)  NOT NULL,
        GLType            NVARCHAR(20)  NULL,
        AsOfMonth         TINYINT       NOT NULL DEFAULT(0),  -- last actual month the forecast complements
        ForecastRemaining DECIMAL(18,2) NOT NULL DEFAULT(0),  -- forecast for months AsOfMonth+1..12
        Notes             NVARCHAR(400) NULL,
        UpdatedAt         DATETIME2(0)  NOT NULL DEFAULT(SYSUTCDATETIME()),
        UpdatedBy         NVARCHAR(100) NULL,
        CONSTRAINT UQ_ActualForecasts UNIQUE (BudgetYear, EntityId, GLCode),
        CONSTRAINT FK_ActualForecasts_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId)
    );
END
ELSE PRINT 'core.ActualForecasts already exists.'

/* 4) HR employee actual postings - per-employee actuals (monthly grain).
      Enables EXACT activity/project attribution: employee actual x budgeted allocation rate.
      EmployeeCostId is a soft link to core.HrEmployeeCosts (resolved at import from the HR code). */
IF OBJECT_ID(N'core.HrActualPostings', N'U') IS NULL
BEGIN
    PRINT 'Creating core.HrActualPostings...'
    CREATE TABLE core.HrActualPostings (
        HrActualPostingId BIGINT IDENTITY(1,1) PRIMARY KEY,
        BudgetYear      INT           NOT NULL,
        PeriodMonth     TINYINT       NOT NULL,   -- 1-12
        EntityId        INT           NOT NULL,
        EmployeeCode    NVARCHAR(50)  NOT NULL,   -- raw HR code from the file (matches HrEmployeeCosts.EmployeeId)
        EmployeeCostId  INT           NULL,        -- resolved budgeted employee (NULL = unmatched)
        GLCode          NVARCHAR(30)  NULL,        -- denormalised salary GL (for GL/Category rollup)
        Amount          DECIMAL(18,2) NOT NULL DEFAULT(0),
        Source          NVARCHAR(10)  NOT NULL DEFAULT('HR_EMP'),
        ImportBatchId   BIGINT        NULL,
        SourceFile      NVARCHAR(260) NULL,
        CreatedAt       DATETIME2(0)  NOT NULL DEFAULT(SYSUTCDATETIME()),
        CreatedBy       NVARCHAR(100) NULL,
        CONSTRAINT FK_HrActualPostings_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId),
        CONSTRAINT FK_HrActualPostings_Batch  FOREIGN KEY (ImportBatchId) REFERENCES core.ActualImportBatches(ActualImportBatchId)
    );
    CREATE INDEX IX_HrActualPostings_Scope ON core.HrActualPostings (BudgetYear, EntityId, EmployeeCostId);
END
ELSE PRINT 'core.HrActualPostings already exists.'

PRINT 'ActualsComparison_Schema.sql complete.'
