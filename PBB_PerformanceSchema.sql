/*
    PBB Performance Layer - ADDITIVE schema migration
    --------------------------------------------------
    Adds the performance/maturity tables required by the management
    "PBB Cross-Entity Performance & Maturity Review" deck.

    SAFE / ADDITIVE: creates new tables in schema [core] only.
    Does NOT alter BudgetLines, BudgetSubmissions, HR, or any existing object.
    Idempotent - safe to run multiple times.
*/
USE db_ac6910_govbudget;

IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'core')
BEGIN
    EXEC('CREATE SCHEMA [core]')
END

/* 1) Cost-shape mapping (Phase 0 R1): GL code/keyword -> bucket */
IF OBJECT_ID(N'core.CostShapeMap', N'U') IS NULL
BEGIN
    PRINT 'Creating core.CostShapeMap...'
    CREATE TABLE core.CostShapeMap (
        CostShapeMapId INT IDENTITY(1,1) PRIMARY KEY,
        GLCode         NVARCHAR(30)  NULL,   -- exact GL match (optional)
        MatchKeyword   NVARCHAR(100) NULL,   -- GL-name keyword match (optional)
        Bucket         NVARCHAR(30)  NOT NULL, -- Manpower|Capital|Consultancy|Maintenance|Other
        Priority       INT           NOT NULL DEFAULT(100),
        IsActive       BIT           NOT NULL DEFAULT(1)
    );
END
ELSE PRINT 'core.CostShapeMap already exists.'

/* 2) Activity outputs (Phase 0 R6): output volume -> cost-per-output */
IF OBJECT_ID(N'core.ActivityOutputs', N'U') IS NULL
BEGIN
    PRINT 'Creating core.ActivityOutputs...'
    CREATE TABLE core.ActivityOutputs (
        ActivityOutputId BIGINT IDENTITY(1,1) PRIMARY KEY,
        ActivityId       INT           NOT NULL,
        BudgetYear       INT           NOT NULL,
        OutputMeasure    NVARCHAR(200) NOT NULL,
        OutputVolume     DECIMAL(18,4) NOT NULL DEFAULT(0),
        IsPrimary        BIT           NOT NULL DEFAULT(1),
        CreatedAt        DATETIME2(0)  NOT NULL DEFAULT(SYSUTCDATETIME()),
        CreatedBy        NVARCHAR(100) NULL,
        CONSTRAINT UQ_ActivityOutputs UNIQUE (ActivityId, BudgetYear, OutputMeasure),
        CONSTRAINT FK_ActivityOutputs_Activity FOREIGN KEY (ActivityId) REFERENCES core.Activities(ActivityId)
    );
END
ELSE PRINT 'core.ActivityOutputs already exists.'

/* 3) KPIs (Phase 0 R3): baseline -> target -> mid-year, status */
IF OBJECT_ID(N'core.Kpis', N'U') IS NULL
BEGIN
    PRINT 'Creating core.Kpis...'
    CREATE TABLE core.Kpis (
        KpiId          BIGINT IDENTITY(1,1) PRIMARY KEY,
        BudgetYear     INT           NOT NULL,
        Period         NVARCHAR(20)  NOT NULL DEFAULT('MidYear'),  -- MidYear|YearEnd
        EntityId       INT           NOT NULL,
        ProgramId      INT           NULL,
        ActivityId     INT           NULL,
        KpiName        NVARCHAR(300) NOT NULL,
        Unit           NVARCHAR(50)  NULL,           -- count|%|days|ratio...
        Direction      NVARCHAR(10)  NOT NULL DEFAULT('UP'), -- UP=higher better, DOWN=lower better
        Baseline       DECIMAL(18,4) NULL,
        Target         DECIMAL(18,4) NULL,
        ActualValue    DECIMAL(18,4) NULL,
        Status         NVARCHAR(20)  NULL,           -- Green|Watch|Behind (computed or set)
        CreatedAt      DATETIME2(0)  NOT NULL DEFAULT(SYSUTCDATETIME()),
        CreatedBy      NVARCHAR(100) NULL,
        CONSTRAINT FK_Kpis_Entity   FOREIGN KEY (EntityId)   REFERENCES core.Entities(EntityId),
        CONSTRAINT FK_Kpis_Program  FOREIGN KEY (ProgramId)  REFERENCES core.Programs(ProgramId),
        CONSTRAINT FK_Kpis_Activity FOREIGN KEY (ActivityId) REFERENCES core.Activities(ActivityId)
    );
    CREATE INDEX IX_Kpis_Scope ON core.Kpis(BudgetYear, EntityId, Period);
END
ELSE PRINT 'core.Kpis already exists.'

/* 4) KPI <-> cost linkage (Phase 0 R3/deck): cost per unit improvement */
IF OBJECT_ID(N'core.KpiCostLinks', N'U') IS NULL
BEGIN
    PRINT 'Creating core.KpiCostLinks...'
    CREATE TABLE core.KpiCostLinks (
        KpiCostLinkId BIGINT IDENTITY(1,1) PRIMARY KEY,
        KpiId         BIGINT NOT NULL,
        ActivityId    INT    NULL,
        ProgramId     INT    NULL,
        WeightPct     DECIMAL(9,4) NOT NULL DEFAULT(100),
        CONSTRAINT FK_KpiCostLinks_Kpi      FOREIGN KEY (KpiId)      REFERENCES core.Kpis(KpiId) ON DELETE CASCADE,
        CONSTRAINT FK_KpiCostLinks_Activity FOREIGN KEY (ActivityId) REFERENCES core.Activities(ActivityId),
        CONSTRAINT FK_KpiCostLinks_Program  FOREIGN KEY (ProgramId)  REFERENCES core.Programs(ProgramId)
    );
END
ELSE PRINT 'core.KpiCostLinks already exists.'

/* 5) Maturity assessments (Phase 0 R4): Stage 1.0-4.0, OECD 4-form */
IF OBJECT_ID(N'core.MaturityAssessments', N'U') IS NULL
BEGIN
    PRINT 'Creating core.MaturityAssessments...'
    CREATE TABLE core.MaturityAssessments (
        MaturityAssessmentId INT IDENTITY(1,1) PRIMARY KEY,
        EntityId    INT           NOT NULL,
        BudgetYear  INT           NOT NULL,
        Period      NVARCHAR(20)  NOT NULL DEFAULT('MidYear'),
        Stage       DECIMAL(3,1)  NOT NULL DEFAULT(1.0),   -- 1.0 - 4.0
        Form        NVARCHAR(40)  NULL,  -- Presentational|Performance-Informed|Managerial|Direct
        StatusLabel NVARCHAR(20)  NULL,  -- AHEAD|MIXED|BEHIND
        Notes       NVARCHAR(1000) NULL,
        AssessedAt  DATETIME2(0)  NOT NULL DEFAULT(SYSUTCDATETIME()),
        AssessedBy  NVARCHAR(100) NULL,
        CONSTRAINT UQ_MaturityAssessments UNIQUE (EntityId, BudgetYear, Period),
        CONSTRAINT FK_MaturityAssessments_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId)
    );
END
ELSE PRINT 'core.MaturityAssessments already exists.'

/* 6) Entity review notes / key outcomes (Phase 0 R4/profile slides) */
IF OBJECT_ID(N'core.EntityReviewNotes', N'U') IS NULL
BEGIN
    PRINT 'Creating core.EntityReviewNotes...'
    CREATE TABLE core.EntityReviewNotes (
        EntityReviewNoteId INT IDENTITY(1,1) PRIMARY KEY,
        EntityId   INT           NOT NULL,
        BudgetYear INT           NOT NULL,
        Period     NVARCHAR(20)  NOT NULL DEFAULT('MidYear'),
        NoteType   NVARCHAR(30)  NOT NULL DEFAULT('Outcome'), -- Assessment|Outcome|Issue
        Body       NVARCHAR(MAX) NULL,
        SortOrder  INT           NOT NULL DEFAULT(0),
        CreatedAt  DATETIME2(0)  NOT NULL DEFAULT(SYSUTCDATETIME()),
        CreatedBy  NVARCHAR(100) NULL,
        CONSTRAINT FK_EntityReviewNotes_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId)
    );
END
ELSE PRINT 'core.EntityReviewNotes already exists.'

/* 6b) Review narratives (deck editorial slides: Headline Findings, Recommendations, 90-Day Plan) - cross-entity */
IF OBJECT_ID(N'core.ReviewNarratives', N'U') IS NULL
BEGIN
    PRINT 'Creating core.ReviewNarratives...'
    CREATE TABLE core.ReviewNarratives (
        ReviewNarrativeId INT IDENTITY(1,1) PRIMARY KEY,
        BudgetYear     INT           NOT NULL,
        Period         NVARCHAR(20)  NOT NULL DEFAULT('MidYear'),
        Section        NVARCHAR(30)  NOT NULL,  -- Finding|Recommendation|Action
        Title          NVARCHAR(300) NULL,
        Body           NVARCHAR(MAX) NULL,
        Owner          NVARCHAR(200) NULL,      -- 90-day plan: action owner
        DueText        NVARCHAR(100) NULL,      -- 90-day plan: due (e.g. End Q3 FY25)
        SuccessMeasure NVARCHAR(500) NULL,      -- 90-day plan: success measure
        SortOrder      INT           NOT NULL DEFAULT(0),
        CreatedAt      DATETIME2(0)  NOT NULL DEFAULT(SYSUTCDATETIME()),
        CreatedBy      NVARCHAR(100) NULL
    );
    CREATE INDEX IX_ReviewNarratives_Scope ON core.ReviewNarratives(BudgetYear, Period, Section, SortOrder);
END
ELSE PRINT 'core.ReviewNarratives already exists.'

/* 7) Seed default cost-shape keyword mapping (Phase 0 R1 defaults) */
IF NOT EXISTS (SELECT 1 FROM core.CostShapeMap)
BEGIN
    PRINT 'Seeding default core.CostShapeMap rows...'
    INSERT INTO core.CostShapeMap (GLCode, MatchKeyword, Bucket, Priority) VALUES
        (NULL, 'consult',      'Consultancy', 10),
        (NULL, 'advisory',     'Consultancy', 10),
        (NULL, 'professional', 'Consultancy', 10),
        (NULL, 'maintenance',  'Maintenance', 20),
        (NULL, 'repair',       'Maintenance', 20),
        (NULL, 'upkeep',       'Maintenance', 20);
END
ELSE PRINT 'core.CostShapeMap already has rows - skipping seed.'

/* 8) Saved report-builder configurations (Reports module self-service) */
IF OBJECT_ID(N'core.SavedReports', N'U') IS NULL
BEGIN
    PRINT 'Creating core.SavedReports...'
    CREATE TABLE core.SavedReports (
        SavedReportId INT IDENTITY(1,1) PRIMARY KEY,
        OwnerUser     NVARCHAR(256) NOT NULL,   -- owning user (User.Identity.Name)
        Name          NVARCHAR(150) NOT NULL,
        BudgetYear    INT           NULL,
        EntityId      INT           NULL,
        RowDim        NVARCHAR(30)  NOT NULL,
        ColDim        NVARCHAR(30)  NULL,
        Measure       NVARCHAR(30)  NOT NULL,
        Category      NVARCHAR(50)  NULL,
        IncludeHr     BIT           NOT NULL DEFAULT(0),
        ChartType     NVARCHAR(20)  NOT NULL DEFAULT('table'),
        CreatedAt     DATETIME2(0)  NOT NULL DEFAULT(SYSUTCDATETIME())
    );
    CREATE INDEX IX_SavedReports_Owner ON core.SavedReports(OwnerUser, Name);
END
ELSE PRINT 'core.SavedReports already exists.'

PRINT 'PBB performance layer schema check complete.'
