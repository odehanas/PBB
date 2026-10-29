using System;
using System.Collections.Generic;
using System.Linq;
using GovBudget.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GovBudget.Services
{
    // One-time, idempotent security upgrade that runs at startup:
    //   1. adds the credential/lockout columns when they are missing,
    //   2. converts any remaining clear-text password into a PBKDF2 hash and blanks the
    //      legacy column, so no readable password survives in the database.
    //
    // Existing users keep signing in with the password they already have - only the way it
    // is stored changes. Nobody is locked out and no reset is required.
    public static class SecurityUpgrade
    {
        private static readonly string[] SchemaStatements =
        {
            "IF COL_LENGTH('core.AppUsers','PasswordHash') IS NULL ALTER TABLE core.AppUsers ADD PasswordHash NVARCHAR(200) NULL;",
            "IF COL_LENGTH('core.AppUsers','PasswordUpdatedAt') IS NULL ALTER TABLE core.AppUsers ADD PasswordUpdatedAt DATETIME2 NULL;",
            "IF COL_LENGTH('core.AppUsers','MustChangePassword') IS NULL ALTER TABLE core.AppUsers ADD MustChangePassword BIT NOT NULL CONSTRAINT DF_AppUsers_MustChangePassword DEFAULT(0);",
            "IF COL_LENGTH('core.AppUsers','FailedLoginCount') IS NULL ALTER TABLE core.AppUsers ADD FailedLoginCount INT NOT NULL CONSTRAINT DF_AppUsers_FailedLoginCount DEFAULT(0);",
            "IF COL_LENGTH('core.AppUsers','LockoutEndUtc') IS NULL ALTER TABLE core.AppUsers ADD LockoutEndUtc DATETIME2 NULL;",
            "IF COL_LENGTH('core.AppUsers','LastLoginAt') IS NULL ALTER TABLE core.AppUsers ADD LastLoginAt DATETIME2 NULL;",
            "IF COL_LENGTH('core.AppUsers','SecurityStamp') IS NULL ALTER TABLE core.AppUsers ADD SecurityStamp NVARCHAR(64) NULL;",
            // The legacy clear-text column has to accept NULL so it can be emptied.
            "IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('core.AppUsers') AND name = 'Password' AND is_nullable = 0) ALTER TABLE core.AppUsers ALTER COLUMN Password NVARCHAR(128) NULL;",
            "IF COL_LENGTH('core.PasswordResetRequests','TokenHash') IS NULL ALTER TABLE core.PasswordResetRequests ADD TokenHash NVARCHAR(100) NULL;",
            "IF COL_LENGTH('core.PasswordResetRequests','TokenHash') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PasswordResetRequests_TokenHash' AND object_id = OBJECT_ID('core.PasswordResetRequests')) CREATE INDEX IX_PasswordResetRequests_TokenHash ON core.PasswordResetRequests(TokenHash);"
        };

        public static void Run(GovBudgetContext db, ILogger logger)
        {
            EnsureSchema(db, logger);
            MigratePasswords(db, logger);
            RetireClearTextTokens(db, logger);
        }

        private static void EnsureSchema(GovBudgetContext db, ILogger logger)
        {
            foreach (var sql in SchemaStatements)
            {
                try
                {
                    db.Database.ExecuteSqlRaw(sql);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Security schema statement failed: {Sql}", sql);
                }
            }
        }

        private static void MigratePasswords(GovBudgetContext db, ILogger logger)
        {
            try
            {
                var legacy = db.AppUsers
                    .Where(u => u.PasswordHash == null && u.Password != null && u.Password != "")
                    .ToList();

                if (legacy.Count == 0) return;

                foreach (var user in legacy)
                {
                    user.PasswordHash = PasswordHasher.Hash(user.Password!);
                    user.Password = null;                 // clear text removed
                    user.PasswordUpdatedAt ??= DateTime.UtcNow;
                    user.SecurityStamp ??= PasswordHasher.NewSecurityStamp();
                }

                db.AuditLogs.Add(new AuditLogs
                {
                    UserName = "SYSTEM",
                    Action = "UPDATE",
                    EntityName = "AppUsers",
                    RecordId = "",
                    Timestamp = DateTime.UtcNow,
                    Details = $"Security upgrade: hashed and removed clear-text passwords for {legacy.Count} user(s)."
                });

                db.SaveChanges();
                logger.LogWarning("Security upgrade: converted {Count} clear-text password(s) to PBKDF2 hashes.", legacy.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Password hash migration failed.");
            }
        }

        // Any reset link issued before this release still has its token in clear text.
        // Those rows are invalidated: the affected users simply request a new link.
        private static void RetireClearTextTokens(GovBudgetContext db, ILogger logger)
        {
            try
            {
                var stale = db.PasswordResetRequests
                    .Where(r => r.Token != null && r.TokenHash == null && r.TokenUsedAt == null)
                    .ToList();

                if (stale.Count == 0) return;

                foreach (var req in stale)
                {
                    req.Token = null;
                    req.TokenExpiresAt = DateTime.UtcNow.AddMinutes(-1);
                    if (req.Status == "LinkIssued") req.Status = "Pending";
                }

                db.SaveChanges();
                logger.LogWarning("Security upgrade: invalidated {Count} clear-text reset token(s).", stale.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reset token cleanup failed.");
            }
        }
    }
}
