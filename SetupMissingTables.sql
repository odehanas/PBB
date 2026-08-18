/* IMPORTANT: This script runs against the database you currently have selected.
   Before running, make sure the correct database is selected:
     - Local (SSMS):        USE GovBudgetDB;
     - SmarterASP/hosting:  select your DB (e.g. db_ab5910_govbudgel) in the manager,
                            or uncomment and edit the line below.
*/
-- USE GovBudgetDB;
-- GO

/* 1) Ensure Schema Exists */
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'core')
BEGIN
    EXEC('CREATE SCHEMA [core]')
END
GO

/* 2) Create Projects Table */
IF OBJECT_ID(N'core.Projects', N'U') IS NULL
BEGIN
    PRINT 'Creating core.Projects table...'
    CREATE TABLE core.Projects (
        ProjectId INT IDENTITY(1,1) PRIMARY KEY,
        ProjectCode NVARCHAR(30) NOT NULL,
        ProjectName NVARCHAR(200) NOT NULL,
        OwningDepartmentId INT NULL,
        IsActive BIT NOT NULL DEFAULT(1),
        CONSTRAINT UQ_Projects_ProjectCode UNIQUE (ProjectCode),
        CONSTRAINT FK_Project_Department FOREIGN KEY (OwningDepartmentId) REFERENCES core.Departments(DepartmentId)
    );
END
ELSE
BEGIN
    PRINT 'core.Projects table already exists.'
END
GO

/* 3) Create AppUsers Table (if missing) */
IF OBJECT_ID(N'core.AppUsers', N'U') IS NULL
BEGIN
    PRINT 'Creating core.AppUsers table...'
    CREATE TABLE core.AppUsers (
        UserId INT IDENTITY(1,1) PRIMARY KEY,
        UserName NVARCHAR(100) NOT NULL,
        Password NVARCHAR(128) NULL,
        Role NVARCHAR(20) NULL,
        IsActive BIT NOT NULL DEFAULT(1),
        EntityId INT NULL,
        DepartmentId INT NULL,
        CONSTRAINT UQ_AppUsers_UserName UNIQUE (UserName),
        CONSTRAINT FK_AppUser_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId),
        CONSTRAINT FK_AppUser_Department FOREIGN KEY (DepartmentId) REFERENCES core.Departments(DepartmentId)
    );
    
    IF NOT EXISTS (SELECT 1 FROM core.AppUsers WHERE UserName = 'admin')
    BEGIN
        INSERT INTO core.AppUsers (UserName, Password, Role, IsActive, EntityId, DepartmentId)
        VALUES ('admin', 'admin', 'SYSADMIN', 1, NULL, NULL);
    END
END
GO

/* 3b) Update AppUsers Table to include EntityId and backfill */
IF OBJECT_ID(N'core.AppUsers', N'U') IS NOT NULL
AND NOT EXISTS (
    SELECT * FROM sys.columns
    WHERE object_id = OBJECT_ID(N'core.AppUsers') AND name = 'EntityId'
)
BEGIN
    PRINT 'Adding EntityId to core.AppUsers...'
    ALTER TABLE core.AppUsers ADD EntityId INT NULL;
    ALTER TABLE core.AppUsers ADD CONSTRAINT FK_AppUser_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId);

    UPDATE u
    SET EntityId = d.EntityId
    FROM core.AppUsers u
    JOIN core.Departments d ON u.DepartmentId = d.DepartmentId
    WHERE u.EntityId IS NULL AND u.DepartmentId IS NOT NULL;
END
GO

/* 3c) Ensure the default admin user is SYSADMIN and unscoped */
IF OBJECT_ID(N'core.AppUsers', N'U') IS NOT NULL
BEGIN
    UPDATE core.AppUsers
    SET Role = 'SYSADMIN', EntityId = NULL, DepartmentId = NULL
    WHERE UserName = 'admin'
      AND (Role IS NULL OR Role IN ('ADMIN', 'SYSADMIN'))
      AND (EntityId IS NULL OR EntityId <= 0)
      AND (DepartmentId IS NULL OR DepartmentId <= 0);
END
GO

/* 4) Update BudgetLines Table to include ProjectId */
IF NOT EXISTS (
    SELECT * FROM sys.columns 
    WHERE object_id = OBJECT_ID(N'core.BudgetLines') AND name = 'ProjectId'
)
BEGIN
    PRINT 'Adding ProjectId to core.BudgetLines...'
    ALTER TABLE core.BudgetLines ADD ProjectId INT NULL;
    
    ALTER TABLE core.BudgetLines 
    ADD CONSTRAINT FK_BL_Project FOREIGN KEY (ProjectId) REFERENCES core.Projects(ProjectId);
END
GO

/* 5) Update BudgetLines Table to include Description */
IF NOT EXISTS (
    SELECT * FROM sys.columns 
    WHERE object_id = OBJECT_ID(N'core.BudgetLines') AND name = 'Description'
)
BEGIN
    PRINT 'Adding Description to core.BudgetLines...'
    ALTER TABLE core.BudgetLines ADD Description NVARCHAR(300) NOT NULL DEFAULT '';
END
GO

/* 5c) Update BudgetLines Table to include CapexAssetType */
IF OBJECT_ID(N'core.BudgetLines', N'U') IS NOT NULL
AND COL_LENGTH('core.BudgetLines', 'CapexAssetType') IS NULL
BEGIN
    PRINT 'Adding CapexAssetType to core.BudgetLines...'
    ALTER TABLE core.BudgetLines ADD CapexAssetType NVARCHAR(20) NULL;
END
GO

IF OBJECT_ID(N'core.BudgetLines', N'U') IS NOT NULL
AND COL_LENGTH('core.BudgetLines', 'CapexAssetType') IS NOT NULL
BEGIN
    UPDATE b
    SET CapexAssetType = 'NEW'
    FROM core.BudgetLines b
    WHERE b.CapexAssetType IS NULL;
END
GO

/* 5b) Create BudgetLineDocuments Table */
IF OBJECT_ID(N'core.BudgetLineDocuments', N'U') IS NULL
BEGIN
    PRINT 'Creating core.BudgetLineDocuments table...'
    CREATE TABLE core.BudgetLineDocuments (
        BudgetLineId BIGINT NOT NULL PRIMARY KEY,
        FileName NVARCHAR(260) NOT NULL,
        ContentType NVARCHAR(100) NOT NULL,
        SizeBytes INT NOT NULL,
        Content VARBINARY(MAX) NOT NULL,
        UploadedAt DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
        UploadedBy NVARCHAR(100) NULL,
        CONSTRAINT FK_BudgetLineDocuments_BudgetLines FOREIGN KEY (BudgetLineId)
            REFERENCES core.BudgetLines(BudgetLineId) ON DELETE CASCADE
    );
END
GO

/* 6) Create AuditLogs Table */
IF OBJECT_ID(N'core.AuditLogs', N'U') IS NULL
BEGIN
    PRINT 'Creating core.AuditLogs table...'
    CREATE TABLE core.AuditLogs (
        AuditLogId BIGINT IDENTITY(1,1) PRIMARY KEY,
        Timestamp DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
        UserName NVARCHAR(100) NOT NULL,
        Action NVARCHAR(50) NOT NULL,      -- LOGIN, INSERT, UPDATE, DELETE
        EntityName NVARCHAR(100) NULL,     -- Table name
        RecordId NVARCHAR(100) NULL,       -- ID of the record
        Details NVARCHAR(MAX) NULL         -- Description or serialized data
    );
END
GO

/* 6b) Update HrEmployeeCosts Table to include GLCode */
IF EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'core.HrEmployeeCosts') AND type = N'U')
AND NOT EXISTS (
    SELECT * FROM sys.columns
    WHERE object_id = OBJECT_ID(N'core.HrEmployeeCosts') AND name = 'GLCode'
)
BEGIN
    PRINT 'Adding GLCode to core.HrEmployeeCosts...'
    ALTER TABLE core.HrEmployeeCosts ADD GLCode NVARCHAR(30) NOT NULL DEFAULT '';
END
GO

/* 6c) Update HrEmployeeCosts Table to include GLKind */
IF EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'core.HrEmployeeCosts') AND type = N'U')
AND NOT EXISTS (
    SELECT * FROM sys.columns
    WHERE object_id = OBJECT_ID(N'core.HrEmployeeCosts') AND name = 'GLKind'
)
BEGIN
    PRINT 'Adding GLKind to core.HrEmployeeCosts...'
    ALTER TABLE core.HrEmployeeCosts ADD GLKind NVARCHAR(20) NOT NULL DEFAULT '';
END
GO

/* 7) Create HrEmployeeCosts Table */
IF OBJECT_ID(N'core.HrEmployeeCosts', N'U') IS NULL
BEGIN
    PRINT 'Creating core.HrEmployeeCosts table...'
    CREATE TABLE core.HrEmployeeCosts (
        EmployeeCostId INT IDENTITY(1,1) PRIMARY KEY,
        BudgetYear INT NOT NULL,
        EmployeeId NVARCHAR(50) NOT NULL,
        EmployeeName NVARCHAR(200) NOT NULL,
        GLCode NVARCHAR(30) NOT NULL DEFAULT '',
        GLKind NVARCHAR(20) NOT NULL DEFAULT '',
        EntityId INT NULL,
        EntityName NVARCHAR(200) NOT NULL,
        DepartmentId INT NULL,
        DepartmentName NVARCHAR(200) NOT NULL,
        AnnualCost DECIMAL(18,2) NOT NULL,
        ImportedAt DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
        ImportedBy NVARCHAR(100) NULL,
        SourceFile NVARCHAR(260) NULL,
        CONSTRAINT UQ_HrEmployeeCosts_YearEmployee UNIQUE (BudgetYear, EmployeeId),
        CONSTRAINT FK_HrEmployeeCosts_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId),
        CONSTRAINT FK_HrEmployeeCosts_Department FOREIGN KEY (DepartmentId) REFERENCES core.Departments(DepartmentId)
    );
END
GO

/* 8) Create HrEmployeeCostAllocations Table */
IF OBJECT_ID(N'core.HrEmployeeCostAllocations', N'U') IS NULL
BEGIN
    PRINT 'Creating core.HrEmployeeCostAllocations table...'
    CREATE TABLE core.HrEmployeeCostAllocations (
        AllocationId BIGINT IDENTITY(1,1) PRIMARY KEY,
        EmployeeCostId INT NOT NULL,
        ActivityId INT NOT NULL,
        ProjectId INT NULL,
        AllocatedAmount DECIMAL(18,2) NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
        CreatedBy NVARCHAR(100) NOT NULL,
        CONSTRAINT FK_HrAlloc_EmployeeCost FOREIGN KEY (EmployeeCostId) REFERENCES core.HrEmployeeCosts(EmployeeCostId),
        CONSTRAINT FK_HrAlloc_Activity FOREIGN KEY (ActivityId) REFERENCES core.Activities(ActivityId),
        CONSTRAINT FK_HrAlloc_Project FOREIGN KEY (ProjectId) REFERENCES core.Projects(ProjectId)
    );
END
GO

/* 9) Create BudgetSubmissions Table */
IF OBJECT_ID(N'core.BudgetSubmissions', N'U') IS NULL
BEGIN
    PRINT 'Creating core.BudgetSubmissions table...'
    CREATE TABLE core.BudgetSubmissions (
        SubmissionId BIGINT IDENTITY(1,1) PRIMARY KEY,
        BudgetYear INT NOT NULL,
        EntityId INT NOT NULL,
        DepartmentId INT NOT NULL,
        CategoryId INT NOT NULL,
        VersionNo INT NOT NULL DEFAULT(1),
        ParentSubmissionId BIGINT NULL,
        Status NVARCHAR(20) NOT NULL DEFAULT('Draft'),
        SubmittedAt DATETIME2 NULL,
        SubmittedBy NVARCHAR(100) NULL,
        ApprovedAt DATETIME2 NULL,
        ApprovedBy NVARCHAR(100) NULL,
        ApprovalNote NVARCHAR(500) NULL,
        ReturnedAt DATETIME2 NULL,
        ReturnedBy NVARCHAR(100) NULL,
        ReturnNote NVARCHAR(500) NULL,
        SentToCentralAt DATETIME2 NULL,
        SentToCentralBy NVARCHAR(100) NULL,
        FinalizedAt DATETIME2 NULL,
        FinalizedBy NVARCHAR(100) NULL,
        CONSTRAINT UQ_BudgetSubmissions_ScopeVersion UNIQUE (BudgetYear, EntityId, DepartmentId, CategoryId, VersionNo),
        CONSTRAINT FK_BudgetSubmissions_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId),
        CONSTRAINT FK_BudgetSubmissions_Department FOREIGN KEY (DepartmentId) REFERENCES core.Departments(DepartmentId),
        CONSTRAINT FK_BudgetSubmissions_Category FOREIGN KEY (CategoryId) REFERENCES core.Categories(CategoryId),
        CONSTRAINT FK_BudgetSubmissions_Parent FOREIGN KEY (ParentSubmissionId) REFERENCES core.BudgetSubmissions(SubmissionId)
    );
END
GO

/* 9b) Upgrade BudgetSubmissions for versioning (existing databases) */
IF OBJECT_ID(N'core.BudgetSubmissions', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('core.BudgetSubmissions', 'VersionNo') IS NULL
    BEGIN
        ALTER TABLE core.BudgetSubmissions ADD VersionNo INT NOT NULL CONSTRAINT DF_BudgetSubmissions_VersionNo DEFAULT(1);
    END

    IF COL_LENGTH('core.BudgetSubmissions', 'ParentSubmissionId') IS NULL
    BEGIN
        ALTER TABLE core.BudgetSubmissions ADD ParentSubmissionId BIGINT NULL;
    END

    IF COL_LENGTH('core.BudgetSubmissions', 'ReturnedAt') IS NULL
    BEGIN
        ALTER TABLE core.BudgetSubmissions ADD ReturnedAt DATETIME2 NULL;
    END

    IF COL_LENGTH('core.BudgetSubmissions', 'ReturnedBy') IS NULL
    BEGIN
        ALTER TABLE core.BudgetSubmissions ADD ReturnedBy NVARCHAR(100) NULL;
    END

    IF COL_LENGTH('core.BudgetSubmissions', 'ReturnNote') IS NULL
    BEGIN
        ALTER TABLE core.BudgetSubmissions ADD ReturnNote NVARCHAR(500) NULL;
    END

    IF COL_LENGTH('core.BudgetSubmissions', 'SysApprovedAt') IS NULL
    BEGIN
        ALTER TABLE core.BudgetSubmissions ADD SysApprovedAt DATETIME2 NULL;
    END

    IF COL_LENGTH('core.BudgetSubmissions', 'SysApprovedBy') IS NULL
    BEGIN
        ALTER TABLE core.BudgetSubmissions ADD SysApprovedBy NVARCHAR(100) NULL;
    END

    IF COL_LENGTH('core.BudgetSubmissions', 'SysApprovalNote') IS NULL
    BEGIN
        ALTER TABLE core.BudgetSubmissions ADD SysApprovalNote NVARCHAR(500) NULL;
    END

    IF NOT EXISTS (
        SELECT 1
        FROM sys.foreign_keys
        WHERE name = N'FK_BudgetSubmissions_Parent'
          AND parent_object_id = OBJECT_ID(N'core.BudgetSubmissions')
    )
    BEGIN
        ALTER TABLE core.BudgetSubmissions
        ADD CONSTRAINT FK_BudgetSubmissions_Parent FOREIGN KEY (ParentSubmissionId) REFERENCES core.BudgetSubmissions(SubmissionId);
    END

    IF EXISTS (
        SELECT 1
        FROM sys.key_constraints
        WHERE name = N'UQ_BudgetSubmissions_Scope'
          AND parent_object_id = OBJECT_ID(N'core.BudgetSubmissions')
    )
    BEGIN
        ALTER TABLE core.BudgetSubmissions DROP CONSTRAINT UQ_BudgetSubmissions_Scope;
    END

    IF EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE name = N'UQ_BudgetSubmissions_Scope'
          AND object_id = OBJECT_ID(N'core.BudgetSubmissions')
    )
    BEGIN
        DROP INDEX UQ_BudgetSubmissions_Scope ON core.BudgetSubmissions;
    END

    IF NOT EXISTS (
        SELECT 1
        FROM sys.key_constraints
        WHERE name = N'UQ_BudgetSubmissions_ScopeVersion'
          AND parent_object_id = OBJECT_ID(N'core.BudgetSubmissions')
    )
    BEGIN
        ALTER TABLE core.BudgetSubmissions
        ADD CONSTRAINT UQ_BudgetSubmissions_ScopeVersion UNIQUE (BudgetYear, EntityId, DepartmentId, CategoryId, VersionNo);
    END
END
GO

/* 9c) Create BudgetSubmissionLines snapshot table */
IF OBJECT_ID(N'core.BudgetSubmissionLines', N'U') IS NULL
BEGIN
    PRINT 'Creating core.BudgetSubmissionLines table...'
    CREATE TABLE core.BudgetSubmissionLines (
        SubmissionLineId BIGINT IDENTITY(1,1) PRIMARY KEY,
        SubmissionId BIGINT NOT NULL,
        SourceBudgetLineId BIGINT NOT NULL,
        BudgetYear INT NOT NULL,
        EntityId INT NOT NULL,
        DepartmentId INT NOT NULL,
        CategoryId INT NOT NULL,
        ItemId INT NOT NULL,
        ProgramId INT NULL,
        ActivityId INT NULL,
        ProjectId INT NULL,
        Quantity DECIMAL(18,4) NOT NULL,
        UnitPrice DECIMAL(18,4) NOT NULL,
        Amount DECIMAL(18,2) NOT NULL,
        DistributionMode NVARCHAR(10) NOT NULL,
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
        F1_Amount DECIMAL(18,2) NOT NULL,
        F2_Percent DECIMAL(9,4) NOT NULL,
        F2_Amount DECIMAL(18,2) NOT NULL,
        Dep_Method NVARCHAR(20) NOT NULL,
        Dep_LifeMonths INT NOT NULL,
        Dep_StartMonth TINYINT NOT NULL,
        CapexAssetType NVARCHAR(20) NULL,
        Notes NVARCHAR(500) NULL,
        Description NVARCHAR(300) NOT NULL,
        CreatedAt DATETIME2 NOT NULL,
        CreatedBy NVARCHAR(100) NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy NVARCHAR(100) NULL,
        DocFileName NVARCHAR(260) NULL,
        DocContentType NVARCHAR(100) NULL,
        DocSizeBytes INT NULL,
        DocContent VARBINARY(MAX) NULL,
        DocUploadedAt DATETIME2 NULL,
        DocUploadedBy NVARCHAR(100) NULL,
        SnapshottedAt DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
        SnapshottedBy NVARCHAR(100) NULL,
        CONSTRAINT UQ_BudgetSubmissionLines UNIQUE (SubmissionId, SourceBudgetLineId),
        CONSTRAINT FK_BudgetSubmissionLines_Submission FOREIGN KEY (SubmissionId) REFERENCES core.BudgetSubmissions(SubmissionId)
    );
END
GO

IF OBJECT_ID(N'core.BudgetSubmissionLines', N'U') IS NOT NULL
AND COL_LENGTH('core.BudgetSubmissionLines', 'CapexAssetType') IS NULL
BEGIN
    PRINT 'Adding CapexAssetType to core.BudgetSubmissionLines...'
    ALTER TABLE core.BudgetSubmissionLines ADD CapexAssetType NVARCHAR(20) NULL;
END
GO

IF OBJECT_ID(N'core.BudgetSubmissionLines', N'U') IS NOT NULL
AND COL_LENGTH('core.BudgetSubmissionLines', 'CapexAssetType') IS NOT NULL
BEGIN
    UPDATE l
    SET CapexAssetType = 'NEW'
    FROM core.BudgetSubmissionLines l
    WHERE l.CapexAssetType IS NULL;
END
GO

/* 9d) Create BudgetRevisionRequests table */
IF OBJECT_ID(N'core.BudgetRevisionRequests', N'U') IS NULL
BEGIN
    PRINT 'Creating core.BudgetRevisionRequests table...'
    CREATE TABLE core.BudgetRevisionRequests (
        RequestId BIGINT IDENTITY(1,1) PRIMARY KEY,
        SubmissionId BIGINT NOT NULL,
        ActionType NVARCHAR(20) NOT NULL,
        Note NVARCHAR(500) NULL,
        RequestedAt DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
        RequestedBy NVARCHAR(100) NULL,
        CONSTRAINT FK_BudgetRevisionRequests_Submission FOREIGN KEY (SubmissionId) REFERENCES core.BudgetSubmissions(SubmissionId)
    );
END
GO

/* 10) Create InternalMessages Table */
IF OBJECT_ID(N'core.InternalMessages', N'U') IS NULL
BEGIN
    PRINT 'Creating core.InternalMessages table...'
    CREATE TABLE core.InternalMessages (
        MessageId BIGINT IDENTITY(1,1) PRIMARY KEY,
        FromUser NVARCHAR(100) NOT NULL,
        FromEntityCode NVARCHAR(20) NULL,
        FromDeptCode NVARCHAR(20) NULL,
        Subject NVARCHAR(200) NOT NULL,
        Body NVARCHAR(MAX) NOT NULL,
        Status NVARCHAR(20) NOT NULL DEFAULT('Pending'),
        CreatedAt DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
        ReadAt DATETIME2 NULL,
        ReadBy NVARCHAR(100) NULL,
        AdminResponse NVARCHAR(MAX) NULL,
        RespondedAt DATETIME2 NULL,
        RespondedBy NVARCHAR(100) NULL
    );
END
GO

/* 10b) Create PasswordResetRequests Table */
IF OBJECT_ID(N'core.PasswordResetRequests', N'U') IS NULL
BEGIN
    PRINT 'Creating core.PasswordResetRequests table...'
    CREATE TABLE core.PasswordResetRequests (
        ResetRequestId BIGINT IDENTITY(1,1) PRIMARY KEY,
        UserName NVARCHAR(100) NOT NULL,
        UserId INT NULL,
        EntityId INT NULL,
        ContactInfo NVARCHAR(200) NULL,
        Note NVARCHAR(500) NULL,
        Status NVARCHAR(20) NOT NULL DEFAULT('Pending'),
        RequestSource NVARCHAR(20) NULL,
        RequestedAt DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
        Token NVARCHAR(128) NULL,
        TokenExpiresAt DATETIME2 NULL,
        TokenUsedAt DATETIME2 NULL,
        IssuedAt DATETIME2 NULL,
        IssuedBy NVARCHAR(100) NULL,
        CompletedAt DATETIME2 NULL,
        RejectedAt DATETIME2 NULL,
        RejectedBy NVARCHAR(100) NULL,
        AdminNote NVARCHAR(500) NULL
    );
    CREATE INDEX IX_PasswordResetRequests_Token ON core.PasswordResetRequests(Token);
END
GO

/* 11) Create DOF_CombindBudget_Final Table */
IF OBJECT_ID(N'core.DOF_CombindBudget_Final', N'U') IS NULL
BEGIN
    PRINT 'Creating core.DOF_CombindBudget_Final table...'
    CREATE TABLE core.DOF_CombindBudget_Final (
        FinalBudgetLineId BIGINT IDENTITY(1,1) PRIMARY KEY,
        SubmissionId BIGINT NOT NULL,
        SourceBudgetLineId BIGINT NOT NULL,
        BudgetYear INT NOT NULL,
        EntityId INT NOT NULL,
        DepartmentId INT NOT NULL,
        CategoryId INT NOT NULL,
        ItemId INT NOT NULL,
        ProgramId INT NULL,
        ActivityId INT NULL,
        ProjectId INT NULL,
        Quantity DECIMAL(18,4) NOT NULL,
        UnitPrice DECIMAL(18,4) NOT NULL,
        Amount DECIMAL(18,2) NOT NULL,
        DistributionMode NVARCHAR(10) NOT NULL,
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
        F1_Amount DECIMAL(18,2) NOT NULL,
        F2_Percent DECIMAL(9,4) NOT NULL,
        F2_Amount DECIMAL(18,2) NOT NULL,
        Dep_Method NVARCHAR(20) NOT NULL,
        Dep_LifeMonths INT NOT NULL,
        Dep_StartMonth TINYINT NOT NULL,
        CapexAssetType NVARCHAR(20) NULL,
        Notes NVARCHAR(500) NULL,
        Description NVARCHAR(300) NOT NULL,
        CreatedAt DATETIME2 NOT NULL,
        CreatedBy NVARCHAR(100) NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy NVARCHAR(100) NULL,
        DocFileName NVARCHAR(260) NULL,
        DocContentType NVARCHAR(100) NULL,
        DocSizeBytes INT NULL,
        DocContent VARBINARY(MAX) NULL,
        DocUploadedAt DATETIME2 NULL,
        DocUploadedBy NVARCHAR(100) NULL,
        ApprovedAt DATETIME2 NULL,
        ApprovedBy NVARCHAR(100) NULL,
        ApprovalNote NVARCHAR(500) NULL,
        CopiedAt DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT UQ_DOF_CombindBudget_Final UNIQUE (SubmissionId, SourceBudgetLineId),
        CONSTRAINT FK_Final_Submission FOREIGN KEY (SubmissionId) REFERENCES core.BudgetSubmissions(SubmissionId)
    );
END
GO

IF OBJECT_ID(N'core.DOF_CombindBudget_Final', N'U') IS NOT NULL
AND COL_LENGTH('core.DOF_CombindBudget_Final', 'CapexAssetType') IS NULL
BEGIN
    PRINT 'Adding CapexAssetType to core.DOF_CombindBudget_Final...'
    ALTER TABLE core.DOF_CombindBudget_Final ADD CapexAssetType NVARCHAR(20) NULL;
END
GO

/* 12) Create Historical GL Actuals Table */
IF OBJECT_ID(N'core.HistoricalGlActuals', N'U') IS NULL
BEGIN
    PRINT 'Creating core.HistoricalGlActuals table...'
    CREATE TABLE core.HistoricalGlActuals (
        HistoricalActualId BIGINT IDENTITY(1,1) PRIMARY KEY,
        BudgetYear INT NOT NULL,
        EntityId INT NOT NULL,
        DepartmentId INT NOT NULL,
        GLCode NVARCHAR(30) NOT NULL,
        GLType NVARCHAR(20) NULL,
        Amount DECIMAL(18,2) NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
        CreatedBy NVARCHAR(100) NULL,
        SourceFile NVARCHAR(260) NULL,
        CONSTRAINT UQ_HistoricalGlActuals_Scope UNIQUE (BudgetYear, EntityId, DepartmentId, GLCode),
        CONSTRAINT FK_HistoricalGlActuals_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId),
        CONSTRAINT FK_HistoricalGlActuals_Department FOREIGN KEY (DepartmentId) REFERENCES core.Departments(DepartmentId)
    );
END
GO

IF OBJECT_ID(N'core.HistoricalGlActuals', N'U') IS NOT NULL
AND COL_LENGTH('core.HistoricalGlActuals', 'GLType') IS NULL
BEGIN
    PRINT 'Adding GLType to core.HistoricalGlActuals...'
    ALTER TABLE core.HistoricalGlActuals ADD GLType NVARCHAR(20) NULL;
END
GO

/* 12b) Create Mid-Year GL Actuals + Forecast Table */
IF OBJECT_ID(N'core.MidYearGlActualForecasts', N'U') IS NULL
BEGIN
    PRINT 'Creating core.MidYearGlActualForecasts table...'
    CREATE TABLE core.MidYearGlActualForecasts (
        MidYearId BIGINT IDENTITY(1,1) PRIMARY KEY,
        BudgetYear INT NOT NULL,
        EntityId INT NOT NULL,
        GLCode NVARCHAR(30) NOT NULL,
        GLType NVARCHAR(20) NOT NULL,
        ActualH1Amount DECIMAL(18,2) NOT NULL,
        ForecastH2Amount DECIMAL(18,2) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
        CreatedBy NVARCHAR(100) NULL,
        ForecastUpdatedAt DATETIME2 NULL,
        ForecastUpdatedBy NVARCHAR(100) NULL,
        SourceFile NVARCHAR(260) NULL,
        CONSTRAINT UQ_MidYearGlActualForecasts_Scope UNIQUE (BudgetYear, EntityId, GLCode),
        CONSTRAINT FK_MidYearGlActualForecasts_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId)
    );
END
GO

/* 12) Create What-If Scenario Tables */
IF OBJECT_ID(N'core.WhatIfScenarios', N'U') IS NULL
BEGIN
    PRINT 'Creating core.WhatIfScenarios table...'
    CREATE TABLE core.WhatIfScenarios (
        ScenarioId INT IDENTITY(1,1) PRIMARY KEY,
        BudgetYear INT NOT NULL,
        EntityId INT NULL,
        DepartmentId INT NULL,
        ScenarioName NVARCHAR(200) NOT NULL,
        IsActive BIT NOT NULL DEFAULT(1),
        CreatedAt DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
        CreatedBy NVARCHAR(100) NOT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy NVARCHAR(100) NULL,
        CONSTRAINT UQ_WhatIfScenarios_ScopeName UNIQUE (BudgetYear, EntityId, DepartmentId, ScenarioName),
        CONSTRAINT FK_WhatIfScenarios_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId),
        CONSTRAINT FK_WhatIfScenarios_Department FOREIGN KEY (DepartmentId) REFERENCES core.Departments(DepartmentId)
    );
END
GO

IF OBJECT_ID(N'core.WhatIfScenarioDefaults', N'U') IS NULL
BEGIN
    PRINT 'Creating core.WhatIfScenarioDefaults table...'
    CREATE TABLE core.WhatIfScenarioDefaults (
        ScenarioId INT NOT NULL PRIMARY KEY,
        CostInflationRate DECIMAL(9,4) NOT NULL DEFAULT(0),
        RevenueGrowthRate DECIMAL(9,4) NOT NULL DEFAULT(0),
        CONSTRAINT FK_WhatIfScenarioDefaults_Scenario FOREIGN KEY (ScenarioId) REFERENCES core.WhatIfScenarios(ScenarioId) ON DELETE CASCADE
    );
END
GO

IF OBJECT_ID(N'core.WhatIfScenarioProjectRates', N'U') IS NULL
BEGIN
    PRINT 'Creating core.WhatIfScenarioProjectRates table...'
    CREATE TABLE core.WhatIfScenarioProjectRates (
        ScenarioProjectRateId BIGINT IDENTITY(1,1) PRIMARY KEY,
        ScenarioId INT NOT NULL,
        ProjectId INT NOT NULL,
        CostInflationRate DECIMAL(9,4) NULL,
        RevenueGrowthRate DECIMAL(9,4) NULL,
        CONSTRAINT UQ_WhatIfScenarioProjectRates UNIQUE (ScenarioId, ProjectId),
        CONSTRAINT FK_WhatIfScenarioProjectRates_Scenario FOREIGN KEY (ScenarioId) REFERENCES core.WhatIfScenarios(ScenarioId) ON DELETE CASCADE,
        CONSTRAINT FK_WhatIfScenarioProjectRates_Project FOREIGN KEY (ProjectId) REFERENCES core.Projects(ProjectId)
    );
END
GO

/* Ensure core.Categories exists and is seeded with the codes Budget Entry relies on.
   The app also self-seeds these at startup; this block is a manual fallback. */
IF OBJECT_ID(N'core.Categories', N'U') IS NULL
BEGIN
    PRINT 'Creating core.Categories table...'
    CREATE TABLE core.Categories (
        CategoryId INT IDENTITY(1,1) PRIMARY KEY,
        CategoryCode NVARCHAR(30) NOT NULL,
        CategoryName NVARCHAR(200) NOT NULL,
        CONSTRAINT UQ_Categories_CategoryCode UNIQUE (CategoryCode)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM core.Categories WHERE UPPER(CategoryCode) = 'REVENUE')
    INSERT INTO core.Categories (CategoryCode, CategoryName) VALUES ('REVENUE', 'Revenue');

IF NOT EXISTS (SELECT 1 FROM core.Categories WHERE UPPER(CategoryCode) = 'OPEX')
    INSERT INTO core.Categories (CategoryCode, CategoryName) VALUES ('OPEX', 'Operating Expenditure');

IF NOT EXISTS (SELECT 1 FROM core.Categories WHERE UPPER(CategoryCode) = 'CAPEX')
    INSERT INTO core.Categories (CategoryCode, CategoryName) VALUES ('CAPEX', 'Capital Expenditure');
GO
