using System;
using System.Collections.Generic;
using System.Linq;
using GovBudget.Models;
using GovBudget.Utils;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Services
{
    // Creates the role/permission tables if missing and seeds defaults that reproduce the
    // access rules the application had before permissions became configurable. Safe to run
    // on every start: table creation is guarded and seeding only inserts what is absent, so
    // administrator edits are never overwritten.
    public static class PermissionSeeder
    {
        private const string SchemaSql = """
IF OBJECT_ID(N'core.AppRoles', N'U') IS NULL
BEGIN
    CREATE TABLE core.AppRoles (
        RoleId INT IDENTITY(1,1) PRIMARY KEY,
        RoleCode NVARCHAR(20) NOT NULL,
        RoleName NVARCHAR(100) NOT NULL,
        Description NVARCHAR(300) NULL,
        IsSystem BIT NOT NULL DEFAULT(0),
        IsEntityScoped BIT NOT NULL DEFAULT(1),
        IsActive BIT NOT NULL DEFAULT(1),
        CONSTRAINT UQ_AppRoles_RoleCode UNIQUE (RoleCode)
    );
END;

IF OBJECT_ID(N'core.RolePermissions', N'U') IS NULL
BEGIN
    CREATE TABLE core.RolePermissions (
        RolePermissionId INT IDENTITY(1,1) PRIMARY KEY,
        RoleId INT NOT NULL,
        FormKey NVARCHAR(50) NOT NULL,
        CanView BIT NOT NULL DEFAULT(0),
        CanAdd BIT NOT NULL DEFAULT(0),
        CanEdit BIT NOT NULL DEFAULT(0),
        CanDelete BIT NOT NULL DEFAULT(0),
        UpdatedAt DATETIME2 NULL,
        UpdatedBy NVARCHAR(100) NULL,
        CONSTRAINT UQ_RolePermissions_RoleForm UNIQUE (RoleId, FormKey),
        CONSTRAINT FK_RolePermissions_Role FOREIGN KEY (RoleId)
            REFERENCES core.AppRoles(RoleId) ON DELETE CASCADE
    );
END;
""";

        public static void Run(GovBudgetContext db)
        {
            db.Database.ExecuteSqlRaw(SchemaSql);

            SeedRoles(db);
            SeedPermissions(db);
        }

        private static void SeedRoles(GovBudgetContext db)
        {
            var wanted = new[]
            {
                new AppRoles
                {
                    RoleCode = "SYSADMIN", RoleName = "System Administrator", IsSystem = true,
                    IsEntityScoped = false, IsActive = true,
                    Description = "Full access to every form and every entity. Always unrestricted."
                },
                new AppRoles
                {
                    RoleCode = "ADMIN", RoleName = "Entity Administrator", IsSystem = true,
                    IsEntityScoped = true, IsActive = true,
                    Description = "Administers their own entity only."
                },
                new AppRoles
                {
                    RoleCode = "USER", RoleName = "Budget User", IsSystem = true,
                    IsEntityScoped = true, IsActive = true,
                    Description = "Prepares the budget for their own cost center."
                },
                new AppRoles
                {
                    RoleCode = "VIEWER", RoleName = "Reviewer (view only)", IsSystem = false,
                    IsEntityScoped = true, IsActive = true,
                    Description = "Can open and review screens for their entity but cannot add, edit or delete."
                }
            };

            var existing = db.AppRoles.Select(r => r.RoleCode).ToList()
                .Select(c => (c ?? "").Trim().ToUpperInvariant())
                .ToHashSet();

            var toAdd = wanted.Where(r => !existing.Contains(r.RoleCode)).ToList();
            if (toAdd.Count > 0)
            {
                db.AppRoles.AddRange(toAdd);
                db.SaveChanges();
            }
        }

        private static void SeedPermissions(GovBudgetContext db)
        {
            var roles = db.AppRoles.ToList()
                .ToDictionary(r => (r.RoleCode ?? "").Trim().ToUpperInvariant(), r => r.RoleId);

            var already = db.RolePermissions
                .Select(p => new { p.RoleId, p.FormKey })
                .ToList()
                .Select(p => p.RoleId + "|" + p.FormKey.ToUpperInvariant())
                .ToHashSet();

            var rows = new List<RolePermissions>();

            void Grant(string roleCode, string formKey, bool view, bool add, bool edit, bool del)
            {
                if (!roles.TryGetValue(roleCode, out var roleId)) return;
                if (already.Contains(roleId + "|" + formKey.ToUpperInvariant())) return;

                rows.Add(new RolePermissions
                {
                    RoleId = roleId,
                    FormKey = formKey,
                    CanView = view,
                    CanAdd = add,
                    CanEdit = edit,
                    CanDelete = del,
                    UpdatedAt = DateTime.UtcNow,
                    UpdatedBy = "seed"
                });
            }

            void GrantFull(string roleCode, params string[] formKeys)
            {
                foreach (var f in formKeys) Grant(roleCode, f, true, true, true, true);
            }

            void GrantViewOnly(string roleCode, params string[] formKeys)
            {
                foreach (var f in formKeys) Grant(roleCode, f, true, false, false, false);
            }

            // SYSADMIN is hard-wired to full access in PermissionService, but seed explicit
            // rows so the grid shows it correctly.
            GrantFull("SYSADMIN", AppForms.All.Select(f => f.Key).ToArray());

            // ADMIN: everything they could reach before, except the rights screen itself.
            GrantFull("ADMIN", AppForms.All
                .Where(f => f.Key != AppForms.Roles)
                .Select(f => f.Key)
                .ToArray());

            // USER: budget preparation plus read access to reporting.
            GrantFull("USER", AppForms.BudgetSetup, AppForms.BudgetEntry, AppForms.HrAllocation, AppForms.MidYear);
            GrantFull("USER", AppForms.WhatIf, AppForms.Requests);
            GrantViewOnly("USER", AppForms.Reports, AppForms.BudgetVsActual, AppForms.Guides);

            // VIEWER: the review-only role — can see, can change nothing.
            GrantViewOnly("VIEWER",
                AppForms.BudgetSetup, AppForms.BudgetEntry, AppForms.HrAllocation, AppForms.MidYear,
                AppForms.Reports, AppForms.BudgetVsActual, AppForms.ManagementReview,
                AppForms.Performance, AppForms.Requests, AppForms.Guides);

            if (rows.Count > 0)
            {
                db.RolePermissions.AddRange(rows);
                db.SaveChanges();
            }
        }
    }
}
