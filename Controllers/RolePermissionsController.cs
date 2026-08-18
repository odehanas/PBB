using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GovBudget.Models;
using GovBudget.Services;
using GovBudget.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Controllers
{
    // Roles & Rights. Restricted to SYSADMIN by policy as well as by form permission:
    // this screen grants access to every other screen, so it must never be delegated by
    // accident.
    [Authorize(Policy = "SysAdminOnly")]
    public class RolePermissionsController : Controller
    {
        private readonly GovBudgetContext _db;
        private readonly IPermissionService _permissions;

        public RolePermissionsController(GovBudgetContext db, IPermissionService permissions)
        {
            _db = db;
            _permissions = permissions;
        }

        [HttpGet]
        public async Task<IActionResult> Index(int? roleId = null)
        {
            var roles = await _db.AppRoles
                .AsNoTracking()
                .OrderByDescending(r => r.IsSystem)
                .ThenBy(r => r.RoleCode)
                .ToListAsync();

            if (roles.Count == 0)
            {
                TempData["Error"] = "No roles found. Restart the application to seed the default roles.";
                return View(new RoleRightsVm());
            }

            var selected = roleId.HasValue
                ? roles.FirstOrDefault(r => r.RoleId == roleId.Value) ?? roles[0]
                : roles[0];

            var saved = await _db.RolePermissions
                .AsNoTracking()
                .Where(p => p.RoleId == selected.RoleId)
                .ToListAsync();

            var byForm = saved.ToDictionary(p => p.FormKey, StringComparer.OrdinalIgnoreCase);

            var vm = new RoleRightsVm
            {
                Roles = roles,
                SelectedRoleId = selected.RoleId,
                SelectedRoleCode = selected.RoleCode,
                SelectedRoleName = selected.RoleName,
                SelectedRoleDescription = selected.Description,
                IsSuperRole = string.Equals(selected.RoleCode, PermissionService.SuperRole, StringComparison.OrdinalIgnoreCase),
                Rows = AppForms.All.Select(f =>
                {
                    byForm.TryGetValue(f.Key, out var p);
                    return new FormRightRow
                    {
                        FormKey = f.Key,
                        Display = f.Display,
                        Group = f.Group,
                        Description = f.Description,
                        ViewOnlyByNature = f.ViewOnlyByNature,
                        CanView = p?.CanView ?? false,
                        CanAdd = p?.CanAdd ?? false,
                        CanEdit = p?.CanEdit ?? false,
                        CanDelete = p?.CanDelete ?? false
                    };
                }).ToList()
            };

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(int roleId, List<FormRightRow> rows)
        {
            var role = await _db.AppRoles.FirstOrDefaultAsync(r => r.RoleId == roleId);
            if (role == null) return NotFound();

            if (string.Equals(role.RoleCode, PermissionService.SuperRole, StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "SYSADMIN always has full access and cannot be restricted.";
                return RedirectToAction(nameof(Index), new { roleId });
            }

            rows ??= new List<FormRightRow>();

            var existing = await _db.RolePermissions
                .Where(p => p.RoleId == roleId)
                .ToListAsync();

            var byForm = existing.ToDictionary(p => p.FormKey, StringComparer.OrdinalIgnoreCase);
            var now = DateTime.UtcNow;
            var who = User.Identity?.Name ?? "Unknown";
            var changes = 0;

            foreach (var row in rows)
            {
                // Ignore anything that is not a known form so a tampered post cannot
                // create rights for arbitrary keys.
                var form = AppForms.Find(row.FormKey);
                if (form == null) continue;

                // View is the master switch: no view means no rights at all.
                var canView = row.CanView;
                var canAdd = canView && !form.ViewOnlyByNature && row.CanAdd;
                var canEdit = canView && !form.ViewOnlyByNature && row.CanEdit;
                var canDelete = canView && !form.ViewOnlyByNature && row.CanDelete;

                if (byForm.TryGetValue(form.Key, out var p))
                {
                    if (p.CanView == canView && p.CanAdd == canAdd && p.CanEdit == canEdit && p.CanDelete == canDelete)
                    {
                        continue;
                    }

                    p.CanView = canView;
                    p.CanAdd = canAdd;
                    p.CanEdit = canEdit;
                    p.CanDelete = canDelete;
                    p.UpdatedAt = now;
                    p.UpdatedBy = who;
                    changes++;
                }
                else if (canView || canAdd || canEdit || canDelete)
                {
                    _db.RolePermissions.Add(new RolePermissions
                    {
                        RoleId = roleId,
                        FormKey = form.Key,
                        CanView = canView,
                        CanAdd = canAdd,
                        CanEdit = canEdit,
                        CanDelete = canDelete,
                        UpdatedAt = now,
                        UpdatedBy = who
                    });
                    changes++;
                }
            }

            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = who,
                Action = "UPDATE",
                EntityName = "RolePermissions",
                RecordId = roleId.ToString(),
                Timestamp = now,
                Details = $"Updated form rights for role '{role.RoleCode}' ({changes} change(s))."
            });

            await _db.SaveChangesAsync();
            _permissions.InvalidateCache();

            TempData["Success"] = changes == 0
                ? "No changes to save."
                : $"Saved {changes} permission change(s) for '{role.RoleName}'. Affected users see this on their next page load.";

            return RedirectToAction(nameof(Index), new { roleId });
        }

        [HttpGet]
        public IActionResult CreateRole() => View(new AppRoles { IsActive = true, IsEntityScoped = true });

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateRole(AppRoles vm)
        {
            var code = (vm.RoleCode ?? "").Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(code))
            {
                ModelState.AddModelError(nameof(vm.RoleCode), "Role code is required.");
            }
            else if (code.Length > 20)
            {
                ModelState.AddModelError(nameof(vm.RoleCode), "Role code must be 20 characters or fewer.");
            }
            else if (await _db.AppRoles.AnyAsync(r => r.RoleCode == code))
            {
                ModelState.AddModelError(nameof(vm.RoleCode), "That role code already exists.");
            }

            if (string.IsNullOrWhiteSpace(vm.RoleName))
            {
                ModelState.AddModelError(nameof(vm.RoleName), "Role name is required.");
            }

            if (!ModelState.IsValid) return View(vm);

            var role = new AppRoles
            {
                RoleCode = code,
                RoleName = vm.RoleName.Trim(),
                Description = string.IsNullOrWhiteSpace(vm.Description) ? null : vm.Description.Trim(),
                IsSystem = false,
                IsEntityScoped = vm.IsEntityScoped,
                IsActive = true
            };

            _db.AppRoles.Add(role);

            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = User.Identity?.Name ?? "Unknown",
                Action = "INSERT",
                EntityName = "AppRoles",
                RecordId = code,
                Timestamp = DateTime.UtcNow,
                Details = $"Created role '{code}'. It starts with no rights."
            });

            await _db.SaveChangesAsync();
            _permissions.InvalidateCache();

            TempData["Success"] = $"Role '{code}' created. It has no rights yet — tick the forms it may use below.";
            return RedirectToAction(nameof(Index), new { roleId = role.RoleId });
        }

        public class RoleRightsVm
        {
            public List<AppRoles> Roles { get; set; } = new();
            public int SelectedRoleId { get; set; }
            public string SelectedRoleCode { get; set; } = "";
            public string SelectedRoleName { get; set; } = "";
            public string? SelectedRoleDescription { get; set; }
            public bool IsSuperRole { get; set; }
            public List<FormRightRow> Rows { get; set; } = new();
        }

        public class FormRightRow
        {
            public string FormKey { get; set; } = "";
            public string Display { get; set; } = "";
            public string Group { get; set; } = "";
            public string Description { get; set; } = "";
            public bool ViewOnlyByNature { get; set; }
            public bool CanView { get; set; }
            public bool CanAdd { get; set; }
            public bool CanEdit { get; set; }
            public bool CanDelete { get; set; }
        }
    }
}
