/* ==========================================================================
   GovBudget - FULL SCHEMA (structure only, NO DATA)
   --------------------------------------------------------------------------
   Recreates the whole GovBudget database on a LOCAL SQL Server: every table,
   primary/foreign key, unique constraint, index and the BI views - but no
   business data. A tiny seed (the 3 budget Categories + a default 'admin'
   user) is included at the end so you can log in and use the app immediately;
   delete that section if you want a completely empty database.

   HOW TO RUN (SSMS / Azure Data Studio)
     1. Create an empty database first, e.g.:
            CREATE DATABASE GovBudgetDB;
        (or use the CREATE DATABASE block below - uncomment it).
     2. Select that database in the toolbar dropdown.
     3. Run this whole script (F5). It uses GO batch separators.

   Objects are created in dependency order, so it runs top-to-bottom cleanly
   on a brand-new empty database. Everything lives in the [core] schema.
   ========================================================================== */

/* --- Optional: create the database (uncomment to use) ---------------------
CREATE DATABASE GovBudgetDB;
GO
USE GovBudgetDB;
GO
--------------------------------------------------------------------------- */

/* 0) Schema ---------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'core')
    EXEC('CREATE SCHEMA [core]');
GO

/* ==========================================================================
   1) MASTER / REFERENCE TABLES  (no outbound FKs, created first)
   ========================================================================== */

/* Entities ----------------------------------------------------------------- */
CREATE TABLE core.Entities (
    EntityId   INT IDENTITY(1,1) NOT NULL,
    EntityCode NVARCHAR(20)  NOT NULL,
    EntityName NVARCHAR(200) NOT NULL,
    IsActive   BIT NOT NULL CONSTRAINT DF_Entities_IsActive DEFAULT(1),
    CONSTRAINT PK_Entities PRIMARY KEY (EntityId),
    CONSTRAINT UQ_Entities_EntityCode UNIQUE (EntityCode)
);
GO

/* Departments -------------------------------------------------------------- */
CREATE TABLE core.Departments (
    DepartmentId INT IDENTITY(1,1) NOT NULL,
    EntityId     INT NOT NULL,
    DeptCode     NVARCHAR(20)  NOT NULL,
    DeptName     NVARCHAR(200) NOT NULL,
    IsActive     BIT NOT NULL CONSTRAINT DF_Departments_IsActive DEFAULT(1),
    CONSTRAINT PK_Departments PRIMARY KEY (DepartmentId),
    CONSTRAINT UQ_Department UNIQUE (EntityId, DeptCode),
    CONSTRAINT FK_Department_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId)
);
GO

/* Programs ----------------------------------------------------------------- */
CREATE TABLE core.Programs (
    ProgramId          INT IDENTITY(1,1) NOT NULL,
    EntityId           INT NOT NULL,
    ProgramCode        NVARCHAR(30)  NOT NULL,
    ProgramName        NVARCHAR(200) NOT NULL,
    IsActive           BIT NOT NULL CONSTRAINT DF_Programs_IsActive DEFAULT(1),
    ProgramType        NVARCHAR(20)  NOT NULL CONSTRAINT DF_Programs_ProgramType DEFAULT('Mandate'),
    AllocationSequence INT NULL,
    CONSTRAINT PK_Programs PRIMARY KEY (ProgramId),
    CONSTRAINT UQ_Program UNIQUE (EntityId, ProgramCode),
    CONSTRAINT FK_Program_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId)
);
GO

/* Activities --------------------------------------------------------------- */
CREATE TABLE core.Activities (
    ActivityId   INT IDENTITY(1,1) NOT NULL,
    ProgramId    INT NOT NULL,
    DepartmentId INT NOT NULL,
    ActivityCode NVARCHAR(30)  NOT NULL,
    ActivityName NVARCHAR(200) NOT NULL,
    IsActive     BIT NOT NULL CONSTRAINT DF_Activities_IsActive DEFAULT(1),
    CONSTRAINT PK_Activities PRIMARY KEY (ActivityId),
    CONSTRAINT UQ_Activity UNIQUE (ProgramId, ActivityCode),
    CONSTRAINT FK_Activity_Program    FOREIGN KEY (ProgramId)    REFERENCES core.Programs(ProgramId),
    CONSTRAINT FK_Activity_Department FOREIGN KEY (DepartmentId) REFERENCES core.Departments(DepartmentId)
);
GO

/* Projects ----------------------------------------------------------------- */
CREATE TABLE core.Projects (
    ProjectId          INT IDENTITY(1,1) NOT NULL,
    ProjectCode        NVARCHAR(30)  NOT NULL,
    ProjectName        NVARCHAR(200) NOT NULL,
    OwningDepartmentId INT NULL,
    IsActive           BIT NOT NULL CONSTRAINT DF_Projects_IsActive DEFAULT(1),
    CONSTRAINT PK_Projects PRIMARY KEY (ProjectId),
    CONSTRAINT UQ_Projects_ProjectCode UNIQUE (ProjectCode),
    CONSTRAINT FK_Project_Department FOREIGN KEY (OwningDepartmentId) REFERENCES core.Departments(DepartmentId)
);
GO

/* Categories --------------------------------------------------------------- */
CREATE TABLE core.Categories (
    CategoryId   INT IDENTITY(1,1) NOT NULL,
    CategoryCode NVARCHAR(10) NOT NULL,
    CategoryName NVARCHAR(50) NOT NULL,
    CONSTRAINT PK_Categories PRIMARY KEY (CategoryId),
    CONSTRAINT UQ_Categories_CategoryCode UNIQUE (CategoryCode)
);
GO

/* GLAccounts --------------------------------------------------------------- */
CREATE TABLE core.GLAccounts (
    GLAccountId INT IDENTITY(1,1) NOT NULL,
    GLCode      NVARCHAR(30)  NOT NULL,
    GLName      NVARCHAR(200) NOT NULL,
    GLType      NVARCHAR(20)  NOT NULL,
    CONSTRAINT PK_GLAccounts PRIMARY KEY (GLAccountId),
    CONSTRAINT UQ_GLAccounts_GLCode UNIQUE (GLCode)
);
GO

/* Items -------------------------------------------------------------------- */
CREATE TABLE core.Items (
    ItemId      INT IDENTITY(1,1) NOT NULL,
    ItemCode    NVARCHAR(30)  NOT NULL,
    ItemName    NVARCHAR(200) NOT NULL,
    GLAccountId INT NOT NULL,
    IsActive    BIT NOT NULL CONSTRAINT DF_Items_IsActive DEFAULT(1),
    CONSTRAINT PK_Items PRIMARY KEY (ItemId),
    CONSTRAINT UQ_Items_ItemCode UNIQUE (ItemCode),
    CONSTRAINT FK_Item_GL FOREIGN KEY (GLAccountId) REFERENCES core.GLAccounts(GLAccountId)
);
GO

/* ==========================================================================
   2) BUDGET LINES (the main transactional table) + documents
   ========================================================================== */

CREATE TABLE core.BudgetLines (
    BudgetLineId     BIGINT IDENTITY(1,1) NOT NULL,
    BudgetYear       INT NOT NULL,
    EntityId         INT NOT NULL,
    DepartmentId     INT NOT NULL,
    CategoryId       INT NOT NULL,
    ItemId           INT NOT NULL,
    ProgramId        INT NULL,
    ActivityId       INT NULL,
    Quantity         DECIMAL(18,4) NOT NULL,
    UnitPrice        DECIMAL(18,4) NOT NULL,
    Amount           DECIMAL(18,2) NOT NULL,
    DistributionMode NVARCHAR(10) NOT NULL CONSTRAINT DF_BudgetLines_DistributionMode DEFAULT('EQUAL'),
    M01 DECIMAL(18,2) NOT NULL,
    M02 DECIMAL(18,2) NOT NULL,
    M03 DECIMAL(18,2) NOT NULL,
    M04 DECIMAL(18,2) NOT NULL,
    M05 DECIMAL(18,2) NOT NULL,
    M06 DECIMAL(18,2) NOT NULL,
    M07 DECIMAL(18,2) NOT NULL,
    M08 DECIMAL(18,2) NOT NULL,
    M09 DECIMAL(18,2) NOT NULL,
    M10 DECIMAL(18,2) NOT NULL,
    M11 DECIMAL(18,2) NOT NULL,
    M12 DECIMAL(18,2) NOT NULL,
    F1_Percent DECIMAL(9,4) NOT NULL,
    F1_Amount  DECIMAL(18,2) NOT NULL,
    F2_Percent DECIMAL(9,4) NOT NULL,
    F2_Amount  DECIMAL(18,2) NOT NULL,
    Dep_Method     NVARCHAR(20) NOT NULL CONSTRAINT DF_BudgetLines_DepMethod DEFAULT('STRAIGHT'),
    Dep_LifeMonths INT NOT NULL,
    Dep_StartMonth TINYINT NOT NULL CONSTRAINT DF_BudgetLines_DepStartMonth DEFAULT(1),
    CapexAssetType NVARCHAR(20) NULL,
    Notes          NVARCHAR(500) NULL,
    CreatedAt      DATETIME2(0) NOT NULL CONSTRAINT DF_BudgetLines_CreatedAt DEFAULT(SYSDATETIME()),
    CreatedBy      NVARCHAR(100) NULL,
    UpdatedAt      DATETIME2(0) NULL,
    UpdatedBy      NVARCHAR(100) NULL,
    EntrySource    NVARCHAR(10) NULL,     -- MANUAL | UPLOAD (NULL = legacy/manual)
    Description    NVARCHAR(300) NOT NULL CONSTRAINT DF_BudgetLines_Description DEFAULT(''),
    ProjectId      INT NULL,
    CONSTRAINT PK_BudgetLines PRIMARY KEY (BudgetLineId),
    CONSTRAINT FK_BL_Entity  FOREIGN KEY (EntityId)     REFERENCES core.Entities(EntityId),
    CONSTRAINT FK_BL_Dep     FOREIGN KEY (DepartmentId) REFERENCES core.Departments(DepartmentId),
    CONSTRAINT FK_BL_Cat     FOREIGN KEY (CategoryId)   REFERENCES core.Categories(CategoryId),
    CONSTRAINT FK_BL_Item    FOREIGN KEY (ItemId)       REFERENCES core.Items(ItemId),
    CONSTRAINT FK_BL_Prog    FOREIGN KEY (ProgramId)    REFERENCES core.Programs(ProgramId),
    CONSTRAINT FK_BL_Act     FOREIGN KEY (ActivityId)   REFERENCES core.Activities(ActivityId),
    CONSTRAINT FK_BL_Project FOREIGN KEY (ProjectId)    REFERENCES core.Projects(ProjectId)
);
GO
CREATE INDEX IX_BudgetLines_Scope ON core.BudgetLines(BudgetYear, EntityId, DepartmentId, CategoryId);
GO

/* One optional attachment per budget line (1:1). */
CREATE TABLE core.BudgetLineDocuments (
    BudgetLineId BIGINT NOT NULL,
    FileName     NVARCHAR(260) NOT NULL,
    ContentType  NVARCHAR(100) NOT NULL,
    SizeBytes    INT NOT NULL,
    Content      VARBINARY(MAX) NOT NULL,
    UploadedAt   DATETIME2 NOT NULL CONSTRAINT DF_BudgetLineDocuments_UploadedAt DEFAULT(SYSUTCDATETIME()),
    UploadedBy   NVARCHAR(100) NULL,
    CONSTRAINT PK_BudgetLineDocuments PRIMARY KEY (BudgetLineId),
    CONSTRAINT FK_BudgetLineDocuments_BudgetLines FOREIGN KEY (BudgetLineId)
        REFERENCES core.BudgetLines(BudgetLineId) ON DELETE CASCADE
);
GO

/* ==========================================================================
   3) BUDGET SUBMISSION WORKFLOW
   ========================================================================== */

CREATE TABLE core.BudgetSubmissions (
    SubmissionId       BIGINT IDENTITY(1,1) NOT NULL,
    BudgetYear         INT NOT NULL,
    EntityId           INT NOT NULL,
    DepartmentId       INT NOT NULL,
    CategoryId         INT NOT NULL,
    VersionNo          INT NOT NULL CONSTRAINT DF_BudgetSubmissions_VersionNo DEFAULT(1),
    ParentSubmissionId BIGINT NULL,
    Status             NVARCHAR(20) NOT NULL CONSTRAINT DF_BudgetSubmissions_Status DEFAULT('Draft'),
    SubmittedAt        DATETIME2 NULL,
    SubmittedBy        NVARCHAR(100) NULL,
    ApprovedAt         DATETIME2 NULL,
    ApprovedBy         NVARCHAR(100) NULL,
    ApprovalNote       NVARCHAR(500) NULL,
    ReturnedAt         DATETIME2 NULL,
    ReturnedBy         NVARCHAR(100) NULL,
    ReturnNote         NVARCHAR(500) NULL,
    SentToCentralAt    DATETIME2 NULL,
    SentToCentralBy    NVARCHAR(100) NULL,
    FinalizedAt        DATETIME2 NULL,
    FinalizedBy        NVARCHAR(100) NULL,
    SysApprovedAt      DATETIME2 NULL,
    SysApprovedBy      NVARCHAR(100) NULL,
    SysApprovalNote    NVARCHAR(500) NULL,
    CONSTRAINT PK_BudgetSubmissions PRIMARY KEY (SubmissionId),
    CONSTRAINT UQ_BudgetSubmissions_ScopeVersion UNIQUE (BudgetYear, EntityId, DepartmentId, CategoryId, VersionNo),
    CONSTRAINT FK_BudgetSubmissions_Entity     FOREIGN KEY (EntityId)           REFERENCES core.Entities(EntityId),
    CONSTRAINT FK_BudgetSubmissions_Department FOREIGN KEY (DepartmentId)       REFERENCES core.Departments(DepartmentId),
    CONSTRAINT FK_BudgetSubmissions_Category   FOREIGN KEY (CategoryId)         REFERENCES core.Categories(CategoryId),
    CONSTRAINT FK_BudgetSubmissions_Parent     FOREIGN KEY (ParentSubmissionId) REFERENCES core.BudgetSubmissions(SubmissionId)
);
GO

/* Immutable snapshot of the budget lines captured when a submission is sent. */
CREATE TABLE core.BudgetSubmissionLines (
    SubmissionLineId   BIGINT IDENTITY(1,1) NOT NULL,
    SubmissionId       BIGINT NOT NULL,
    SourceBudgetLineId BIGINT NOT NULL,
    BudgetYear   INT NOT NULL,
    EntityId     INT NOT NULL,
    DepartmentId INT NOT NULL,
    CategoryId   INT NOT NULL,
    ItemId       INT NOT NULL,
    ProgramId    INT NULL,
    ActivityId   INT NULL,
    ProjectId    INT NULL,
    Quantity  DECIMAL(18,4) NOT NULL,
    UnitPrice DECIMAL(18,4) NOT NULL,
    Amount    DECIMAL(18,2) NOT NULL,
    DistributionMode NVARCHAR(10) NOT NULL,
    M01 DECIMAL(18,2) NOT NULL, M02 DECIMAL(18,2) NOT NULL, M03 DECIMAL(18,2) NOT NULL,
    M04 DECIMAL(18,2) NOT NULL, M05 DECIMAL(18,2) NOT NULL, M06 DECIMAL(18,2) NOT NULL,
    M07 DECIMAL(18,2) NOT NULL, M08 DECIMAL(18,2) NOT NULL, M09 DECIMAL(18,2) NOT NULL,
    M10 DECIMAL(18,2) NOT NULL, M11 DECIMAL(18,2) NOT NULL, M12 DECIMAL(18,2) NOT NULL,
    F1_Percent DECIMAL(9,4) NOT NULL,
    F1_Amount  DECIMAL(18,2) NOT NULL,
    F2_Percent DECIMAL(9,4) NOT NULL,
    F2_Amount  DECIMAL(18,2) NOT NULL,
    Dep_Method     NVARCHAR(20) NOT NULL,
    Dep_LifeMonths INT NOT NULL,
    Dep_StartMonth TINYINT NOT NULL,
    CapexAssetType NVARCHAR(20) NULL,
    Notes          NVARCHAR(500) NULL,
    Description    NVARCHAR(300) NOT NULL,
    CreatedAt      DATETIME2 NOT NULL,
    CreatedBy      NVARCHAR(100) NULL,
    UpdatedAt      DATETIME2 NULL,
    UpdatedBy      NVARCHAR(100) NULL,
    DocFileName    NVARCHAR(260) NULL,
    DocContentType NVARCHAR(100) NULL,
    DocSizeBytes   INT NULL,
    DocContent     VARBINARY(MAX) NULL,
    DocUploadedAt  DATETIME2 NULL,
    DocUploadedBy  NVARCHAR(100) NULL,
    SnapshottedAt  DATETIME2 NOT NULL CONSTRAINT DF_BudgetSubmissionLines_SnapshottedAt DEFAULT(SYSUTCDATETIME()),
    SnapshottedBy  NVARCHAR(100) NULL,
    CONSTRAINT PK_BudgetSubmissionLines PRIMARY KEY (SubmissionLineId),
    CONSTRAINT UQ_BudgetSubmissionLines UNIQUE (SubmissionId, SourceBudgetLineId),
    CONSTRAINT FK_BudgetSubmissionLines_Submission FOREIGN KEY (SubmissionId) REFERENCES core.BudgetSubmissions(SubmissionId)
);
GO

CREATE TABLE core.BudgetRevisionRequests (
    RequestId    BIGINT IDENTITY(1,1) NOT NULL,
    SubmissionId BIGINT NOT NULL,
    ActionType   NVARCHAR(20) NOT NULL,
    Note         NVARCHAR(500) NULL,
    RequestedAt  DATETIME2 NOT NULL CONSTRAINT DF_BudgetRevisionRequests_RequestedAt DEFAULT(SYSUTCDATETIME()),
    RequestedBy  NVARCHAR(100) NULL,
    CONSTRAINT PK_BudgetRevisionRequests PRIMARY KEY (RequestId),
    CONSTRAINT FK_BudgetRevisionRequests_Submission FOREIGN KEY (SubmissionId) REFERENCES core.BudgetSubmissions(SubmissionId)
);
GO

/* Central finance "combined final" budget (copied from approved submissions).
   Not an EF entity, but part of the production database. */
CREATE TABLE core.DOF_CombindBudget_Final (
    FinalBudgetLineId  BIGINT IDENTITY(1,1) NOT NULL,
    SubmissionId       BIGINT NOT NULL,
    SourceBudgetLineId BIGINT NOT NULL,
    BudgetYear   INT NOT NULL,
    EntityId     INT NOT NULL,
    DepartmentId INT NOT NULL,
    CategoryId   INT NOT NULL,
    ItemId       INT NOT NULL,
    ProgramId    INT NULL,
    ActivityId   INT NULL,
    ProjectId    INT NULL,
    Quantity  DECIMAL(18,4) NOT NULL,
    UnitPrice DECIMAL(18,4) NOT NULL,
    Amount    DECIMAL(18,2) NOT NULL,
    DistributionMode NVARCHAR(10) NOT NULL,
    M01 DECIMAL(18,2) NOT NULL, M02 DECIMAL(18,2) NOT NULL, M03 DECIMAL(18,2) NOT NULL,
    M04 DECIMAL(18,2) NOT NULL, M05 DECIMAL(18,2) NOT NULL, M06 DECIMAL(18,2) NOT NULL,
    M07 DECIMAL(18,2) NOT NULL, M08 DECIMAL(18,2) NOT NULL, M09 DECIMAL(18,2) NOT NULL,
    M10 DECIMAL(18,2) NOT NULL, M11 DECIMAL(18,2) NOT NULL, M12 DECIMAL(18,2) NOT NULL,
    F1_Percent DECIMAL(9,4) NOT NULL,
    F1_Amount  DECIMAL(18,2) NOT NULL,
    F2_Percent DECIMAL(9,4) NOT NULL,
    F2_Amount  DECIMAL(18,2) NOT NULL,
    Dep_Method     NVARCHAR(20) NOT NULL,
    Dep_LifeMonths INT NOT NULL,
    Dep_StartMonth TINYINT NOT NULL,
    CapexAssetType NVARCHAR(20) NULL,
    Notes          NVARCHAR(500) NULL,
    Description    NVARCHAR(300) NOT NULL,
    CreatedAt      DATETIME2 NOT NULL,
    CreatedBy      NVARCHAR(100) NULL,
    UpdatedAt      DATETIME2 NULL,
    UpdatedBy      NVARCHAR(100) NULL,
    DocFileName    NVARCHAR(260) NULL,
    DocContentType NVARCHAR(100) NULL,
    DocSizeBytes   INT NULL,
    DocContent     VARBINARY(MAX) NULL,
    DocUploadedAt  DATETIME2 NULL,
    DocUploadedBy  NVARCHAR(100) NULL,
    ApprovedAt     DATETIME2 NULL,
    ApprovedBy     NVARCHAR(100) NULL,
    ApprovalNote   NVARCHAR(500) NULL,
    CopiedAt       DATETIME2 NOT NULL CONSTRAINT DF_DOF_CombindBudget_Final_CopiedAt DEFAULT(SYSUTCDATETIME()),
    CONSTRAINT PK_DOF_CombindBudget_Final PRIMARY KEY (FinalBudgetLineId),
    CONSTRAINT UQ_DOF_CombindBudget_Final UNIQUE (SubmissionId, SourceBudgetLineId),
    CONSTRAINT FK_Final_Submission FOREIGN KEY (SubmissionId) REFERENCES core.BudgetSubmissions(SubmissionId)
);
GO

/* ==========================================================================
   4) SECURITY / AUDIT / MESSAGING
   ========================================================================== */

CREATE TABLE core.AppUsers (
    UserId       INT IDENTITY(1,1) NOT NULL,
    UserName     NVARCHAR(100) NOT NULL,
    Password     NVARCHAR(128) NULL,
    Role         NVARCHAR(20)  NULL,
    EntityId     INT NULL,
    DepartmentId INT NULL,
    IsActive     BIT NOT NULL CONSTRAINT DF_AppUsers_IsActive DEFAULT(1),
    CONSTRAINT PK_AppUsers PRIMARY KEY (UserId),
    CONSTRAINT UQ_AppUsers_UserName UNIQUE (UserName),
    CONSTRAINT FK_AppUser_Entity     FOREIGN KEY (EntityId)     REFERENCES core.Entities(EntityId),
    CONSTRAINT FK_AppUser_Department FOREIGN KEY (DepartmentId) REFERENCES core.Departments(DepartmentId)
);
GO

CREATE TABLE core.AuditLogs (
    AuditLogId BIGINT IDENTITY(1,1) NOT NULL,
    Timestamp  DATETIME2 NOT NULL CONSTRAINT DF_AuditLogs_Timestamp DEFAULT(SYSUTCDATETIME()),
    UserName   NVARCHAR(100) NOT NULL,
    Action     NVARCHAR(50)  NOT NULL,   -- LOGIN, INSERT, UPDATE, DELETE
    EntityName NVARCHAR(100) NULL,
    RecordId   NVARCHAR(100) NULL,
    Details    NVARCHAR(MAX) NULL,
    CONSTRAINT PK_AuditLogs PRIMARY KEY (AuditLogId)
);
GO

CREATE TABLE core.InternalMessages (
    MessageId      BIGINT IDENTITY(1,1) NOT NULL,
    FromUser       NVARCHAR(100) NOT NULL,
    FromEntityCode NVARCHAR(20)  NULL,
    FromDeptCode   NVARCHAR(20)  NULL,
    Subject        NVARCHAR(200) NOT NULL,
    Body           NVARCHAR(MAX) NOT NULL,
    Status         NVARCHAR(20)  NOT NULL CONSTRAINT DF_InternalMessages_Status DEFAULT('Pending'),
    CreatedAt      DATETIME2 NOT NULL CONSTRAINT DF_InternalMessages_CreatedAt DEFAULT(SYSUTCDATETIME()),
    ReadAt         DATETIME2 NULL,
    ReadBy         NVARCHAR(100) NULL,
    AdminResponse  NVARCHAR(MAX) NULL,
    RespondedAt    DATETIME2 NULL,
    RespondedBy    NVARCHAR(100) NULL,
    CONSTRAINT PK_InternalMessages PRIMARY KEY (MessageId)
);
GO

CREATE TABLE core.PasswordResetRequests (
    ResetRequestId BIGINT IDENTITY(1,1) NOT NULL,
    UserName       NVARCHAR(100) NOT NULL,
    UserId         INT NULL,
    EntityId       INT NULL,
    ContactInfo    NVARCHAR(200) NULL,
    Note           NVARCHAR(500) NULL,
    Status         NVARCHAR(20)  NOT NULL CONSTRAINT DF_PasswordResetRequests_Status DEFAULT('Pending'),
    RequestSource  NVARCHAR(20)  NULL,
    RequestedAt    DATETIME2 NOT NULL CONSTRAINT DF_PasswordResetRequests_RequestedAt DEFAULT(SYSUTCDATETIME()),
    Token          NVARCHAR(128) NULL,
    TokenExpiresAt DATETIME2 NULL,
    TokenUsedAt    DATETIME2 NULL,
    IssuedAt       DATETIME2 NULL,
    IssuedBy       NVARCHAR(100) NULL,
    CompletedAt    DATETIME2 NULL,
    RejectedAt     DATETIME2 NULL,
    RejectedBy     NVARCHAR(100) NULL,
    AdminNote      NVARCHAR(500) NULL,
    CONSTRAINT PK_PasswordResetRequests PRIMARY KEY (ResetRequestId)
);
GO
CREATE INDEX IX_PasswordResetRequests_Token ON core.PasswordResetRequests(Token);
GO

/* ==========================================================================
   5) HR COSTS + GL ACTUALS / FORECASTS
   ========================================================================== */

CREATE TABLE core.HrEmployeeCosts (
    EmployeeCostId INT IDENTITY(1,1) NOT NULL,
    BudgetYear     INT NOT NULL,
    EmployeeId     NVARCHAR(50)  NOT NULL,
    EmployeeName   NVARCHAR(200) NOT NULL,
    Occupation     NVARCHAR(150) NULL,
    GLCode         NVARCHAR(30)  NOT NULL,
    GLKind         NVARCHAR(20)  NOT NULL,
    EntityId       INT NULL,
    EntityName     NVARCHAR(200) NOT NULL,
    DepartmentId   INT NULL,
    DepartmentName NVARCHAR(200) NOT NULL,
    AnnualCost     DECIMAL(18,2) NOT NULL,
    ImportedAt     DATETIME2 NOT NULL CONSTRAINT DF_HrEmployeeCosts_ImportedAt DEFAULT(SYSUTCDATETIME()),
    ImportedBy     NVARCHAR(100) NULL,
    SourceFile     NVARCHAR(260) NULL,
    CONSTRAINT PK_HrEmployeeCosts PRIMARY KEY (EmployeeCostId),
    CONSTRAINT UQ_HrEmployeeCosts_YearEmployee UNIQUE (BudgetYear, EmployeeId),
    CONSTRAINT FK_HrEmployeeCosts_Entity     FOREIGN KEY (EntityId)     REFERENCES core.Entities(EntityId),
    CONSTRAINT FK_HrEmployeeCosts_Department FOREIGN KEY (DepartmentId) REFERENCES core.Departments(DepartmentId)
);
GO

CREATE TABLE core.HrEmployeeCostAllocations (
    AllocationId    BIGINT IDENTITY(1,1) NOT NULL,
    EmployeeCostId  INT NOT NULL,
    ActivityId      INT NOT NULL,
    ProjectId       INT NULL,
    AllocatedAmount DECIMAL(18,2) NOT NULL,
    CreatedAt       DATETIME2 NOT NULL CONSTRAINT DF_HrEmployeeCostAllocations_CreatedAt DEFAULT(SYSUTCDATETIME()),
    CreatedBy       NVARCHAR(100) NOT NULL,
    CONSTRAINT PK_HrEmployeeCostAllocations PRIMARY KEY (AllocationId),
    CONSTRAINT FK_HrAlloc_EmployeeCost FOREIGN KEY (EmployeeCostId) REFERENCES core.HrEmployeeCosts(EmployeeCostId),
    CONSTRAINT FK_HrAlloc_Activity     FOREIGN KEY (ActivityId)     REFERENCES core.Activities(ActivityId),
    CONSTRAINT FK_HrAlloc_Project      FOREIGN KEY (ProjectId)      REFERENCES core.Projects(ProjectId)
);
GO

CREATE TABLE core.HistoricalGlActuals (
    HistoricalActualId BIGINT IDENTITY(1,1) NOT NULL,
    BudgetYear   INT NOT NULL,
    EntityId     INT NOT NULL,
    DepartmentId INT NOT NULL,
    GLCode       NVARCHAR(30) NOT NULL,
    GLType       NVARCHAR(20) NULL,
    Amount       DECIMAL(18,2) NOT NULL,
    CreatedAt    DATETIME2 NOT NULL CONSTRAINT DF_HistoricalGlActuals_CreatedAt DEFAULT(SYSUTCDATETIME()),
    CreatedBy    NVARCHAR(100) NULL,
    SourceFile   NVARCHAR(260) NULL,
    CONSTRAINT PK_HistoricalGlActuals PRIMARY KEY (HistoricalActualId),
    CONSTRAINT UQ_HistoricalGlActuals_Scope UNIQUE (BudgetYear, EntityId, DepartmentId, GLCode),
    CONSTRAINT FK_HistoricalGlActuals_Entity     FOREIGN KEY (EntityId)     REFERENCES core.Entities(EntityId),
    CONSTRAINT FK_HistoricalGlActuals_Department FOREIGN KEY (DepartmentId) REFERENCES core.Departments(DepartmentId)
);
GO

CREATE TABLE core.MidYearGlActualForecasts (
    MidYearId    BIGINT IDENTITY(1,1) NOT NULL,
    BudgetYear   INT NOT NULL,
    EntityId     INT NOT NULL,
    GLCode       NVARCHAR(30) NOT NULL,
    GLType       NVARCHAR(20) NOT NULL,
    ActualH1Amount   DECIMAL(18,2) NOT NULL,
    ForecastH2Amount DECIMAL(18,2) NULL,
    CreatedAt        DATETIME2 NOT NULL CONSTRAINT DF_MidYearGlActualForecasts_CreatedAt DEFAULT(SYSUTCDATETIME()),
    CreatedBy        NVARCHAR(100) NULL,
    ForecastUpdatedAt DATETIME2 NULL,
    ForecastUpdatedBy NVARCHAR(100) NULL,
    SourceFile        NVARCHAR(260) NULL,
    CONSTRAINT PK_MidYearGlActualForecasts PRIMARY KEY (MidYearId),
    CONSTRAINT UQ_MidYearGlActualForecasts_Scope UNIQUE (BudgetYear, EntityId, GLCode),
    CONSTRAINT FK_MidYearGlActualForecasts_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId)
);
GO

/* ==========================================================================
   6) WHAT-IF SCENARIOS
   ========================================================================== */

CREATE TABLE core.WhatIfScenarios (
    ScenarioId   INT IDENTITY(1,1) NOT NULL,
    BudgetYear   INT NOT NULL,
    EntityId     INT NULL,
    DepartmentId INT NULL,
    ScenarioName NVARCHAR(200) NOT NULL,
    IsActive     BIT NOT NULL CONSTRAINT DF_WhatIfScenarios_IsActive DEFAULT(1),
    CreatedAt    DATETIME2 NOT NULL CONSTRAINT DF_WhatIfScenarios_CreatedAt DEFAULT(SYSUTCDATETIME()),
    CreatedBy    NVARCHAR(100) NOT NULL,
    UpdatedAt    DATETIME2 NULL,
    UpdatedBy    NVARCHAR(100) NULL,
    CONSTRAINT PK_WhatIfScenarios PRIMARY KEY (ScenarioId),
    CONSTRAINT UQ_WhatIfScenarios_ScopeName UNIQUE (BudgetYear, EntityId, DepartmentId, ScenarioName),
    CONSTRAINT FK_WhatIfScenarios_Entity     FOREIGN KEY (EntityId)     REFERENCES core.Entities(EntityId),
    CONSTRAINT FK_WhatIfScenarios_Department FOREIGN KEY (DepartmentId) REFERENCES core.Departments(DepartmentId)
);
GO

CREATE TABLE core.WhatIfScenarioDefaults (
    ScenarioId        INT NOT NULL,
    CostInflationRate DECIMAL(9,4) NOT NULL CONSTRAINT DF_WhatIfScenarioDefaults_Cost DEFAULT(0),
    RevenueGrowthRate DECIMAL(9,4) NOT NULL CONSTRAINT DF_WhatIfScenarioDefaults_Rev DEFAULT(0),
    CONSTRAINT PK_WhatIfScenarioDefaults PRIMARY KEY (ScenarioId),
    CONSTRAINT FK_WhatIfScenarioDefaults_Scenario FOREIGN KEY (ScenarioId)
        REFERENCES core.WhatIfScenarios(ScenarioId) ON DELETE CASCADE
);
GO

CREATE TABLE core.WhatIfScenarioProjectRates (
    ScenarioProjectRateId BIGINT IDENTITY(1,1) NOT NULL,
    ScenarioId INT NOT NULL,
    ProjectId  INT NOT NULL,
    CostInflationRate DECIMAL(9,4) NULL,
    RevenueGrowthRate DECIMAL(9,4) NULL,
    CONSTRAINT PK_WhatIfScenarioProjectRates PRIMARY KEY (ScenarioProjectRateId),
    CONSTRAINT UQ_WhatIfScenarioProjectRates UNIQUE (ScenarioId, ProjectId),
    CONSTRAINT FK_WhatIfScenarioProjectRates_Scenario FOREIGN KEY (ScenarioId)
        REFERENCES core.WhatIfScenarios(ScenarioId) ON DELETE CASCADE,
    CONSTRAINT FK_WhatIfScenarioProjectRates_Project FOREIGN KEY (ProjectId)
        REFERENCES core.Projects(ProjectId)
);
GO

/* ==========================================================================
   7) PBB PERFORMANCE LAYER (KPIs, outputs, maturity, narratives, reports)
   ========================================================================== */

CREATE TABLE core.Kpis (
    KpiId       BIGINT IDENTITY(1,1) NOT NULL,
    BudgetYear  INT NOT NULL,
    Period      NVARCHAR(20)  NOT NULL CONSTRAINT DF_Kpis_Period DEFAULT('MidYear'),
    EntityId    INT NOT NULL,
    ProgramId   INT NULL,
    ActivityId  INT NULL,
    KpiName     NVARCHAR(300) NOT NULL,
    Unit        NVARCHAR(50)  NULL,
    Direction   NVARCHAR(10)  NOT NULL CONSTRAINT DF_Kpis_Direction DEFAULT('UP'),
    Baseline    DECIMAL(18,4) NULL,
    Target      DECIMAL(18,4) NULL,
    ActualValue DECIMAL(18,4) NULL,
    Status      NVARCHAR(20)  NULL,
    CreatedAt   DATETIME2(0)  NOT NULL CONSTRAINT DF_Kpis_CreatedAt DEFAULT(SYSUTCDATETIME()),
    CreatedBy   NVARCHAR(100) NULL,
    CONSTRAINT PK_Kpis PRIMARY KEY (KpiId),
    CONSTRAINT FK_Kpis_Entity   FOREIGN KEY (EntityId)   REFERENCES core.Entities(EntityId),
    CONSTRAINT FK_Kpis_Program  FOREIGN KEY (ProgramId)  REFERENCES core.Programs(ProgramId),
    CONSTRAINT FK_Kpis_Activity FOREIGN KEY (ActivityId) REFERENCES core.Activities(ActivityId)
);
GO
CREATE INDEX IX_Kpis_Scope ON core.Kpis(BudgetYear, EntityId, Period);
GO

CREATE TABLE core.KpiCostLinks (
    KpiCostLinkId BIGINT IDENTITY(1,1) NOT NULL,
    KpiId         BIGINT NOT NULL,
    ActivityId    INT NULL,
    ProgramId     INT NULL,
    WeightPct     DECIMAL(9,4) NOT NULL CONSTRAINT DF_KpiCostLinks_WeightPct DEFAULT(100),
    CONSTRAINT PK_KpiCostLinks PRIMARY KEY (KpiCostLinkId),
    CONSTRAINT FK_KpiCostLinks_Kpi      FOREIGN KEY (KpiId)      REFERENCES core.Kpis(KpiId) ON DELETE CASCADE,
    CONSTRAINT FK_KpiCostLinks_Activity FOREIGN KEY (ActivityId) REFERENCES core.Activities(ActivityId),
    CONSTRAINT FK_KpiCostLinks_Program  FOREIGN KEY (ProgramId)  REFERENCES core.Programs(ProgramId)
);
GO

CREATE TABLE core.ActivityOutputs (
    ActivityOutputId BIGINT IDENTITY(1,1) NOT NULL,
    ActivityId    INT NOT NULL,
    BudgetYear    INT NOT NULL,
    OutputMeasure NVARCHAR(200) NOT NULL,
    OutputVolume  DECIMAL(18,4) NOT NULL CONSTRAINT DF_ActivityOutputs_OutputVolume DEFAULT(0),
    IsPrimary     BIT NOT NULL CONSTRAINT DF_ActivityOutputs_IsPrimary DEFAULT(1),
    CreatedAt     DATETIME2(0)  NOT NULL CONSTRAINT DF_ActivityOutputs_CreatedAt DEFAULT(SYSUTCDATETIME()),
    CreatedBy     NVARCHAR(100) NULL,
    CONSTRAINT PK_ActivityOutputs PRIMARY KEY (ActivityOutputId),
    CONSTRAINT UQ_ActivityOutputs UNIQUE (ActivityId, BudgetYear, OutputMeasure),
    CONSTRAINT FK_ActivityOutputs_Activity FOREIGN KEY (ActivityId) REFERENCES core.Activities(ActivityId)
);
GO

CREATE TABLE core.MaturityAssessments (
    MaturityAssessmentId INT IDENTITY(1,1) NOT NULL,
    EntityId    INT NOT NULL,
    BudgetYear  INT NOT NULL,
    Period      NVARCHAR(20)   NOT NULL CONSTRAINT DF_MaturityAssessments_Period DEFAULT('MidYear'),
    Stage       DECIMAL(3,1)   NOT NULL CONSTRAINT DF_MaturityAssessments_Stage DEFAULT(1.0),
    Form        NVARCHAR(40)   NULL,
    StatusLabel NVARCHAR(20)   NULL,
    Notes       NVARCHAR(1000) NULL,
    AssessedAt  DATETIME2(0)   NOT NULL CONSTRAINT DF_MaturityAssessments_AssessedAt DEFAULT(SYSUTCDATETIME()),
    AssessedBy  NVARCHAR(100)  NULL,
    CONSTRAINT PK_MaturityAssessments PRIMARY KEY (MaturityAssessmentId),
    CONSTRAINT UQ_MaturityAssessments UNIQUE (EntityId, BudgetYear, Period),
    CONSTRAINT FK_MaturityAssessments_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId)
);
GO

CREATE TABLE core.EntityReviewNotes (
    EntityReviewNoteId INT IDENTITY(1,1) NOT NULL,
    EntityId   INT NOT NULL,
    BudgetYear INT NOT NULL,
    Period     NVARCHAR(20)  NOT NULL CONSTRAINT DF_EntityReviewNotes_Period DEFAULT('MidYear'),
    NoteType   NVARCHAR(30)  NOT NULL CONSTRAINT DF_EntityReviewNotes_NoteType DEFAULT('Outcome'),
    Body       NVARCHAR(MAX) NULL,
    SortOrder  INT NOT NULL CONSTRAINT DF_EntityReviewNotes_SortOrder DEFAULT(0),
    CreatedAt  DATETIME2(0)  NOT NULL CONSTRAINT DF_EntityReviewNotes_CreatedAt DEFAULT(SYSUTCDATETIME()),
    CreatedBy  NVARCHAR(100) NULL,
    CONSTRAINT PK_EntityReviewNotes PRIMARY KEY (EntityReviewNoteId),
    CONSTRAINT FK_EntityReviewNotes_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId)
);
GO

CREATE TABLE core.ReviewNarratives (
    ReviewNarrativeId INT IDENTITY(1,1) NOT NULL,
    BudgetYear     INT NOT NULL,
    Period         NVARCHAR(20)  NOT NULL CONSTRAINT DF_ReviewNarratives_Period DEFAULT('MidYear'),
    Section        NVARCHAR(30)  NOT NULL CONSTRAINT DF_ReviewNarratives_Section DEFAULT('Finding'),
    Title          NVARCHAR(300) NULL,
    Body           NVARCHAR(MAX) NULL,
    Owner          NVARCHAR(200) NULL,
    DueText        NVARCHAR(100) NULL,
    SuccessMeasure NVARCHAR(500) NULL,
    SortOrder      INT NOT NULL CONSTRAINT DF_ReviewNarratives_SortOrder DEFAULT(0),
    CreatedAt      DATETIME2(0)  NOT NULL CONSTRAINT DF_ReviewNarratives_CreatedAt DEFAULT(SYSUTCDATETIME()),
    CreatedBy      NVARCHAR(100) NULL,
    CONSTRAINT PK_ReviewNarratives PRIMARY KEY (ReviewNarrativeId)
);
GO
CREATE INDEX IX_ReviewNarratives_Scope ON core.ReviewNarratives(BudgetYear, Period, Section, SortOrder);
GO

CREATE TABLE core.CostShapeMap (
    CostShapeMapId INT IDENTITY(1,1) NOT NULL,
    GLCode       NVARCHAR(30)  NULL,
    MatchKeyword NVARCHAR(100) NULL,
    Bucket       NVARCHAR(30)  NOT NULL,
    Priority     INT NOT NULL CONSTRAINT DF_CostShapeMap_Priority DEFAULT(100),
    IsActive     BIT NOT NULL CONSTRAINT DF_CostShapeMap_IsActive DEFAULT(1),
    CONSTRAINT PK_CostShapeMap PRIMARY KEY (CostShapeMapId)
);
GO

CREATE TABLE core.SavedReports (
    SavedReportId INT IDENTITY(1,1) NOT NULL,
    OwnerUser     NVARCHAR(256) NOT NULL,
    Name          NVARCHAR(150) NOT NULL,
    BudgetYear    INT NULL,
    EntityId      INT NULL,
    RowDim        NVARCHAR(30)  NOT NULL,
    ColDim        NVARCHAR(30)  NULL,
    Measure       NVARCHAR(30)  NOT NULL,
    Category      NVARCHAR(50)  NULL,
    IncludeHr     BIT NOT NULL CONSTRAINT DF_SavedReports_IncludeHr DEFAULT(0),
    ChartType     NVARCHAR(20)  NOT NULL CONSTRAINT DF_SavedReports_ChartType DEFAULT('table'),
    CategoryMode      NVARCHAR(10)  NOT NULL CONSTRAINT DF_SavedReports_CategoryMode DEFAULT('Include'),
    CategoriesCsv     NVARCHAR(400) NULL,
    ProgramTypeFilter NVARCHAR(20)  NULL,
    CostBasis         NVARCHAR(20)  NOT NULL CONSTRAINT DF_SavedReports_CostBasis DEFAULT('Direct'),
    CreatedAt     DATETIME2(0) NOT NULL CONSTRAINT DF_SavedReports_CreatedAt DEFAULT(SYSUTCDATETIME()),
    CONSTRAINT PK_SavedReports PRIMARY KEY (SavedReportId)
);
GO
CREATE INDEX IX_SavedReports_Owner ON core.SavedReports(OwnerUser, Name);
GO

/* ==========================================================================
   8) COST REALLOCATION ENGINE (drivers, rules, runs, transactions)
   ========================================================================== */

CREATE TABLE core.AllocationDrivers (
    DriverId   INT IDENTITY(1,1) NOT NULL,
    DriverCode NVARCHAR(40)  NOT NULL,
    DriverName NVARCHAR(120) NOT NULL,
    Unit       NVARCHAR(40)  NULL,
    IsActive   BIT NOT NULL CONSTRAINT DF_AllocationDrivers_IsActive DEFAULT(1),
    CONSTRAINT PK_AllocationDrivers PRIMARY KEY (DriverId)
);
GO
CREATE UNIQUE INDEX UX_AllocationDrivers_Code ON core.AllocationDrivers(DriverCode);
GO

CREATE TABLE core.AllocationDriverValues (
    DriverValueId    INT IDENTITY(1,1) NOT NULL,
    DriverId         INT NOT NULL,
    BudgetYear       INT NOT NULL,
    TargetProgramId  INT NOT NULL,
    TargetActivityId INT NULL,
    Value            DECIMAL(18,4) NOT NULL CONSTRAINT DF_AllocationDriverValues_Value DEFAULT(0),
    CONSTRAINT PK_AllocationDriverValues PRIMARY KEY (DriverValueId),
    CONSTRAINT FK_DriverValues_Driver  FOREIGN KEY (DriverId)        REFERENCES core.AllocationDrivers(DriverId),
    CONSTRAINT FK_DriverValues_Program FOREIGN KEY (TargetProgramId) REFERENCES core.Programs(ProgramId)
);
GO
CREATE INDEX IX_DriverValues_Lookup ON core.AllocationDriverValues(BudgetYear, DriverId, TargetProgramId);
GO

CREATE TABLE core.AllocationRules (
    RuleId           INT IDENTITY(1,1) NOT NULL,
    BudgetYear       INT NOT NULL,
    EntityId         INT NOT NULL,
    SourceProgramId  INT NOT NULL,
    SourceActivityId INT NULL,
    Method           NVARCHAR(20) NOT NULL,
    DriverId         INT NULL,
    CategoryScopeCsv NVARCHAR(200) NOT NULL CONSTRAINT DF_AllocationRules_CategoryScopeCsv DEFAULT('OPEX,HR'),
    TargetScope      NVARCHAR(20)  NOT NULL CONSTRAINT DF_AllocationRules_TargetScope DEFAULT('AllMandate'),
    SourcePercent    DECIMAL(9,4)  NOT NULL CONSTRAINT DF_AllocationRules_SourcePercent DEFAULT(100),
    Sequence         INT NOT NULL CONSTRAINT DF_AllocationRules_Sequence DEFAULT(100),
    IsActive         BIT NOT NULL CONSTRAINT DF_AllocationRules_IsActive DEFAULT(1),
    CreatedAt        DATETIME2(0)  NOT NULL CONSTRAINT DF_AllocationRules_CreatedAt DEFAULT(SYSUTCDATETIME()),
    CreatedBy        NVARCHAR(100) NULL,
    CONSTRAINT PK_AllocationRules PRIMARY KEY (RuleId),
    CONSTRAINT FK_AllocRules_Source FOREIGN KEY (SourceProgramId) REFERENCES core.Programs(ProgramId),
    CONSTRAINT FK_AllocRules_Driver FOREIGN KEY (DriverId)        REFERENCES core.AllocationDrivers(DriverId)
);
GO
CREATE INDEX IX_AllocRules_Scope ON core.AllocationRules(BudgetYear, EntityId, IsActive);
GO

CREATE TABLE core.AllocationRuleTargets (
    RuleTargetId     INT IDENTITY(1,1) NOT NULL,
    RuleId           INT NOT NULL,
    TargetProgramId  INT NOT NULL,
    TargetActivityId INT NULL,
    Weight           DECIMAL(9,4) NOT NULL CONSTRAINT DF_AllocationRuleTargets_Weight DEFAULT(0),
    CONSTRAINT PK_AllocationRuleTargets PRIMARY KEY (RuleTargetId),
    CONSTRAINT FK_RuleTargets_Rule    FOREIGN KEY (RuleId)          REFERENCES core.AllocationRules(RuleId) ON DELETE CASCADE,
    CONSTRAINT FK_RuleTargets_Program FOREIGN KEY (TargetProgramId) REFERENCES core.Programs(ProgramId)
);
GO
CREATE INDEX IX_RuleTargets_Rule ON core.AllocationRuleTargets(RuleId);
GO

CREATE TABLE core.AllocationRuns (
    RunId        INT IDENTITY(1,1) NOT NULL,
    BudgetYear   INT NOT NULL,
    EntityId     INT NULL,
    Period       NVARCHAR(20) NOT NULL CONSTRAINT DF_AllocationRuns_Period DEFAULT('Annual'),
    Status       NVARCHAR(20) NOT NULL CONSTRAINT DF_AllocationRuns_Status DEFAULT('Draft'),
    Method       NVARCHAR(20) NOT NULL CONSTRAINT DF_AllocationRuns_Method DEFAULT('StepDown'),
    RunAt        DATETIME2(0) NOT NULL CONSTRAINT DF_AllocationRuns_RunAt DEFAULT(SYSUTCDATETIME()),
    RunBy        NVARCHAR(100) NULL,
    Notes        NVARCHAR(500) NULL,
    ReconciledOk BIT NOT NULL CONSTRAINT DF_AllocationRuns_ReconciledOk DEFAULT(0),
    CONSTRAINT PK_AllocationRuns PRIMARY KEY (RunId)
);
GO
CREATE INDEX IX_AllocRuns_Scope ON core.AllocationRuns(BudgetYear, EntityId, Status);
GO

CREATE TABLE core.AllocationTransactions (
    TxnId            BIGINT IDENTITY(1,1) NOT NULL,
    RunId            INT NOT NULL,
    BudgetYear       INT NOT NULL,
    Period           NVARCHAR(20) NOT NULL CONSTRAINT DF_AllocationTransactions_Period DEFAULT('Annual'),
    EntityId         INT NOT NULL,
    SourceProgramId  INT NOT NULL,
    SourceActivityId INT NULL,
    SourceCategoryCode NVARCHAR(50) NULL,
    TargetProgramId  INT NOT NULL,
    TargetActivityId INT NULL,
    DriverId         INT NULL,
    BasisValue    DECIMAL(18,4) NOT NULL CONSTRAINT DF_AllocationTransactions_BasisValue DEFAULT(0),
    BasisTotal    DECIMAL(18,4) NOT NULL CONSTRAINT DF_AllocationTransactions_BasisTotal DEFAULT(0),
    AllocationPct DECIMAL(9,6)  NOT NULL CONSTRAINT DF_AllocationTransactions_AllocationPct DEFAULT(0),
    Amount        DECIMAL(18,2) NOT NULL CONSTRAINT DF_AllocationTransactions_Amount DEFAULT(0),
    CreatedAt     DATETIME2(0)  NOT NULL CONSTRAINT DF_AllocationTransactions_CreatedAt DEFAULT(SYSUTCDATETIME()),
    CONSTRAINT PK_AllocationTransactions PRIMARY KEY (TxnId),
    CONSTRAINT FK_AllocTxn_Run FOREIGN KEY (RunId) REFERENCES core.AllocationRuns(RunId)
);
GO
CREATE INDEX IX_AllocTxn_Target ON core.AllocationTransactions(BudgetYear, TargetProgramId);
CREATE INDEX IX_AllocTxn_Source ON core.AllocationTransactions(BudgetYear, SourceProgramId);
CREATE INDEX IX_AllocTxn_Run    ON core.AllocationTransactions(RunId);
GO

/* ==========================================================================
   9) MINIMAL SEED (delete this block if you want a completely empty DB)
      - the 3 budget categories the app relies on
      - a default SYSADMIN login  (user: admin  /  password: admin)
   ========================================================================== */
IF NOT EXISTS (SELECT 1 FROM core.Categories WHERE UPPER(CategoryCode) = 'REVENUE')
    INSERT INTO core.Categories (CategoryCode, CategoryName) VALUES ('REVENUE', 'Revenue');
IF NOT EXISTS (SELECT 1 FROM core.Categories WHERE UPPER(CategoryCode) = 'OPEX')
    INSERT INTO core.Categories (CategoryCode, CategoryName) VALUES ('OPEX', 'Operating Expenditure');
IF NOT EXISTS (SELECT 1 FROM core.Categories WHERE UPPER(CategoryCode) = 'CAPEX')
    INSERT INTO core.Categories (CategoryCode, CategoryName) VALUES ('CAPEX', 'Capital Expenditure');
GO
IF NOT EXISTS (SELECT 1 FROM core.AppUsers WHERE UserName = 'admin')
    INSERT INTO core.AppUsers (UserName, Password, Role, IsActive, EntityId, DepartmentId)
    VALUES ('admin', 'admin', 'SYSADMIN', 1, NULL, NULL);
GO

/* ==========================================================================
   10) VIEWS
   --------------------------------------------------------------------------
   10a) core.vw_GL_CashBasis  - used by the app (mapped read-only by EF).

   NOTE: the exact production definition of this view was not available in the
   source repo, so this is a faithful RECONSTRUCTION that matches the columns
   the app reads (budget lines rolled up to GL account, with the monthly
   spread). If your hosted copy differs, replace this CREATE VIEW with the
   scripted definition from the server (right-click the view > Script As).
   ========================================================================== */
GO
CREATE OR ALTER VIEW core.vw_GL_CashBasis AS
    SELECT
        b.BudgetYear,
        e.EntityCode, e.EntityName,
        d.DeptCode,   d.DeptName,
        cat.CategoryCode,
        gl.GLCode, gl.GLName, gl.GLType,
        SUM(b.Amount)                              AS AnnualAmount,
        SUM(b.M01 + b.M02 + b.M03 + b.M04 + b.M05 + b.M06
          + b.M07 + b.M08 + b.M09 + b.M10 + b.M11 + b.M12) AS DistributedAmount,
        SUM(b.M01) AS M01, SUM(b.M02) AS M02, SUM(b.M03) AS M03,
        SUM(b.M04) AS M04, SUM(b.M05) AS M05, SUM(b.M06) AS M06,
        SUM(b.M07) AS M07, SUM(b.M08) AS M08, SUM(b.M09) AS M09,
        SUM(b.M10) AS M10, SUM(b.M11) AS M11, SUM(b.M12) AS M12
    FROM core.BudgetLines b
    JOIN core.Categories  cat ON cat.CategoryId = b.CategoryId
    JOIN core.Items       it  ON it.ItemId      = b.ItemId
    JOIN core.GLAccounts  gl  ON gl.GLAccountId = it.GLAccountId
    JOIN core.Entities    e   ON e.EntityId     = b.EntityId
    JOIN core.Departments d   ON d.DepartmentId = b.DepartmentId
    GROUP BY
        b.BudgetYear, e.EntityCode, e.EntityName, d.DeptCode, d.DeptName,
        cat.CategoryCode, gl.GLCode, gl.GLName, gl.GLType;
GO

/* --------------------------------------------------------------------------
   10b) BI / Power BI views  (from docs/PowerBI_CombinedCost_Views.sql)
        core.vw_CostByGL, core.vw_CostByActivity,
        core.vw_CostByActivity_AfterAllocation
   -------------------------------------------------------------------------- */
GO
CREATE OR ALTER VIEW core.vw_CostByGL AS
    SELECT
        b.BudgetYear,
        b.EntityId,     e.EntityCode,  e.EntityName,
        b.DepartmentId, d.DeptCode,    d.DeptName,
        cat.CategoryCode                    AS CostType,
        gl.GLCode, gl.GLName, gl.GLType,
        b.Amount,
        b.M01, b.M02, b.M03, b.M04, b.M05, b.M06,
        b.M07, b.M08, b.M09, b.M10, b.M11, b.M12,
        b.F1_Amount                         AS Forecast1Amount,
        b.F2_Amount                         AS Forecast2Amount,
        CAST(N'Budget' AS nvarchar(20))     AS Source
    FROM core.BudgetLines  b
    JOIN core.Categories   cat ON cat.CategoryId  = b.CategoryId
    JOIN core.Items        it  ON it.ItemId       = b.ItemId
    JOIN core.GLAccounts   gl  ON gl.GLAccountId  = it.GLAccountId
    JOIN core.Entities     e   ON e.EntityId      = b.EntityId
    JOIN core.Departments  d   ON d.DepartmentId  = b.DepartmentId
    WHERE cat.CategoryCode <> N'HR'
    UNION ALL
    SELECT
        emp.BudgetYear,
        emp.EntityId,     e.EntityCode,  e.EntityName,
        emp.DepartmentId, d.DeptCode,    d.DeptName,
        N'HR'                               AS CostType,
        emp.GLCode,
        MAX(gl.GLName)                      AS GLName,
        MAX(gl.GLType)                      AS GLType,
        SUM(emp.AnnualCost)                 AS Amount,
        SUM(emp.AnnualCost)/12.0, SUM(emp.AnnualCost)/12.0, SUM(emp.AnnualCost)/12.0,
        SUM(emp.AnnualCost)/12.0, SUM(emp.AnnualCost)/12.0, SUM(emp.AnnualCost)/12.0,
        SUM(emp.AnnualCost)/12.0, SUM(emp.AnnualCost)/12.0, SUM(emp.AnnualCost)/12.0,
        SUM(emp.AnnualCost)/12.0, SUM(emp.AnnualCost)/12.0, SUM(emp.AnnualCost)/12.0,
        SUM(emp.AnnualCost)                 AS Forecast1Amount,
        SUM(emp.AnnualCost)                 AS Forecast2Amount,
        CAST(N'HR-Imported' AS nvarchar(20)) AS Source
    FROM core.HrEmployeeCosts emp
    LEFT JOIN core.GLAccounts  gl ON gl.GLCode      = emp.GLCode
    LEFT JOIN core.Entities    e  ON e.EntityId     = emp.EntityId
    LEFT JOIN core.Departments d  ON d.DepartmentId = emp.DepartmentId
    GROUP BY
        emp.BudgetYear, emp.EntityId, e.EntityCode, e.EntityName,
        emp.DepartmentId, d.DeptCode, d.DeptName, emp.GLCode;
GO

CREATE OR ALTER VIEW core.vw_CostByActivity AS
    SELECT
        b.BudgetYear,
        b.EntityId,     e.EntityCode,  e.EntityName,
        b.DepartmentId, d.DeptCode,    d.DeptName,
        cat.CategoryCode                    AS CostType,
        gl.GLCode, gl.GLName, gl.GLType,
        prog.ProgramId, prog.ProgramCode, prog.ProgramName, prog.ProgramType,
        act.ActivityId, act.ActivityCode, act.ActivityName,
        b.ProjectId, proj.ProjectCode, proj.ProjectName,
        b.Amount,
        CAST(N'Budget' AS nvarchar(20))     AS Source
    FROM core.BudgetLines  b
    JOIN core.Categories   cat ON cat.CategoryId  = b.CategoryId
    JOIN core.Items        it  ON it.ItemId       = b.ItemId
    JOIN core.GLAccounts   gl  ON gl.GLAccountId  = it.GLAccountId
    JOIN core.Entities     e   ON e.EntityId      = b.EntityId
    JOIN core.Departments  d   ON d.DepartmentId  = b.DepartmentId
    LEFT JOIN core.Activities act  ON act.ActivityId = b.ActivityId
    LEFT JOIN core.Programs   prog ON prog.ProgramId = COALESCE(b.ProgramId, act.ProgramId)
    LEFT JOIN core.Projects   proj ON proj.ProjectId = b.ProjectId
    WHERE cat.CategoryCode <> N'HR'
    UNION ALL
    SELECT
        emp.BudgetYear,
        emp.EntityId,     e.EntityCode,  e.EntityName,
        act.DepartmentId, d.DeptCode,    d.DeptName,
        N'HR'                               AS CostType,
        emp.GLCode, gl.GLName, gl.GLType,
        prog.ProgramId, prog.ProgramCode, prog.ProgramName, prog.ProgramType,
        act.ActivityId, act.ActivityCode, act.ActivityName,
        a.ProjectId, proj.ProjectCode, proj.ProjectName,
        a.AllocatedAmount                   AS Amount,
        CAST(N'HR-Allocated' AS nvarchar(20)) AS Source
    FROM core.HrEmployeeCostAllocations a
    JOIN core.HrEmployeeCosts emp ON emp.EmployeeCostId = a.EmployeeCostId
    JOIN core.Activities      act ON act.ActivityId     = a.ActivityId
    JOIN core.Programs        prog ON prog.ProgramId     = act.ProgramId
    LEFT JOIN core.Projects   proj ON proj.ProjectId     = a.ProjectId
    LEFT JOIN core.GLAccounts gl  ON gl.GLCode           = emp.GLCode
    LEFT JOIN core.Entities   e   ON e.EntityId          = emp.EntityId
    LEFT JOIN core.Departments d  ON d.DepartmentId      = act.DepartmentId;
GO

CREATE OR ALTER VIEW core.vw_CostByActivity_AfterAllocation AS
    WITH Base AS (
        SELECT * FROM core.vw_CostByActivity
    ),
    RunRank AS (
        SELECT r.RunId, x.BudgetYear, x.EntityId,
               ROW_NUMBER() OVER (PARTITION BY x.BudgetYear, x.EntityId
                                  ORDER BY r.RunAt DESC, r.RunId DESC) AS rn
        FROM core.AllocationRuns r
        JOIN (SELECT DISTINCT RunId, BudgetYear, EntityId
              FROM core.AllocationTransactions) x ON x.RunId = r.RunId
        WHERE r.Status = N'Posted'
    ),
    LatestRun AS (
        SELECT RunId, BudgetYear, EntityId FROM RunRank WHERE rn = 1
    ),
    Txn AS (
        SELECT tx.BudgetYear, tx.EntityId,
               UPPER(tx.SourceCategoryCode) AS CostType,
               tx.SourceProgramId, tx.TargetProgramId, tx.Amount
        FROM core.AllocationTransactions tx
        JOIN LatestRun lr ON lr.RunId = tx.RunId
                         AND lr.BudgetYear = tx.BudgetYear
                         AND lr.EntityId  = tx.EntityId
    ),
    ProgNet AS (
        SELECT BudgetYear, EntityId, TargetProgramId AS ProgramId, CostType,
               SUM(Amount) AS NetAmount, CAST(N'Allocation-In' AS nvarchar(20)) AS Source
        FROM Txn
        GROUP BY BudgetYear, EntityId, TargetProgramId, CostType
        UNION ALL
        SELECT BudgetYear, EntityId, SourceProgramId AS ProgramId, CostType,
               -SUM(Amount) AS NetAmount, CAST(N'Allocation-Out' AS nvarchar(20)) AS Source
        FROM Txn
        GROUP BY BudgetYear, EntityId, SourceProgramId, CostType
    ),
    ActBase AS (
        SELECT BudgetYear, EntityId, ProgramId, ActivityId, ActivityCode, ActivityName,
               CostType, SUM(Amount) AS ActAmount
        FROM Base
        WHERE ActivityId IS NOT NULL
        GROUP BY BudgetYear, EntityId, ProgramId, ActivityId, ActivityCode, ActivityName, CostType
    ),
    ProgBase AS (
        SELECT BudgetYear, EntityId, ProgramId, CostType, SUM(ActAmount) AS ProgAmount
        FROM ActBase
        GROUP BY BudgetYear, EntityId, ProgramId, CostType
    )
    SELECT
        BudgetYear, EntityId, EntityCode, EntityName,
        DepartmentId, DeptCode, DeptName,
        CostType, GLCode, GLName, GLType,
        ProgramId, ProgramCode, ProgramName, ProgramType,
        ActivityId, ActivityCode, ActivityName,
        ProjectId, ProjectCode, ProjectName,
        Amount, Source
    FROM Base
    UNION ALL
    SELECT
        pn.BudgetYear, pn.EntityId, e.EntityCode, e.EntityName,
        CAST(NULL AS int), CAST(NULL AS nvarchar(4000)), CAST(NULL AS nvarchar(4000)),
        pn.CostType,
        CAST(NULL AS nvarchar(4000)), CAST(NULL AS nvarchar(4000)), CAST(NULL AS nvarchar(4000)),
        p.ProgramId, p.ProgramCode, p.ProgramName, p.ProgramType,
        ab.ActivityId, ab.ActivityCode, ab.ActivityName,
        CAST(NULL AS int), CAST(NULL AS nvarchar(4000)), CAST(NULL AS nvarchar(4000)),
        pn.NetAmount * ab.ActAmount / pb.ProgAmount AS Amount,
        pn.Source
    FROM ProgNet pn
    JOIN ProgBase pb ON pb.BudgetYear = pn.BudgetYear AND pb.EntityId = pn.EntityId
                    AND pb.ProgramId  = pn.ProgramId  AND pb.CostType = pn.CostType
                    AND pb.ProgAmount <> 0
    JOIN ActBase ab  ON ab.BudgetYear = pn.BudgetYear AND ab.EntityId = pn.EntityId
                    AND ab.ProgramId  = pn.ProgramId  AND ab.CostType = pn.CostType
    JOIN core.Programs p ON p.ProgramId = pn.ProgramId
    JOIN core.Entities e ON e.EntityId  = pn.EntityId
    UNION ALL
    SELECT
        pn.BudgetYear, pn.EntityId, e.EntityCode, e.EntityName,
        CAST(NULL AS int), CAST(NULL AS nvarchar(4000)), CAST(NULL AS nvarchar(4000)),
        pn.CostType,
        CAST(NULL AS nvarchar(4000)), CAST(NULL AS nvarchar(4000)), CAST(NULL AS nvarchar(4000)),
        p.ProgramId, p.ProgramCode, p.ProgramName, p.ProgramType,
        CAST(NULL AS int), CAST(NULL AS nvarchar(4000)), CAST(NULL AS nvarchar(4000)),
        CAST(NULL AS int), CAST(NULL AS nvarchar(4000)), CAST(NULL AS nvarchar(4000)),
        pn.NetAmount AS Amount,
        pn.Source
    FROM ProgNet pn
    JOIN core.Programs p ON p.ProgramId = pn.ProgramId
    JOIN core.Entities e ON e.EntityId  = pn.EntityId
    LEFT JOIN ProgBase pb ON pb.BudgetYear = pn.BudgetYear AND pb.EntityId = pn.EntityId
                         AND pb.ProgramId  = pn.ProgramId  AND pb.CostType = pn.CostType
                         AND pb.ProgAmount <> 0
    WHERE pb.ProgramId IS NULL;
GO

/* --------------------------------------------------------------------------
   10c) Allocated HR cost by Occupation views
        (from docs/PowerBI_AllocatedCostByOccupation_View.sql)
   -------------------------------------------------------------------------- */
GO
CREATE OR ALTER VIEW core.vw_AllocatedCostByOccupation AS
    SELECT
        emp.BudgetYear,
        emp.EntityId, e.EntityCode, e.EntityName,
        COALESCE(NULLIF(LTRIM(RTRIM(emp.Occupation)), N''), N'(Unspecified)') AS Occupation,
        prog.ProgramId, prog.ProgramCode, prog.ProgramName, prog.ProgramType,
        act.ActivityId, act.ActivityCode, act.ActivityName,
        a.ProjectId, proj.ProjectCode, proj.ProjectName,
        COUNT(DISTINCT emp.EmployeeCostId)  AS EmployeeCount,
        SUM(a.AllocatedAmount)              AS AllocatedCost
    FROM core.HrEmployeeCostAllocations a
    JOIN core.HrEmployeeCosts  emp ON emp.EmployeeCostId = a.EmployeeCostId
    JOIN core.Activities       act ON act.ActivityId     = a.ActivityId
    JOIN core.Programs        prog ON prog.ProgramId      = act.ProgramId
    LEFT JOIN core.Projects   proj ON proj.ProjectId      = a.ProjectId
    LEFT JOIN core.Entities   e    ON e.EntityId          = emp.EntityId
    GROUP BY
        emp.BudgetYear,
        emp.EntityId, e.EntityCode, e.EntityName,
        COALESCE(NULLIF(LTRIM(RTRIM(emp.Occupation)), N''), N'(Unspecified)'),
        prog.ProgramId, prog.ProgramCode, prog.ProgramName, prog.ProgramType,
        act.ActivityId, act.ActivityCode, act.ActivityName,
        a.ProjectId, proj.ProjectCode, proj.ProjectName;
GO

CREATE OR ALTER VIEW core.vw_AllocatedCostByOccupation_Summary AS
    WITH Emp AS (
        SELECT
            emp.BudgetYear, emp.EntityId,
            COALESCE(NULLIF(LTRIM(RTRIM(emp.Occupation)), N''), N'(Unspecified)') AS Occupation,
            COUNT(*)            AS EmployeeCount,
            SUM(emp.AnnualCost) AS TotalAnnualCost
        FROM core.HrEmployeeCosts emp
        GROUP BY emp.BudgetYear, emp.EntityId,
                 COALESCE(NULLIF(LTRIM(RTRIM(emp.Occupation)), N''), N'(Unspecified)')
    ),
    Alloc AS (
        SELECT
            emp.BudgetYear, emp.EntityId,
            COALESCE(NULLIF(LTRIM(RTRIM(emp.Occupation)), N''), N'(Unspecified)') AS Occupation,
            SUM(a.AllocatedAmount) AS AllocatedCost
        FROM core.HrEmployeeCostAllocations a
        JOIN core.HrEmployeeCosts emp ON emp.EmployeeCostId = a.EmployeeCostId
        GROUP BY emp.BudgetYear, emp.EntityId,
                 COALESCE(NULLIF(LTRIM(RTRIM(emp.Occupation)), N''), N'(Unspecified)')
    )
    SELECT
        COALESCE(em.BudgetYear, al.BudgetYear)  AS BudgetYear,
        COALESCE(em.EntityId, al.EntityId)      AS EntityId,
        e.EntityCode, e.EntityName,
        COALESCE(em.Occupation, al.Occupation)  AS Occupation,
        ISNULL(em.EmployeeCount, 0)             AS EmployeeCount,
        ISNULL(em.TotalAnnualCost, 0)           AS TotalAnnualCost,
        ISNULL(al.AllocatedCost, 0)             AS AllocatedCost,
        ISNULL(em.TotalAnnualCost, 0) - ISNULL(al.AllocatedCost, 0) AS UnallocatedCost
    FROM Emp em
    FULL OUTER JOIN Alloc al
        ON  al.BudgetYear = em.BudgetYear
        AND al.EntityId   = em.EntityId
        AND al.Occupation = em.Occupation
    LEFT JOIN core.Entities e ON e.EntityId = COALESCE(em.EntityId, al.EntityId);
GO

PRINT 'GovBudget full schema created successfully.';
GO
