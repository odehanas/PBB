/* Upload history / undo (Admin > Upload History).
   The application creates these tables itself at startup (Services/ImportHistoryUpgrade.cs).
   Run this script manually only if the app's database login is not allowed to CREATE TABLE.
   Safe to run more than once. Select the GovBudget database first. */
IF OBJECT_ID(N'core.ImportBatches', N'U') IS NULL
BEGIN
    CREATE TABLE core.ImportBatches (
        ImportBatchId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ImportBatches PRIMARY KEY,
        ImportType    NVARCHAR(30)  NOT NULL,
        BudgetYear    INT           NULL,
        EntityId      INT           NULL,
        Scope         NVARCHAR(200) NULL,
        SourceFile    NVARCHAR(260) NULL,
        ImportedBy    NVARCHAR(100) NULL,
        ImportedAt    DATETIME2     NOT NULL CONSTRAINT DF_ImportBatches_ImportedAt DEFAULT (SYSUTCDATETIME()),
        RowsCreated   INT           NOT NULL CONSTRAINT DF_ImportBatches_Created DEFAULT (0),
        RowsUpdated   INT           NOT NULL CONSTRAINT DF_ImportBatches_Updated DEFAULT (0),
        RowsDeleted   INT           NOT NULL CONSTRAINT DF_ImportBatches_Deleted DEFAULT (0),
        Status        NVARCHAR(20)  NOT NULL CONSTRAINT DF_ImportBatches_Status DEFAULT (N'APPLIED'),
        UndoneAt      DATETIME2     NULL,
        UndoneBy      NVARCHAR(100) NULL
    );
    CREATE INDEX IX_ImportBatches_Lookup ON core.ImportBatches (ImportType, BudgetYear, EntityId, ImportedAt DESC);
END
GO

IF OBJECT_ID(N'core.ImportBatchChanges', N'U') IS NULL
BEGIN
    CREATE TABLE core.ImportBatchChanges (
        ImportChangeId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ImportBatchChanges PRIMARY KEY,
        ImportBatchId  BIGINT        NOT NULL CONSTRAINT FK_ImportBatchChanges_Batch REFERENCES core.ImportBatches(ImportBatchId) ON DELETE CASCADE,
        TableName      NVARCHAR(60)  NOT NULL,
        ChangeType     NVARCHAR(10)  NOT NULL,
        RecordId       BIGINT        NOT NULL,
        BeforeJson     NVARCHAR(MAX) NULL
    );
    CREATE INDEX IX_ImportBatchChanges_Batch ON core.ImportBatchChanges (ImportBatchId);
    CREATE INDEX IX_ImportBatchChanges_Record ON core.ImportBatchChanges (TableName, RecordId);
END;
GO
