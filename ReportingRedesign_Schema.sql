/*
    Reporting Redesign - ADDITIVE schema migration
    ----------------------------------------------
    Adds:
      - Program classification (Mandate/Support) + step-down allocation order
      - Saved-report filter columns (category include/exclude, program type, cost basis)
      - Cost reallocation engine tables: drivers, driver values, rules, rule targets,
        allocation runs, and the allocation transactions ledger.

    SAFE / ADDITIVE: only ALTERs add nullable/defaulted columns; only CREATEs new tables.
    Idempotent - safe to run multiple times. No GO batch separators (web SQL tool).
*/
USE db_ac6910_govbudget;

/* ---------- 1) Program classification ---------- */
IF COL_LENGTH('core.Programs', 'ProgramType') IS NULL
BEGIN
    PRINT 'Adding core.Programs.ProgramType...'
    ALTER TABLE core.Programs ADD ProgramType NVARCHAR(20) NOT NULL CONSTRAINT DF_Programs_ProgramType DEFAULT('Mandate');
END
ELSE PRINT 'core.Programs.ProgramType already exists.'

IF COL_LENGTH('core.Programs', 'AllocationSequence') IS NULL
BEGIN
    PRINT 'Adding core.Programs.AllocationSequence...'
    ALTER TABLE core.Programs ADD AllocationSequence INT NULL;
END
ELSE PRINT 'core.Programs.AllocationSequence already exists.'

/* ---------- 2) Saved-report filter columns ---------- */
IF OBJECT_ID(N'core.SavedReports', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('core.SavedReports', 'CategoryMode') IS NULL
        ALTER TABLE core.SavedReports ADD CategoryMode NVARCHAR(10) NOT NULL CONSTRAINT DF_SavedReports_CategoryMode DEFAULT('Include');
    IF COL_LENGTH('core.SavedReports', 'CategoriesCsv') IS NULL
        ALTER TABLE core.SavedReports ADD CategoriesCsv NVARCHAR(400) NULL;
    IF COL_LENGTH('core.SavedReports', 'ProgramTypeFilter') IS NULL
        ALTER TABLE core.SavedReports ADD ProgramTypeFilter NVARCHAR(20) NULL;
    IF COL_LENGTH('core.SavedReports', 'CostBasis') IS NULL
        ALTER TABLE core.SavedReports ADD CostBasis NVARCHAR(20) NOT NULL CONSTRAINT DF_SavedReports_CostBasis DEFAULT('Direct');
END
ELSE PRINT 'core.SavedReports does not exist yet (run PBB_PerformanceSchema.sql first).'

/* ---------- 3) Allocation drivers (lookup) ---------- */
IF OBJECT_ID(N'core.AllocationDrivers', N'U') IS NULL
BEGIN
    PRINT 'Creating core.AllocationDrivers...'
    CREATE TABLE core.AllocationDrivers (
        DriverId   INT IDENTITY(1,1) PRIMARY KEY,
        DriverCode NVARCHAR(40)  NOT NULL,   -- HEADCOUNT | FLOOR_AREA | TXN_VOLUME | ...
        DriverName NVARCHAR(120) NOT NULL,
        Unit       NVARCHAR(40)  NULL,
        IsActive   BIT           NOT NULL DEFAULT(1)
    );
    CREATE UNIQUE INDEX UX_AllocationDrivers_Code ON core.AllocationDrivers(DriverCode);

    INSERT INTO core.AllocationDrivers (DriverCode, DriverName, Unit) VALUES
        ('HEADCOUNT',  'Headcount / FTE',   'FTE'),
        ('FLOOR_AREA', 'Floor area',        'sqm'),
        ('TXN_VOLUME', 'Transaction volume','count');
END
ELSE PRINT 'core.AllocationDrivers already exists.'

/* ---------- 4) Driver values (per target program/activity per year) ---------- */
IF OBJECT_ID(N'core.AllocationDriverValues', N'U') IS NULL
BEGIN
    PRINT 'Creating core.AllocationDriverValues...'
    CREATE TABLE core.AllocationDriverValues (
        DriverValueId   INT IDENTITY(1,1) PRIMARY KEY,
        DriverId        INT NOT NULL,
        BudgetYear      INT NOT NULL,
        TargetProgramId INT NOT NULL,
        TargetActivityId INT NULL,
        Value           DECIMAL(18,4) NOT NULL DEFAULT(0),
        CONSTRAINT FK_DriverValues_Driver  FOREIGN KEY (DriverId)        REFERENCES core.AllocationDrivers(DriverId),
        CONSTRAINT FK_DriverValues_Program FOREIGN KEY (TargetProgramId) REFERENCES core.Programs(ProgramId)
    );
    CREATE INDEX IX_DriverValues_Lookup ON core.AllocationDriverValues(BudgetYear, DriverId, TargetProgramId);
END
ELSE PRINT 'core.AllocationDriverValues already exists.'

/* ---------- 5) Allocation rules (configuration) ---------- */
IF OBJECT_ID(N'core.AllocationRules', N'U') IS NULL
BEGIN
    PRINT 'Creating core.AllocationRules...'
    CREATE TABLE core.AllocationRules (
        RuleId          INT IDENTITY(1,1) PRIMARY KEY,
        BudgetYear      INT NOT NULL,
        EntityId        INT NOT NULL,
        SourceProgramId INT NOT NULL,                 -- a Support program
        SourceActivityId INT NULL,                    -- optional finer source grain
        Method          NVARCHAR(20) NOT NULL,        -- Percentage | Headcount | Driver | Equal
        DriverId        INT NULL,                      -- required when Method = Driver/Headcount
        CategoryScopeCsv NVARCHAR(200) NOT NULL DEFAULT('OPEX,HR'),  -- which categories to reallocate
        TargetScope     NVARCHAR(20) NOT NULL DEFAULT('AllMandate'), -- AllMandate | Explicit
        SourcePercent   DECIMAL(9,4) NOT NULL DEFAULT(100), -- share of pool to push out
        Sequence        INT NOT NULL DEFAULT(100),
        IsActive        BIT NOT NULL DEFAULT(1),
        CreatedAt       DATETIME2(0) NOT NULL DEFAULT(SYSUTCDATETIME()),
        CreatedBy       NVARCHAR(100) NULL,
        CONSTRAINT FK_AllocRules_Source FOREIGN KEY (SourceProgramId) REFERENCES core.Programs(ProgramId),
        CONSTRAINT FK_AllocRules_Driver FOREIGN KEY (DriverId)        REFERENCES core.AllocationDrivers(DriverId)
    );
    CREATE INDEX IX_AllocRules_Scope ON core.AllocationRules(BudgetYear, EntityId, IsActive);
END
ELSE PRINT 'core.AllocationRules already exists.'

/* ---------- 6) Explicit rule targets / weights ---------- */
IF OBJECT_ID(N'core.AllocationRuleTargets', N'U') IS NULL
BEGIN
    PRINT 'Creating core.AllocationRuleTargets...'
    CREATE TABLE core.AllocationRuleTargets (
        RuleTargetId    INT IDENTITY(1,1) PRIMARY KEY,
        RuleId          INT NOT NULL,
        TargetProgramId INT NOT NULL,
        TargetActivityId INT NULL,
        Weight          DECIMAL(9,4) NOT NULL DEFAULT(0),  -- used by Method = Percentage
        CONSTRAINT FK_RuleTargets_Rule    FOREIGN KEY (RuleId)          REFERENCES core.AllocationRules(RuleId) ON DELETE CASCADE,
        CONSTRAINT FK_RuleTargets_Program FOREIGN KEY (TargetProgramId) REFERENCES core.Programs(ProgramId)
    );
    CREATE INDEX IX_RuleTargets_Rule ON core.AllocationRuleTargets(RuleId);
END
ELSE PRINT 'core.AllocationRuleTargets already exists.'

/* ---------- 7) Allocation runs (snapshot/audit header) ---------- */
IF OBJECT_ID(N'core.AllocationRuns', N'U') IS NULL
BEGIN
    PRINT 'Creating core.AllocationRuns...'
    CREATE TABLE core.AllocationRuns (
        RunId      INT IDENTITY(1,1) PRIMARY KEY,
        BudgetYear INT NOT NULL,
        EntityId   INT NULL,                      -- NULL = all entities in the run
        Period     NVARCHAR(20) NOT NULL DEFAULT('Annual'),
        Status     NVARCHAR(20) NOT NULL DEFAULT('Draft'),  -- Draft | Posted | Superseded
        Method     NVARCHAR(20) NOT NULL DEFAULT('StepDown'),
        RunAt      DATETIME2(0) NOT NULL DEFAULT(SYSUTCDATETIME()),
        RunBy      NVARCHAR(100) NULL,
        Notes      NVARCHAR(500) NULL,
        ReconciledOk BIT NOT NULL DEFAULT(0)
    );
    CREATE INDEX IX_AllocRuns_Scope ON core.AllocationRuns(BudgetYear, EntityId, Status);
END
ELSE PRINT 'core.AllocationRuns already exists.'

/* ---------- 8) Allocation transactions (immutable results ledger) ---------- */
IF OBJECT_ID(N'core.AllocationTransactions', N'U') IS NULL
BEGIN
    PRINT 'Creating core.AllocationTransactions...'
    CREATE TABLE core.AllocationTransactions (
        TxnId            BIGINT IDENTITY(1,1) PRIMARY KEY,
        RunId            INT NOT NULL,
        BudgetYear       INT NOT NULL,
        Period           NVARCHAR(20) NOT NULL DEFAULT('Annual'),
        EntityId         INT NOT NULL,
        SourceProgramId  INT NOT NULL,
        SourceActivityId INT NULL,
        SourceCategoryCode NVARCHAR(50) NULL,
        TargetProgramId  INT NOT NULL,
        TargetActivityId INT NULL,
        DriverId         INT NULL,
        BasisValue       DECIMAL(18,4) NOT NULL DEFAULT(0),
        BasisTotal       DECIMAL(18,4) NOT NULL DEFAULT(0),
        AllocationPct    DECIMAL(9,6)  NOT NULL DEFAULT(0),
        Amount           DECIMAL(18,2) NOT NULL DEFAULT(0),
        CreatedAt        DATETIME2(0)  NOT NULL DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_AllocTxn_Run FOREIGN KEY (RunId) REFERENCES core.AllocationRuns(RunId)
    );
    CREATE INDEX IX_AllocTxn_Target ON core.AllocationTransactions(BudgetYear, TargetProgramId);
    CREATE INDEX IX_AllocTxn_Source ON core.AllocationTransactions(BudgetYear, SourceProgramId);
    CREATE INDEX IX_AllocTxn_Run    ON core.AllocationTransactions(RunId);
END
ELSE PRINT 'core.AllocationTransactions already exists.'

PRINT 'Reporting redesign schema check complete.'
