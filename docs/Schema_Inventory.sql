/* ==========================================================================
   GovBudget - LIVE SCHEMA INVENTORY, DRIFT CHECK AND AZURE PRE-FLIGHT
   --------------------------------------------------------------------------
   Read-only. Selects from system catalogue views only: no data is read, nothing
   is written, nothing is locked. Safe to run against production.

   WHY THIS EXISTS
     docs/LocalDatabase_FullSchema.sql is maintained by hand and the
     application adds objects at start-up, so the repository script is a very
     good approximation of the live database but not a guaranteed match. Before
     rebuilding anywhere - Azure, EGA, or a local machine - confirm what the
     live database actually contains.

   HOW TO RUN
     SSMS or Azure Data Studio, connected to the live database.
     Press F5. Each section returns its own result grid.
     Right-click any grid > "Save Results As..." to export CSV.

   Section 1  Object summary
   Section 2  Drift check: expected vs actual objects   <- read this one first
   Section 3  Table inventory with row counts
   Section 4  Full column inventory (export this for comparison)
   Section 5  Indexes, primary keys and foreign keys
   Section 6  VIEW DEFINITIONS - the authoritative source for the views
   Section 7  Azure SQL Database pre-flight checks
   ========================================================================== */

SET NOCOUNT ON;

/* ==========================================================================
   1) OBJECT SUMMARY
   ========================================================================== */
SELECT
    N'1. Object summary'                    AS Section,
    s.name                                  AS SchemaName,
    o.type_desc                             AS ObjectType,
    COUNT(*)                                AS Objects
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.is_ms_shipped = 0
  AND o.type IN ('U','V','P','FN','IF','TF','TR')
GROUP BY s.name, o.type_desc
ORDER BY s.name, o.type_desc;

/* ==========================================================================
   2) DRIFT CHECK - what the repository script expects vs what is really there
      MISSING IN DATABASE = the live database lacks an object the app needs.
      NOT IN REPO SCRIPT  = the live database has an object the script would
                            not recreate, so a rebuild from the script alone
                            would lose it.
   ========================================================================== */
/* COLLATE DATABASE_DEFAULT on both sides of this comparison is required: the
   system catalogue columns carry the server catalogue collation, which on this
   database differs from the database default collation. Without it, the JOIN and
   the CASE below fail with "collation conflict" (error -2147217900). */
DECLARE @Expected TABLE (
    ObjectName SYSNAME COLLATE DATABASE_DEFAULT PRIMARY KEY,
    ObjectType CHAR(1) COLLATE DATABASE_DEFAULT
);

INSERT INTO @Expected (ObjectName, ObjectType) VALUES
    -- Master / reference
    (N'Entities','U'), (N'Departments','U'), (N'Programs','U'), (N'Activities','U'),
    (N'Projects','U'), (N'Categories','U'), (N'GLAccounts','U'), (N'Items','U'),
    -- Budget
    (N'BudgetLines','U'), (N'BudgetLineDocuments','U'),
    -- Submission workflow
    (N'BudgetSubmissions','U'), (N'BudgetSubmissionLines','U'),
    (N'BudgetRevisionRequests','U'), (N'DOF_CombindBudget_Final','U'),
    -- Security / audit / messaging
    (N'AppUsers','U'), (N'AppRoles','U'), (N'RolePermissions','U'),
    (N'AuditLogs','U'), (N'InternalMessages','U'), (N'PasswordResetRequests','U'),
    -- HR and actuals
    (N'HrEmployeeCosts','U'), (N'HrEmployeeCostAllocations','U'),
    (N'HistoricalGlActuals','U'), (N'MidYearGlActualForecasts','U'),
    -- What-if
    (N'WhatIfScenarios','U'), (N'WhatIfScenarioDefaults','U'), (N'WhatIfScenarioProjectRates','U'),
    -- Performance layer
    (N'Kpis','U'), (N'KpiCostLinks','U'), (N'ActivityOutputs','U'),
    (N'MaturityAssessments','U'), (N'EntityReviewNotes','U'), (N'ReviewNarratives','U'),
    (N'CostShapeMap','U'), (N'SavedReports','U'),
    -- Allocation engine
    (N'AllocationDrivers','U'), (N'AllocationDriverValues','U'), (N'AllocationRules','U'),
    (N'AllocationRuleTargets','U'), (N'AllocationRuns','U'), (N'AllocationTransactions','U'),
    -- Views
    (N'vw_GL_CashBasis','V'), (N'vw_CostByGL','V'), (N'vw_CostByActivity','V'),
    (N'vw_CostByActivity_AfterAllocation','V'), (N'vw_AllocatedCostByOccupation','V'),
    (N'vw_AllocatedCostByOccupation_Summary','V');

WITH Actual AS (
    SELECT o.name COLLATE DATABASE_DEFAULT                  AS ObjectName,
           CAST(o.type AS CHAR(1)) COLLATE DATABASE_DEFAULT AS ObjectType
    FROM sys.objects o
    JOIN sys.schemas s ON s.schema_id = o.schema_id
    WHERE o.is_ms_shipped = 0
      AND s.name = N'core'
      AND o.type IN ('U','V')
)
SELECT N'2. Drift check' AS Section,
       COALESCE(e.ObjectName, a.ObjectName)                       AS ObjectName,
       CASE WHEN COALESCE(e.ObjectType, a.ObjectType) = 'U'
            THEN N'table' ELSE N'view' END                        AS ObjectType,
       CASE
           WHEN a.ObjectName IS NULL THEN N'*** MISSING IN DATABASE ***'
           WHEN e.ObjectName IS NULL THEN N'NOT IN REPO SCRIPT (would be lost on rebuild)'
           ELSE N'OK'
       END                                                        AS Status
FROM @Expected e
FULL OUTER JOIN Actual a ON a.ObjectName = e.ObjectName
WHERE a.ObjectName IS NULL OR e.ObjectName IS NULL          -- comment out this line to list every object
ORDER BY Status DESC, ObjectName;

/* Objects outside the [core] schema - the app expects everything in [core]. */
SELECT N'2b. Objects outside [core]' AS Section,
       s.name AS SchemaName, o.name AS ObjectName, o.type_desc AS ObjectType
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.is_ms_shipped = 0
  AND o.type IN ('U','V','P','FN','IF','TF')
  AND s.name <> N'core'
ORDER BY s.name, o.name;

/* ==========================================================================
   3) TABLE INVENTORY WITH ROW COUNTS
      Row counts come from catalogue statistics, so no table scan occurs.
   ========================================================================== */
SELECT
    N'3. Tables' AS Section,
    s.name       AS SchemaName,
    t.name       AS TableName,
    (SELECT COUNT(*) FROM sys.columns c WHERE c.object_id = t.object_id) AS Cols,
    SUM(CASE WHEN p.index_id IN (0,1) THEN p.rows ELSE 0 END)            AS [Rows],
    CAST(SUM(a.total_pages) * 8.0 / 1024 AS DECIMAL(10,1))               AS SizeMB
FROM sys.tables t
JOIN sys.schemas s      ON s.schema_id = t.schema_id
JOIN sys.partitions p   ON p.object_id = t.object_id
JOIN sys.allocation_units a ON a.container_id = p.partition_id
GROUP BY s.name, t.name, t.object_id
ORDER BY SizeMB DESC, t.name;

/* Total database size - use this to size the target server. */
SELECT N'3b. Database size' AS Section,
       CAST(SUM(CASE WHEN type_desc = 'ROWS' THEN size END) * 8.0 / 1024 AS DECIMAL(10,1)) AS DataMB,
       CAST(SUM(CASE WHEN type_desc = 'LOG'  THEN size END) * 8.0 / 1024 AS DECIMAL(10,1)) AS LogMB
FROM sys.database_files;

/* ==========================================================================
   4) FULL COLUMN INVENTORY
      Export this grid to CSV. It is the definitive list to compare against
      docs/LocalDatabase_FullSchema.sql after any release.
   ========================================================================== */
SELECT
    t.name                                  AS TableName,
    c.column_id                             AS Ord,
    c.name                                  AS ColumnName,
    ty.name                                 AS DataType,
    CASE
        WHEN ty.name IN ('nvarchar','nchar') AND c.max_length = -1 THEN N'MAX'
        WHEN ty.name IN ('nvarchar','nchar') THEN CAST(c.max_length / 2 AS NVARCHAR(10))
        WHEN ty.name IN ('varchar','char','varbinary','binary') AND c.max_length = -1 THEN N'MAX'
        WHEN ty.name IN ('varchar','char','varbinary','binary') THEN CAST(c.max_length AS NVARCHAR(10))
        WHEN ty.name IN ('decimal','numeric') THEN CONCAT(c.precision, N',', c.scale)
        ELSE N''
    END                                     AS Length,
    CASE WHEN c.is_nullable = 1 THEN N'NULL' ELSE N'NOT NULL' END AS Nullable,
    CASE WHEN c.is_identity = 1 THEN N'IDENTITY' ELSE N'' END     AS Identity_,
    OBJECT_DEFINITION(c.default_object_id)  AS DefaultDefinition,
    CASE WHEN c.is_computed = 1 THEN N'COMPUTED' ELSE N'' END     AS Computed
FROM sys.columns c
JOIN sys.tables  t  ON t.object_id = c.object_id
JOIN sys.schemas s  ON s.schema_id = t.schema_id
JOIN sys.types   ty ON ty.user_type_id = c.user_type_id
WHERE s.name = N'core'
ORDER BY t.name, c.column_id;

/* ==========================================================================
   5) KEYS AND INDEXES
   ========================================================================== */
SELECT
    N'5a. Keys / indexes' AS Section,
    t.name  AS TableName,
    i.name  AS IndexName,
    i.type_desc AS IndexType,
    CASE WHEN i.is_primary_key = 1 THEN N'PK'
         WHEN i.is_unique_constraint = 1 THEN N'UQ'
         WHEN i.is_unique = 1 THEN N'UNIQUE'
         ELSE N'' END AS Kind,
    STUFF((SELECT N', ' + col.name
           FROM sys.index_columns ic
           JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
           WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
           ORDER BY ic.key_ordinal
           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, N'') AS KeyColumns
FROM sys.indexes i
JOIN sys.tables  t ON t.object_id = i.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = N'core' AND i.type > 0
ORDER BY t.name, Kind DESC, i.name;

SELECT
    N'5b. Foreign keys' AS Section,
    fk.name                     AS ForeignKey,
    pt.name                     AS ChildTable,
    rt.name                     AS ParentTable,
    fk.delete_referential_action_desc AS OnDelete,
    fk.is_not_trusted            AS NotTrusted
FROM sys.foreign_keys fk
JOIN sys.tables pt ON pt.object_id = fk.parent_object_id
JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
ORDER BY pt.name, fk.name;

/* ==========================================================================
   6) VIEW DEFINITIONS  *** THE IMPORTANT ONE ***
      The repository copy of core.vw_GL_CashBasis is a reconstruction. Export
      this grid, or right-click the view in SSMS > Script View as > CREATE To,
      and give the result to the development team so the repository can hold
      the real definition.
   ========================================================================== */
SELECT
    v.name          AS ViewName,
    LEN(m.definition) AS DefinitionLength,
    m.definition    AS ViewDefinition
FROM sys.views v
JOIN sys.sql_modules m ON m.object_id = v.object_id
JOIN sys.schemas s     ON s.schema_id = v.schema_id
WHERE s.name = N'core'
ORDER BY v.name;

/* Any stored procedures, functions or triggers - the repo script creates none,
   so anything listed here must be scripted separately before a rebuild. */
SELECT
    N'6b. Other code objects' AS Section,
    o.type_desc AS ObjectType,
    o.name      AS ObjectName,
    m.definition
FROM sys.sql_modules m
JOIN sys.objects o ON o.object_id = m.object_id
WHERE o.type IN ('P','FN','IF','TF','TR')
  AND o.is_ms_shipped = 0
ORDER BY o.type_desc, o.name;

/* ==========================================================================
   7) AZURE SQL DATABASE PRE-FLIGHT
      Each check should return NO ROWS. Anything returned needs attention
      before importing into Azure SQL Database.
   ========================================================================== */

/* 7a. Tables with no primary key - allowed, but they block some migration
       tooling and cannot be used with transactional replication. */
SELECT N'7a. Table without primary key' AS Finding, t.name AS ObjectName
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = N'core'
  AND NOT EXISTS (SELECT 1 FROM sys.indexes i
                  WHERE i.object_id = t.object_id AND i.is_primary_key = 1);

/* 7b. Cross-database or linked-server references in code - unsupported in
       Azure SQL Database. */
SELECT N'7b. Possible cross-database reference' AS Finding, o.name AS ObjectName
FROM sys.sql_modules m
JOIN sys.objects o ON o.object_id = m.object_id
WHERE m.definition LIKE N'%OPENQUERY%'
   OR m.definition LIKE N'%OPENROWSET%'
   OR m.definition LIKE N'%..[%'
   OR m.definition LIKE N'%master.dbo.%';

/* 7c. Features not available in Azure SQL Database. */
SELECT N'7c. CLR assembly present' AS Finding, name AS ObjectName FROM sys.assemblies WHERE is_user_defined = 1
UNION ALL
SELECT N'7c. FileStream / FileTable present', name FROM sys.tables WHERE is_filetable = 1
UNION ALL
SELECT N'7c. Full-text index present', CAST(OBJECT_NAME(object_id) AS SYSNAME) FROM sys.fulltext_indexes
UNION ALL
SELECT N'7c. Service Broker queue present', name FROM sys.service_queues WHERE is_ms_shipped = 0;

/* 7d. Collation - record it so the target database is created to match.
       IMPORTANT: if ServerDatabaseCollation below is not the default
       SQL_Latin1_General_CP1_CI_AS, the target database must be CREATED with
       that collation explicitly. Azure SQL and a fresh SQL Server install both
       default to SQL_Latin1_General_CP1_CI_AS, so a manually created target
       would silently differ from production. Importing a .bacpac preserves the
       collation automatically; creating the database by hand does not. */
SELECT N'7d. Collation' AS Finding,
       DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS ServerDatabaseCollation,
       SERVERPROPERTY('ProductVersion')           AS ProductVersion,
       SERVERPROPERTY('Edition')                  AS Edition,
       (SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()) AS CompatLevel;

/* 7e. Database users and roles to recreate on the target. */
SELECT N'7e. Database principals' AS Finding,
       dp.name AS PrincipalName, dp.type_desc AS PrincipalType,
       STUFF((SELECT N', ' + r.name
              FROM sys.database_role_members rm
              JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id
              WHERE rm.member_principal_id = dp.principal_id
              FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, N'') AS RoleMemberships
FROM sys.database_principals dp
WHERE dp.type IN ('S','U','G')
  AND dp.name NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys', N'public')
ORDER BY dp.name;
