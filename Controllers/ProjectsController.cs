using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using GovBudget.Models;

namespace GovBudget.Controllers
{
    [Authorize(Policy = "AdminOnly")]
    public class ProjectsController : Controller
    {
        private readonly GovBudgetContext _context;

        public ProjectsController(GovBudgetContext context)
        {
            _context = context;
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

        private bool IsGlobalAdmin()
        {
            var adminEntityId = GetAdminScopedEntityId();
            return User.IsInRole("SYSADMIN") || (User.IsInRole("ADMIN") && !adminEntityId.HasValue);
        }

        // POST: Projects/QuickCreate  (AJAX, admin only) — inline add from the Budget Entry screen.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> QuickCreate(string projectCode, string projectName, int departmentId)
        {
            projectCode = (projectCode ?? "").Trim();
            projectName = (projectName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(projectCode) || string.IsNullOrWhiteSpace(projectName))
                return Json(new { ok = false, error = "Project code and name are required." });

            var dept = await _context.Departments.Include(d => d.Entity)
                .FirstOrDefaultAsync(d => d.DepartmentId == departmentId);
            if (dept == null)
                return Json(new { ok = false, error = "Cost center not found." });

            var adminEntityId = GetAdminScopedEntityId();
            if (adminEntityId.HasValue && adminEntityId.Value != dept.EntityId)
                return Json(new { ok = false, error = "You are not permitted to add projects for this entity." });

            if (await _context.Projects.AnyAsync(p => p.ProjectCode == projectCode))
                return Json(new { ok = false, error = $"A project with code '{projectCode}' already exists." });

            var project = new Projects
            {
                ProjectCode = projectCode,
                ProjectName = projectName,
                OwningDepartmentId = departmentId,
                IsActive = true
            };

            try
            {
                _context.Projects.Add(project);
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Internal database messages are not echoed back to the browser.
                return Json(new { ok = false, error = "Could not save the project. Check the code is unique and try again." });
            }

            return Json(new
            {
                ok = true,
                projectId = project.ProjectId,
                display = $"{project.ProjectCode} - {project.ProjectName}"
            });
        }

        // GET: Projects
        public async Task<IActionResult> Index()
        {
            var adminEntityId = GetAdminScopedEntityId();
            var projects = _context.Projects
                .Include(p => p.OwningDepartment).ThenInclude(d => d!.Entity)
                .AsQueryable();

            if (adminEntityId.HasValue)
            {
                projects = projects.Where(p => p.OwningDepartmentId == null || p.OwningDepartment!.EntityId == adminEntityId.Value);
            }

            projects = projects.OrderBy(p => p.ProjectCode);
            return View(await projects.ToListAsync());
        }

        // GET: Projects/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null) return NotFound();

            var adminEntityId = GetAdminScopedEntityId();
            var project = await _context.Projects
                .Include(p => p.OwningDepartment).ThenInclude(d => d!.Entity)
                .FirstOrDefaultAsync(m => m.ProjectId == id);
            if (project == null) return NotFound();

            if (adminEntityId.HasValue && project.OwningDepartmentId.HasValue && project.OwningDepartment!.EntityId != adminEntityId.Value)
            {
                return Forbid();
            }

            return View(project);
        }

        // GET: Projects/Create
        public IActionResult Create()
        {
            var adminEntityId = GetAdminScopedEntityId();
            PopulateDepartmentDropDown(allowedEntityId: adminEntityId);
            return View();
        }

        // POST: Projects/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("ProjectId,ProjectCode,ProjectName,OwningDepartmentId,IsActive")] Projects project)
        {
            // Remove validation for OwningDepartment since it can be null (General)
            ModelState.Remove(nameof(project.OwningDepartment));

            var adminEntityId = GetAdminScopedEntityId();
            if (adminEntityId.HasValue && !IsGlobalAdmin())
            {
                if (!project.OwningDepartmentId.HasValue)
                {
                    ModelState.AddModelError(nameof(project.OwningDepartmentId), "Entity admins must select an owning department.");
                }
                else
                {
                    var deptEntityId = await _context.Departments
                        .AsNoTracking()
                        .Where(d => d.DepartmentId == project.OwningDepartmentId.Value)
                        .Select(d => (int?)d.EntityId)
                        .FirstOrDefaultAsync();

                    if (!deptEntityId.HasValue || deptEntityId.Value != adminEntityId.Value)
                    {
                        return Forbid();
                    }
                }
            }

            if (ModelState.IsValid)
            {
                _context.Add(project);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            PopulateDepartmentDropDown(selectedId: project.OwningDepartmentId, allowedEntityId: adminEntityId);
            return View(project);
        }

        // GET: Projects/Template
        [HttpGet]
        public IActionResult Template()
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Projects");

            ws.Cell(1, 1).Value = "ProjectCode";
            ws.Cell(1, 2).Value = "ProjectName";
            ws.Cell(1, 3).Value = "OwningEntityCode";
            ws.Cell(1, 4).Value = "OwningDeptCode";
            ws.Cell(1, 5).Value = "IsActive";

            ws.Cell(2, 1).Value = "PRJ001";
            ws.Cell(2, 2).Value = "Sample Project";
            ws.Cell(2, 3).Value = "ENT001";
            ws.Cell(2, 4).Value = "CC001";
            ws.Cell(2, 5).Value = "TRUE";

            ws.Range(1, 1, 1, 5).Style.Font.Bold = true;
            ws.Columns(1, 5).AdjustToContents();

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Projects_Template.xlsx");
        }

        // POST: Projects/Upload
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Upload(IFormFile? file)
        {
            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Please choose an Excel file to upload.";
                return RedirectToAction(nameof(Index));
            }
            if (!Path.GetExtension(file.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Only .xlsx files are supported.";
                return RedirectToAction(nameof(Index));
            }

            var adminEntityId = GetAdminScopedEntityId();
            var isGlobalAdmin = IsGlobalAdmin();

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            ms.Position = 0;

            using var wb = new XLWorkbook(ms);
            var ws = wb.Worksheets.FirstOrDefault();
            if (ws == null)
            {
                TempData["Error"] = "No worksheet found in the uploaded file.";
                return RedirectToAction(nameof(Index));
            }

            var headerRow = ws.FirstRowUsed();
            if (headerRow == null)
            {
                TempData["Error"] = "The uploaded file is empty.";
                return RedirectToAction(nameof(Index));
            }

            var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in headerRow.CellsUsed())
            {
                var name = (cell.GetString() ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(name)) headerMap[name] = cell.Address.ColumnNumber;
            }

            if (!headerMap.TryGetValue("ProjectCode", out var codeCol) ||
                !headerMap.TryGetValue("ProjectName", out var nameCol))
            {
                TempData["Error"] = "Template columns must include: ProjectCode, ProjectName. Optional: OwningEntityCode, OwningDeptCode, IsActive.";
                return RedirectToAction(nameof(Index));
            }
            headerMap.TryGetValue("OwningEntityCode", out var entityCol);
            headerMap.TryGetValue("OwningDeptCode", out var deptCol);
            headerMap.TryGetValue("IsActive", out var activeCol);

            var entityByCode = await _context.Entities.AsNoTracking()
                .Where(e => !string.IsNullOrWhiteSpace(e.EntityCode))
                .ToDictionaryAsync(e => e.EntityCode.Trim(), e => e, StringComparer.OrdinalIgnoreCase);

            var depts = await _context.Departments.AsNoTracking().ToListAsync();
            var deptByEntityCode = depts
                .Where(d => !string.IsNullOrWhiteSpace(d.DeptCode))
                .ToDictionary(d => (d.EntityId, d.DeptCode.Trim().ToUpperInvariant()), d => d);

            var existing = await _context.Projects.ToListAsync();
            var byCode = existing
                .Where(p => !string.IsNullOrWhiteSpace(p.ProjectCode))
                .ToDictionary(p => p.ProjectCode.Trim().ToUpperInvariant(), p => p);

            var created = 0;
            var updated = 0;
            var errors = new List<string>();

            var firstDataRow = headerRow.RowNumber() + 1;
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? firstDataRow - 1;

            for (var r = firstDataRow; r <= lastRow; r++)
            {
                var row = ws.Row(r);
                var code = row.Cell(codeCol).GetString().Trim();
                var nm = row.Cell(nameCol).GetString().Trim();
                var entityCode = entityCol > 0 ? row.Cell(entityCol).GetString().Trim() : "";
                var deptCode = deptCol > 0 ? row.Cell(deptCol).GetString().Trim() : "";
                var activeRaw = activeCol > 0 ? row.Cell(activeCol).GetString().Trim() : "";
                var isActive = ParseBoolOrDefault(activeRaw, true);

                if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(nm) &&
                    string.IsNullOrWhiteSpace(entityCode) && string.IsNullOrWhiteSpace(deptCode)) continue;

                if (string.IsNullOrWhiteSpace(code)) { errors.Add($"Row {r}: ProjectCode is required."); if (errors.Count >= 20) break; continue; }
                if (string.IsNullOrWhiteSpace(nm)) { errors.Add($"Row {r}: ProjectName is required."); if (errors.Count >= 20) break; continue; }

                int? owningDepartmentId = null;
                if (!string.IsNullOrWhiteSpace(deptCode))
                {
                    int resolveEntityId;
                    if (adminEntityId.HasValue)
                    {
                        resolveEntityId = adminEntityId.Value;
                    }
                    else if (!string.IsNullOrWhiteSpace(entityCode))
                    {
                        if (!entityByCode.TryGetValue(entityCode, out var ent))
                        {
                            errors.Add($"Row {r}: OwningEntityCode '{entityCode}' was not found.");
                            if (errors.Count >= 20) break;
                            continue;
                        }
                        resolveEntityId = ent.EntityId;
                    }
                    else
                    {
                        var matches = depts.Where(d => string.Equals(d.DeptCode?.Trim(), deptCode, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (matches.Count == 0)
                        {
                            errors.Add($"Row {r}: OwningDeptCode '{deptCode}' was not found.");
                            if (errors.Count >= 20) break;
                            continue;
                        }
                        if (matches.Count > 1)
                        {
                            errors.Add($"Row {r}: OwningDeptCode '{deptCode}' is ambiguous; provide OwningEntityCode.");
                            if (errors.Count >= 20) break;
                            continue;
                        }
                        owningDepartmentId = matches[0].DepartmentId;
                        resolveEntityId = matches[0].EntityId;
                    }

                    if (!owningDepartmentId.HasValue)
                    {
                        if (!deptByEntityCode.TryGetValue((resolveEntityId, deptCode.ToUpperInvariant()), out var dept))
                        {
                            errors.Add($"Row {r}: OwningDeptCode '{deptCode}' was not found for the entity.");
                            if (errors.Count >= 20) break;
                            continue;
                        }
                        owningDepartmentId = dept.DepartmentId;
                    }
                }

                if (!isGlobalAdmin && !owningDepartmentId.HasValue)
                {
                    errors.Add($"Row {r}: Entity admins must provide an OwningDeptCode.");
                    if (errors.Count >= 20) break;
                    continue;
                }

                if (byCode.TryGetValue(code.ToUpperInvariant(), out var existingProject))
                {
                    if (adminEntityId.HasValue && existingProject.OwningDepartmentId.HasValue)
                    {
                        var ownerEntityId = depts.FirstOrDefault(d => d.DepartmentId == existingProject.OwningDepartmentId.Value)?.EntityId;
                        if (ownerEntityId.HasValue && ownerEntityId.Value != adminEntityId.Value)
                        {
                            errors.Add($"Row {r}: ProjectCode '{code}' belongs to another entity.");
                            if (errors.Count >= 20) break;
                            continue;
                        }
                    }
                    existingProject.ProjectName = nm;
                    existingProject.OwningDepartmentId = owningDepartmentId;
                    existingProject.IsActive = isActive;
                    updated++;
                }
                else
                {
                    var project = new Projects
                    {
                        ProjectCode = code,
                        ProjectName = nm,
                        OwningDepartmentId = owningDepartmentId,
                        IsActive = isActive
                    };
                    _context.Projects.Add(project);
                    byCode[code.ToUpperInvariant()] = project;
                    created++;
                }
            }

            if (errors.Count > 0)
            {
                TempData["Error"] = "Upload failed:\n" + string.Join("\n", errors);
                return RedirectToAction(nameof(Index));
            }

            await _context.SaveChangesAsync();
            TempData["Success"] = $"Upload complete. Created: {created}. Updated: {updated}.";
            return RedirectToAction(nameof(Index));
        }

        private static bool ParseBoolOrDefault(string raw, bool def)
        {
            if (string.IsNullOrWhiteSpace(raw)) return def;
            raw = raw.Trim();
            if (bool.TryParse(raw, out var b)) return b;
            if (raw == "1" || raw.Equals("yes", StringComparison.OrdinalIgnoreCase) || raw.Equals("y", StringComparison.OrdinalIgnoreCase)) return true;
            if (raw == "0" || raw.Equals("no", StringComparison.OrdinalIgnoreCase) || raw.Equals("n", StringComparison.OrdinalIgnoreCase)) return false;
            return def;
        }

        // GET: Projects/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();

            var adminEntityId = GetAdminScopedEntityId();
            var project = await _context.Projects
                .Include(p => p.OwningDepartment).ThenInclude(d => d!.Entity)
                .FirstOrDefaultAsync(p => p.ProjectId == id);
            if (project == null) return NotFound();

            if (!IsGlobalAdmin() && !project.OwningDepartmentId.HasValue)
            {
                return Forbid();
            }

            if (adminEntityId.HasValue && project.OwningDepartmentId.HasValue && project.OwningDepartment!.EntityId != adminEntityId.Value)
            {
                return Forbid();
            }

            PopulateDepartmentDropDown(selectedId: project.OwningDepartmentId, allowedEntityId: adminEntityId);
            return View(project);
        }

        // POST: Projects/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("ProjectId,ProjectCode,ProjectName,OwningDepartmentId,IsActive")] Projects project)
        {
            if (id != project.ProjectId) return NotFound();

            // Remove validation for OwningDepartment since it can be null (General)
            ModelState.Remove(nameof(project.OwningDepartment));

            var adminEntityId = GetAdminScopedEntityId();
            if (adminEntityId.HasValue && !IsGlobalAdmin())
            {
                var existing = await _context.Projects
                    .AsNoTracking()
                    .Include(p => p.OwningDepartment)
                    .FirstOrDefaultAsync(p => p.ProjectId == id);
                if (existing == null) return NotFound();
                if (!existing.OwningDepartmentId.HasValue) return Forbid();

                if (!project.OwningDepartmentId.HasValue)
                {
                    ModelState.AddModelError(nameof(project.OwningDepartmentId), "Entity admins must select an owning department.");
                }
                else
                {
                    var deptEntityId = await _context.Departments
                        .AsNoTracking()
                        .Where(d => d.DepartmentId == project.OwningDepartmentId.Value)
                        .Select(d => (int?)d.EntityId)
                        .FirstOrDefaultAsync();

                    if (!deptEntityId.HasValue || deptEntityId.Value != adminEntityId.Value)
                    {
                        return Forbid();
                    }
                }
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(project);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!ProjectExists(project.ProjectId)) return NotFound();
                    else throw;
                }
                return RedirectToAction(nameof(Index));
            }
            PopulateDepartmentDropDown(selectedId: project.OwningDepartmentId, allowedEntityId: adminEntityId);
            return View(project);
        }

        // GET: Projects/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null) return NotFound();

            var adminEntityId = GetAdminScopedEntityId();
            var project = await _context.Projects
                .Include(p => p.OwningDepartment).ThenInclude(d => d!.Entity)
                .FirstOrDefaultAsync(m => m.ProjectId == id);
            if (project == null) return NotFound();

            if (!IsGlobalAdmin() && !project.OwningDepartmentId.HasValue)
            {
                return Forbid();
            }

            if (adminEntityId.HasValue && project.OwningDepartmentId.HasValue && project.OwningDepartment!.EntityId != adminEntityId.Value)
            {
                return Forbid();
            }

            return View(project);
        }

        // POST: Projects/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var adminEntityId = GetAdminScopedEntityId();
            var project = await _context.Projects
                .Include(p => p.OwningDepartment)
                .FirstOrDefaultAsync(p => p.ProjectId == id);
            if (project != null)
            {
                if (!IsGlobalAdmin() && !project.OwningDepartmentId.HasValue)
                {
                    return Forbid();
                }

                if (adminEntityId.HasValue && project.OwningDepartmentId.HasValue && project.OwningDepartment!.EntityId != adminEntityId.Value)
                {
                    return Forbid();
                }

                _context.Projects.Remove(project);
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        private bool ProjectExists(int id)
        {
            return _context.Projects.Any(e => e.ProjectId == id);
        }

        private void PopulateDepartmentDropDown(int? selectedId = null, int? allowedEntityId = null)
        {
            var depsQuery = _context.Departments
                .Include(d => d.Entity)
                .Where(d => d.IsActive)
                .AsQueryable();

            if (allowedEntityId.HasValue)
            {
                depsQuery = depsQuery.Where(d => d.EntityId == allowedEntityId.Value);
            }

            var deps = depsQuery
                .OrderBy(d => d.DeptCode)
                .Select(d => new
                {
                    d.DepartmentId,
                    Display = d.DeptCode + " — " + d.DeptName + " (" + d.Entity.EntityCode + ")"
                })
                .ToList();

            ViewData["OwningDepartmentId"] = new SelectList(deps, "DepartmentId", "Display", selectedId);
        }
    }
}
