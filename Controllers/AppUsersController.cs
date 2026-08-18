using System;
using System.Linq;
using System.Threading.Tasks;
using GovBudget.Models;
using GovBudget.Services;
using GovBudget.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Controllers
{
    [Authorize(Policy = "AdminOnly")]
    public class AppUsersController : Controller
    {
        private readonly GovBudgetContext _context;
        private readonly IPasswordResetNotifier _resetNotifier;

        public AppUsersController(GovBudgetContext context, IPasswordResetNotifier resetNotifier)
        {
            _context = context;
            _resetNotifier = resetNotifier;
        }

        private int? GetAdminScopedEntityId()
        {
            var entityClaim = User.Claims.FirstOrDefault(c => c.Type == "EntityId")?.Value;
            if (!int.TryParse(entityClaim, out var entityId) || entityId <= 0)
            {
                return null;
            }

            return entityId;
        }

        private IQueryable<AppUsers> ScopedUsersQuery()
        {
            var adminEntityId = GetAdminScopedEntityId();

            var query = _context.AppUsers
                .Include(u => u.Entity)
                .Include(u => u.Department).ThenInclude(d => d!.Entity)
                .AsQueryable();

            if (adminEntityId.HasValue)
            {
                query = query.Where(u =>
                    (u.EntityId.HasValue && u.EntityId.Value == adminEntityId.Value)
                    || (u.DepartmentId.HasValue && u.Department!.EntityId == adminEntityId.Value));
            }

            return query;
        }

        public async Task<IActionResult> Index()
        {
            var users = await ScopedUsersQuery()
                .OrderBy(u => u.UserName)
                .ToListAsync();

            return View(users);
        }

        public async Task<IActionResult> Details(int? id)
        {
            if (id == null) return NotFound();

            var user = await ScopedUsersQuery().FirstOrDefaultAsync(u => u.UserId == id);

            if (user == null) return NotFound();

            return View(user);
        }

        public async Task<IActionResult> Create()
        {
            var adminEntityId = GetAdminScopedEntityId();
            PopulateEntityDropDown(selectedId: adminEntityId, allowedEntityId: adminEntityId);
            PopulateDepartmentDropDown(selectedId: null, entityId: adminEntityId);
            await PopulateRoleDropDown("USER");
            return View(new UserFormVm { IsActive = true, Role = "USER", EntityId = adminEntityId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(UserFormVm vm)
        {
            var adminEntityId = GetAdminScopedEntityId();

            await ValidateAndNormalize(vm, userIdBeingEdited: null);

            if (!ModelState.IsValid)
            {
                var effectiveEntityId = adminEntityId ?? vm.EntityId;
                PopulateEntityDropDown(selectedId: effectiveEntityId, allowedEntityId: adminEntityId);
                PopulateDepartmentDropDown(selectedId: vm.DepartmentId, entityId: effectiveEntityId);
                await PopulateRoleDropDown(vm.Role);
                return View(vm);
            }

            var user = new AppUsers
            {
                UserName = vm.UserName,
                Password = vm.Password,
                Role = vm.Role,
                IsActive = vm.IsActive,
                EntityId = vm.EntityId,
                DepartmentId = vm.DepartmentId
            };

            _context.Add(user);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();
            var adminEntityId = GetAdminScopedEntityId();

            var user = await ScopedUsersQuery().FirstOrDefaultAsync(u => u.UserId == id);
            if (user == null) return NotFound();

            var effectiveEntityId = user.EntityId ?? user.Department?.EntityId;
            PopulateEntityDropDown(selectedId: effectiveEntityId, allowedEntityId: adminEntityId);
            PopulateDepartmentDropDown(selectedId: user.DepartmentId, entityId: effectiveEntityId);
            await PopulateRoleDropDown(user.Role);

            return View(new UserFormVm
            {
                UserId = user.UserId,
                UserName = user.UserName,
                Password = "",
                Role = user.Role,
                EntityId = effectiveEntityId,
                DepartmentId = user.DepartmentId,
                IsActive = user.IsActive
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, UserFormVm vm)
        {
            if (id != vm.UserId) return NotFound();

            var adminEntityId = GetAdminScopedEntityId();

            var user = await ScopedUsersQuery().FirstOrDefaultAsync(u => u.UserId == id);
            if (user == null) return NotFound();

            await ValidateAndNormalize(vm, userIdBeingEdited: id);

            if (!ModelState.IsValid)
            {
                var effectiveEntityId = adminEntityId ?? vm.EntityId;
                PopulateEntityDropDown(selectedId: effectiveEntityId, allowedEntityId: adminEntityId);
                PopulateDepartmentDropDown(selectedId: vm.DepartmentId, entityId: effectiveEntityId);
                await PopulateRoleDropDown(vm.Role);
                return View(vm);
            }

            user.UserName = vm.UserName;
            user.Role = vm.Role;
            user.IsActive = vm.IsActive;
            user.EntityId = vm.EntityId;
            user.DepartmentId = vm.DepartmentId;

            if (!string.IsNullOrWhiteSpace(vm.Password))
            {
                user.Password = vm.Password;
            }

            try
            {
                _context.Update(user);
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!AppUserExists(id)) return NotFound();
                throw;
            }

            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null) return NotFound();

            var user = await ScopedUsersQuery().FirstOrDefaultAsync(u => u.UserId == id);

            if (user == null) return NotFound();
            return View(user);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var user = await ScopedUsersQuery().FirstOrDefaultAsync(u => u.UserId == id);
            if (user != null)
            {
                user.IsActive = false;
                _context.Update(user);
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        // POST: AppUsers/IssueResetLink/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> IssueResetLink(int id)
        {
            var user = await ScopedUsersQuery().FirstOrDefaultAsync(u => u.UserId == id);
            if (user == null) return NotFound();

            var entityId = user.EntityId ?? user.Department?.EntityId;

            var token = ResetTokens.Generate();
            var expires = DateTime.UtcNow.AddDays(7);

            _context.PasswordResetRequests.Add(new PasswordResetRequests
            {
                UserName = user.UserName,
                UserId = user.UserId,
                EntityId = entityId,
                Status = "LinkIssued",
                RequestSource = "Admin",
                RequestedAt = DateTime.UtcNow,
                Token = token,
                TokenExpiresAt = expires,
                IssuedAt = DateTime.UtcNow,
                IssuedBy = User.Identity?.Name ?? "Unknown"
            });

            _context.AuditLogs.Add(new AuditLogs
            {
                UserName = User.Identity?.Name ?? "Unknown",
                Action = "UPDATE",
                EntityName = "PasswordResetRequests",
                RecordId = user.UserId.ToString(),
                Timestamp = DateTime.UtcNow,
                Details = $"Admin generated a password reset link for user '{user.UserName}'."
            });

            await _context.SaveChangesAsync();

            var resetUrl = Url.Action("ResetPassword", "Account", new { token }, Request.Scheme) ?? "";

            await _resetNotifier.NotifyLinkIssuedAsync(new PasswordResetNotification
            {
                UserName = user.UserName,
                ResetUrl = resetUrl,
                ExpiresAt = expires
            });

            TempData["ResetLink"] = resetUrl;
            TempData["ResetLinkUser"] = user.UserName;
            TempData["Success"] = $"Reset link generated for '{user.UserName}'. Copy it below and send it to the user.";
            return RedirectToAction(nameof(Index));
        }

        private bool AppUserExists(int id) => _context.AppUsers.Any(e => e.UserId == id);

        // Roles come from core.AppRoles so any role added on the Roles & Rights screen is
        // immediately assignable. Only a SYSADMIN may hand out the SYSADMIN role.
        private async Task PopulateRoleDropDown(string? selected)
        {
            var isSysAdmin = User.IsInRole("SYSADMIN");

            var roles = await _context.AppRoles
                .AsNoTracking()
                .Where(r => r.IsActive)
                .Where(r => isSysAdmin || r.RoleCode != "SYSADMIN")
                .OrderByDescending(r => r.IsSystem)
                .ThenBy(r => r.RoleCode)
                .Select(r => new
                {
                    r.RoleCode,
                    Display = r.RoleCode + " — " + r.RoleName
                })
                .ToListAsync();

            ViewData["RoleList"] = new SelectList(roles, "RoleCode", "Display", selected);
        }

        private void PopulateEntityDropDown(int? selectedId = null, int? allowedEntityId = null)
        {
            var entsQuery = _context.Entities
                .Where(e => e.IsActive)
                .AsQueryable();

            if (allowedEntityId.HasValue)
            {
                entsQuery = entsQuery.Where(e => e.EntityId == allowedEntityId.Value);
                selectedId = allowedEntityId.Value;
            }

            var ents = entsQuery
                .OrderBy(e => e.EntityCode)
                .Select(e => new
                {
                    e.EntityId,
                    Display = e.EntityCode + " — " + e.EntityName
                })
                .ToList();

            ViewData["EntityId"] = new SelectList(ents, "EntityId", "Display", selectedId);
        }

        private void PopulateDepartmentDropDown(int? selectedId = null, int? entityId = null)
        {
            if (!entityId.HasValue || entityId.Value <= 0)
            {
                ViewData["DepartmentId"] = new SelectList(Enumerable.Empty<SelectListItem>(), "Value", "Text", selectedId);
                return;
            }

            var deps = _context.Departments
                .Where(d => d.IsActive)
                .Where(d => d.EntityId == entityId.Value)
                .OrderBy(d => d.DeptCode)
                .Select(d => new
                {
                    d.DepartmentId,
                    Display = d.DeptCode + " — " + d.DeptName
                })
                .ToList();

            ViewData["DepartmentId"] = new SelectList(deps, "DepartmentId", "Display", selectedId);
        }

        [HttpGet]
        public async Task<IActionResult> DepartmentsForEntity(int entityId)
        {
            var adminEntityId = GetAdminScopedEntityId();
            if (adminEntityId.HasValue && entityId != adminEntityId.Value)
            {
                return Forbid();
            }

            if (entityId <= 0)
            {
                return Ok(Array.Empty<object>());
            }

            var deps = await _context.Departments
                .AsNoTracking()
                .Where(d => d.IsActive && d.EntityId == entityId)
                .OrderBy(d => d.DeptCode)
                .Select(d => new
                {
                    id = d.DepartmentId,
                    text = d.DeptCode + " — " + d.DeptName
                })
                .ToListAsync();

            return Ok(deps);
        }

        private async Task ValidateAndNormalize(UserFormVm vm, int? userIdBeingEdited)
        {
            vm.UserName = (vm.UserName ?? "").Trim();
            vm.Role = (vm.Role ?? "").Trim().ToUpperInvariant();
            var adminEntityId = GetAdminScopedEntityId();
            var isSysAdmin = User.IsInRole("SYSADMIN");

            if (string.IsNullOrWhiteSpace(vm.UserName))
            {
                ModelState.AddModelError(nameof(vm.UserName), "Username is required.");
            }

            var roleDef = await _context.AppRoles
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.RoleCode == vm.Role && r.IsActive);

            if (roleDef == null)
            {
                ModelState.AddModelError(nameof(vm.Role), "Unknown or inactive role. Pick a role from the list.");
            }

            if (!userIdBeingEdited.HasValue && string.IsNullOrWhiteSpace(vm.Password))
            {
                ModelState.AddModelError(nameof(vm.Password), "Password is required.");
            }

            var existingUser = await _context.AppUsers
                .AsNoTracking()
                .Where(u => u.UserName == vm.UserName)
                .Select(u => new { u.UserId })
                .FirstOrDefaultAsync();

            if (existingUser != null && (!userIdBeingEdited.HasValue || existingUser.UserId != userIdBeingEdited.Value))
            {
                ModelState.AddModelError(nameof(vm.UserName), "Username already exists.");
            }

            if (vm.DepartmentId.HasValue)
            {
                var deptEntityId = await _context.Departments
                    .AsNoTracking()
                    .Where(d => d.DepartmentId == vm.DepartmentId.Value)
                    .Select(d => (int?)d.EntityId)
                    .FirstOrDefaultAsync();

                if (!deptEntityId.HasValue)
                {
                    ModelState.AddModelError(nameof(vm.DepartmentId), "Invalid department.");
                }
                else if (vm.EntityId.HasValue && vm.EntityId.Value != deptEntityId.Value)
                {
                    ModelState.AddModelError(nameof(vm.DepartmentId), "Department does not belong to the selected entity.");
                }
                else
                {
                    vm.EntityId = deptEntityId;
                }
            }

            if (adminEntityId.HasValue)
            {
                if (vm.Role == "SYSADMIN")
                {
                    ModelState.AddModelError(nameof(vm.Role), "Only SYSADMIN can create or edit SYSADMIN users.");
                }

                if (!vm.EntityId.HasValue || vm.EntityId.Value != adminEntityId.Value)
                {
                    ModelState.AddModelError(nameof(vm.EntityId), "You can only create or edit users within your entity.");
                }
            }

            if (vm.Role == "SYSADMIN")
            {
                if (!isSysAdmin)
                {
                    ModelState.AddModelError(nameof(vm.Role), "Only SYSADMIN can create or edit SYSADMIN users.");
                }
                vm.EntityId = null;
                vm.DepartmentId = null;
            }

            if (vm.Role == "ADMIN")
            {
                vm.DepartmentId = null;
                if (!vm.EntityId.HasValue)
                {
                    ModelState.AddModelError(nameof(vm.EntityId), "An ADMIN must be linked to an entity.");
                }
            }

            if (vm.Role == "USER" && !vm.EntityId.HasValue && !vm.DepartmentId.HasValue)
            {
                ModelState.AddModelError(nameof(vm.EntityId), "A USER must be linked to an entity or department.");
            }

            // Custom (non built-in) roles: an entity-scoped role has to be tied to a scope,
            // otherwise the user would have nothing to see.
            if (roleDef != null && !roleDef.IsSystem && roleDef.IsEntityScoped
                && !vm.EntityId.HasValue && !vm.DepartmentId.HasValue)
            {
                ModelState.AddModelError(nameof(vm.EntityId),
                    $"The '{roleDef.RoleCode}' role is entity-scoped, so the user must be linked to an entity or department.");
            }
        }

        public class UserFormVm
        {
            public int UserId { get; set; }
            public string UserName { get; set; } = "";
            public string Password { get; set; } = "";
            public string Role { get; set; } = "";
            public int? EntityId { get; set; }
            public int? DepartmentId { get; set; }
            public bool IsActive { get; set; }
        }
    }
}
