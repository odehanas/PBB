/* =====================================================================
   GovBudget - Security hardening schema (idempotent, no GO separators)
   ---------------------------------------------------------------------
   Run once against the GovBudget database before or after deploying the
   hardened build. The application also applies these statements at
   startup, so running this script manually is optional but recommended
   when the runtime account is restricted to read/write only.

   Nothing here deletes data. Existing users keep their current password:
   the application hashes it on the first run and clears the clear-text
   column, so no reset is needed.
   ===================================================================== */

/* ---- core.AppUsers : credential + lockout columns ---- */

IF COL_LENGTH('core.AppUsers','PasswordHash') IS NULL
    ALTER TABLE core.AppUsers ADD PasswordHash NVARCHAR(200) NULL;

IF COL_LENGTH('core.AppUsers','PasswordUpdatedAt') IS NULL
    ALTER TABLE core.AppUsers ADD PasswordUpdatedAt DATETIME2 NULL;

IF COL_LENGTH('core.AppUsers','MustChangePassword') IS NULL
    ALTER TABLE core.AppUsers ADD MustChangePassword BIT NOT NULL
        CONSTRAINT DF_AppUsers_MustChangePassword DEFAULT(0);

IF COL_LENGTH('core.AppUsers','FailedLoginCount') IS NULL
    ALTER TABLE core.AppUsers ADD FailedLoginCount INT NOT NULL
        CONSTRAINT DF_AppUsers_FailedLoginCount DEFAULT(0);

IF COL_LENGTH('core.AppUsers','LockoutEndUtc') IS NULL
    ALTER TABLE core.AppUsers ADD LockoutEndUtc DATETIME2 NULL;

IF COL_LENGTH('core.AppUsers','LastLoginAt') IS NULL
    ALTER TABLE core.AppUsers ADD LastLoginAt DATETIME2 NULL;

IF COL_LENGTH('core.AppUsers','SecurityStamp') IS NULL
    ALTER TABLE core.AppUsers ADD SecurityStamp NVARCHAR(64) NULL;

/* The legacy clear-text column must accept NULL so it can be emptied. */
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('core.AppUsers')
             AND name = 'Password'
             AND is_nullable = 0)
    ALTER TABLE core.AppUsers ALTER COLUMN Password NVARCHAR(128) NULL;

/* ---- core.PasswordResetRequests : store only the token digest ---- */

IF COL_LENGTH('core.PasswordResetRequests','TokenHash') IS NULL
    ALTER TABLE core.PasswordResetRequests ADD TokenHash NVARCHAR(100) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_PasswordResetRequests_TokenHash'
                 AND object_id = OBJECT_ID('core.PasswordResetRequests'))
    CREATE INDEX IX_PasswordResetRequests_TokenHash
        ON core.PasswordResetRequests(TokenHash);

/* Any link issued before this release is invalidated (clear-text token). */
UPDATE core.PasswordResetRequests
   SET Token = NULL,
       TokenExpiresAt = DATEADD(MINUTE, -1, SYSUTCDATETIME()),
       Status = CASE WHEN Status = 'LinkIssued' THEN 'Pending' ELSE Status END
 WHERE Token IS NOT NULL
   AND TokenHash IS NULL
   AND TokenUsedAt IS NULL;

/* ---- Verification queries (run after the application has started once) ----

-- No clear-text password must remain:
SELECT COUNT(*) AS ClearTextPasswordsRemaining
  FROM core.AppUsers
 WHERE Password IS NOT NULL AND Password <> '';

-- Every active user must have a hash:
SELECT UserName, CASE WHEN PasswordHash LIKE 'PBKDF2-SHA256$%' THEN 'Hashed' ELSE 'MISSING' END AS PasswordState
  FROM core.AppUsers
 WHERE IsActive = 1
 ORDER BY UserName;

-- Failed sign-in attempts (new audit action):
SELECT TOP 100 Timestamp, UserName, Details
  FROM core.AuditLogs
 WHERE Action = 'LOGIN_FAILED'
 ORDER BY Timestamp DESC;

--------------------------------------------------------------------- */
