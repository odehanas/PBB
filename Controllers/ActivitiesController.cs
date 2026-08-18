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
    public class ActivitiesController : Controller
    {
        private readonly GovBudgetContext _context;

        public ActivitiesController(GovBudgetContext context)
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

        // POST: Activities/QuickCreate  (AJAX, admin only) — inline add from the Budget Entry screen.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> QuickCreate(string activityCode, string activityName, int programId, int departmentId)
        {
            activityCode = (activityCode ?? "").Trim();
            activityName = (activityName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(activityCode) || string.IsNullOrWhiteSpace(activityName))
                return Json(new { ok = false, error = "Activity code and name are required." });

            var dept = await _context.Departments.Include(d => d.Entity)
                .FirstOrDefaultAsync(d => d.DepartmentId == departmentId);
            if (dept == null)
                return Json(new { ok = false, error = "Cost center not found." });

            var program = await _context.Programs.FirstOrDefaultAsync(p => p.ProgramId == programId);
            if (program == null)
                return Json(new { ok = false, error = "Please choose a valid program." });
            if (program.EntityId != dept.EntityId)
                return Json(new { ok = false, error = "The selected program belongs to a different entity than this cost center." });

            var adminEntityId = GetAdminScopedEntityId();
            if (adminEntityId.HasValue && adminEntityId.Value != dept.EntityId)
                return Json(new { ok = false, error = "You are not permitted to add activities for this entity." });

            if (await _context.Activities.AnyAsync(a => a.DepartmentId == departmentId && a.ActivityCode == activityCode))
                return Json(new { ok = false, error = $"An activity with code '{activityCode}' already exists in this cost center." });

            var activity = new Activities
            {
                ActivityCode = activityCode,
                ActivityName = activityName,
                ProgramId = programId,
                DepartmentId = departmentId,
                IsActive = true
            };

            try
            {
                _context.Activities.Add(activity);
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                return Json(new { ok = false, error = "Could not save activity: " + (ex.InnerException?.Message ?? ex.Message) });
            }

            return Json(new
            {
                ok = true,
                activityId = activity.ActivityId,
                display = $"{activity.ActivityCode} - {activity.ActivityName}"
            });
        }

        // GET: Activities
        public async Task<IActionResult> Index()
        {
            var adminEntityId = GetAdminScopedEntityId();
            var list = _context.Activities
                .Include(a => a.Program).ThenInclude(p => p.Entity)
                .Include(a => a.Department).ThenInclude(d => d.Entity)
                .AsQueryable();

            if (adminEntityId.HasValue)
            {
                list = list.Where(a => a.Program.EntityId == adminEntityId.Value);
            }

            list = list.OrderBy(a => a.ActivityCode);

            return View(await list.ToListAsync());
        }

        // GET: Activities/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null) return NotFound();

            var adminEntityId = GetAdminScopedEntityId();
            var activity = await _context.Activities
                .Include(a => a.Program).ThenInclude(p => p.Entity)
                .Include(a => a.Department).ThenInclude(d => d.Entity)
                .FirstOrDefaultAsync(m => m.ActivityId == id);

            if (activity == null) return NotFound();

            if (adminEntityId.HasValue && activity.Program.EntityId != adminEntityId.Value)
            {
                return Forbid();
            }

            return View(activity);
        }

        // GET: Activities/Create
        public IActionResult Create()
        {
            var adminEntityId = GetAdminScopedEntityId();
            PopulateProgramDropDown(allowedEntityId: adminEntityId);
            PopulateDepartmentDropDown(allowedEntityId: adminEntityId);
            return View();
        }

        // POST: Activities/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("ActivityId,ProgramId,DepartmentId,ActivityCode,ActivityName,IsActive")] Activities activities)
        {
            var adminEntityId = GetAdminScopedEntityId();

            ModelState.Remove(nameof(activities.Program));
            ModelState.Remove(nameof(activities.Department));

            if (adminEntityId.HasValue)
            {
                var programEntityId = await _context.Programs
                    .AsNoTracking()
                    .Where(p => p.ProgramId == activities.ProgramId)
                    .Select(p => (int?)p.EntityId)
                    .FirstOrDefaultAsync();

                var deptEntityId = await _context.Departments
                    .AsNoTracking()
                    .Where(d => d.DepartmentId == activities.DepartmentId)
                    .Select(d => (int?)d.EntityId)
                    .FirstOrDefaultAsync();

                if (!programEntityId.HasValue || programEntityId.Value != adminEntityId.Value
                    || !deptEntityId.HasValue || deptEntityId.Value != adminEntityId.Value)
                {
                    return Forbid();
                }
            }

            if (ModelState.IsValid)
            {
                _context.Add(activities);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            PopulateProgramDropDown(selectedId: activities.ProgramId, allowedEntityId: adminEntityId);
            PopulateDepartmentDropDown(selectedId: activities.DepartmentId, allowedEntityId: adminEntityId);
            return View(activities);
        }

        // GET: Activities/Template
        [HttpGet]
        public IActionResult Template()
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Activities");

            ws.Cell(1, 1).Value = "EntityCode";
            ws.Cell(1, 2).Value = "ProgramCode";
            ws.Cell(1, 3).Value = "DeptCode";
            ws.Cell(1, 4).Value = "ActivityCode";
            ws.Cell(1, 5).Value = "ActivityName";
            ws.Cell(1, 6).Value = "IsActive";

            ws.Cell(2, 1).Value = "ENT001";
            ws.Cell(2, 2).Value = "PRG001";
            ws.Cell(2, 3).Value = "CC001";
            ws.Cell(2, 4).Value = "ACT001";
            ws.Cell(2, 5).Value = "Sample Activity";
            ws.Cell(2, 6).Value = "TRUE";

            ws.Range(1, 1, 1, 6).Style.Font.Bold = true;
            ws.Columns(1, 6).AdjustToContents();

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Activities_Template.xlsx");
        }

        // GET: Activities/Export
        // Exports the same rows the Index grid shows (entity-scoped for entity admins).
        // The first six columns match the upload template, so an exported file can be edited
        // and uploaded straight back; the trailing name columns are for readability only.
        [HttpGet]
        public async Task<IActionResult> Export()
        {
            var adminEntityId = GetAdminScopedEntityId();
            var query = _context.Activities.AsNoTracking()
                .Include(a => a.Program).ThenInclude(p => p.Entity)
                .Include(a => a.Department).ThenInclude(d => d.Entity)
                .AsQueryable();

            if (adminEntityId.HasValue)
            {
                query = query.Where(a => a.Program.EntityId == adminEntityId.Value);
            }

            var list = await query.OrderBy(a => a.ActivityCode).ToListAsync();

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Activities");

            var headers = new[]
            {
                "EntityCode", "ProgramCode", "DeptCode", "ActivityCode", "ActivityName", "IsActive",
                "Entity Name", "Program Name", "Dept Name"
            };
            for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
            var head = ws.Range(1, 1, 1, headers.Length).Style;
            head.Font.Bold = true;
            head.Fill.BackgroundColor = XLColor.FromHtml(GovBudget.Utils.BrandColors.HeaderHex);
            head.Font.FontColor = XLColor.White;

            int r = 2;
            foreach (var a in list)
            {
                ws.Cell(r, 1).Value = a.Program?.Entity?.EntityCode ?? a.Department?.Entity?.EntityCode ?? "";
                ws.Cell(r, 2).Value = a.Program?.ProgramCode ?? "";
                ws.Cell(r, 3).Value = a.Department?.DeptCode ?? "";
                ws.Cell(r, 4).Value = a.ActivityCode;
                ws.Cell(r, 5).Value = a.ActivityName;
                ws.Cell(r, 6).Value = a.IsActive ? "TRUE" : "FALSE";
                ws.Cell(r, 7).Value = a.Program?.Entity?.EntityName ?? a.Department?.Entity?.EntityName ?? "";
                ws.Cell(r, 8).Value = a.Program?.ProgramName ?? "";
                ws.Cell(r, 9).Value = a.Department?.DeptName ?? "";
                r++;
            }

            ws.SheetView.FreezeRows(1);
            if (list.Count > 0) ws.Range(1, 1, r - 1, headers.Length).SetAutoFilter();
            ws.Columns(1, headers.Length).AdjustToContents();

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return File(stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"Activities_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
        }

        // POST: Activities/Upload
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

            if (!headerMap.TryGetValue("EntityCode", out var entityCol) ||
                !headerMap.TryGetValue("ProgramCode", out var programCol) ||
                !headerMap.TryGetValue("DeptCode", out var deptCol) ||
                !headerMap.TryGetValue("ActivityCode", out var codeCol) ||
                !headerMap.TryGetValue("ActivityName", out var nameCol))
            {
                TempData["Error"] = "Template columns must include: EntityCode, ProgramCode, DeptCode, ActivityCode, ActivityName. Optional: IsActive.";
                return RedirectToAction(nameof(Index));
            }
            headerMap.TryGetValue("IsActive", out var activeCol);

            var entityByCode = await _context.Entities.AsNoTracking()
                .Where(e => !string.IsNullOrWhiteSpace(e.EntityCode))
                .ToDictionaryAsync(e => e.EntityCode.Trim(), e => e, StringComparer.OrdinalIgnoreCase);

            var programs = await _context.Programs.AsNoTracking().ToListAsync();
            var programByKey = programs
                .Where(p => !string.IsNullOrWhiteSpace(p.ProgramCode))
                .ToDictionary(p => (p.EntityId, p.ProgramCode.Trim().ToUpperInvariant()), p => p);

            var depts = await _context.Departments.AsNoTracking().ToListAsync();
            var deptByKey = depts
                .Where(d => !string.IsNullOrWhiteSpace(d.DeptCode))
                .ToDictionary(d => (d.EntityId, d.DeptCode.Trim().ToUpperInvariant()), d => d);

            var existing = await _context.Activities.ToListAsync();
            var byKey = existing
                .Where(a => !string.IsNullOrWhiteSpace(a.ActivityCode))
                .ToDictionary(a => (a.ProgramId, a.ActivityCode.Trim().ToUpperInvariant()), a => a);

            var created = 0;
            var updated = 0;
            var errors = new List<string>();

            var firstDataRow = headerRow.RowNumber() + 1;
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? firstDataRow - 1;

            for (var r = firstDataRow; r <= lastRow; r++)
            {
                var row = ws.Row(r);
                var entityCode = row.Cell(entityCol).GetString().Trim();
                var programCode = row.Cell(programCol).GetString().Trim();
                var deptCode = row.Cell(deptCol).GetString().Trim();
                var code = row.Cell(codeCol).GetString().Trim();
                var nm = row.Cell(nameCol).GetString().Trim();
                var activeRaw = activeCol > 0 ? row.Cell(activeCol).GetString().Trim() : "";
                var isActive = ParseBoolOrDefault(activeRaw, true);

                if (string.IsNullOrWhiteSpace(entityCode) && string.IsNullOrWhiteSpace(programCode) &&
                    string.IsNullOrWhiteSpace(deptCode) && string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(nm)) continue;

                if (string.IsNullOrWhiteSpace(entityCode)) { errors.Add($"Row {r}: EntityCode is required."); if (errors.Count >= 20) break; continue; }
                if (string.IsNullOrWhiteSpace(programCode)) { errors.Add($"Row {r}: ProgramCode is required."); if (errors.Count >= 20) break; continue; }
                if (string.IsNullOrWhiteSpace(deptCode)) { errors.Add($"Row {r}: DeptCode is required."); if (errors.Count >= 20) break; continue; }
                if (string.IsNullOrWhiteSpace(code)) { errors.Add($"Row {r}: ActivityCode is required."); if (errors.Count >= 20) break; continue; }
                if (string.IsNullOrWhiteSpace(nm)) { errors.Add($"Row {r}: ActivityName is required."); if (errors.Count >= 20) break; continue; }

                if (!entityByCode.TryGetValue(entityCode, out var ent))
                {
                    errors.Add($"Row {r}: EntityCode '{entityCode}' was not found.");
                    if (errors.Count >= 20) break;
                    continue;
                }
                if (adminEntityId.HasValue && ent.EntityId != adminEntityId.Value)
                {
                    errors.Add($"Row {r}: EntityCode '{entityCode}' is not allowed for this user.");
                    if (errors.Count >= 20) break;
                    continue;
                }

                if (!programByKey.TryGetValue((ent.EntityId, programCode.ToUpperInvariant()), out var program))
                {
                    errors.Add($"Row {r}: ProgramCode '{programCode}' was not found under entity '{entityCode}'.");
                    if (errors.Count >= 20) break;
                    continue;
                }
                if (!deptByKey.TryGetValue((ent.EntityId, deptCode.ToUpperInvariant()), out var dept))
                {
                    errors.Add($"Row {r}: DeptCode '{deptCode}' was not found under entity '{entityCode}'.");
                    if (errors.Count >= 20) break;
                    continue;
                }

                var key = (program.ProgramId, code.ToUpperInvariant());
                if (byKey.TryGetValue(key, out var existingActivity))
                {
                    existingActivity.ActivityName = nm;
                    existingActivity.DepartmentId = dept.DepartmentId;
                    existingActivity.IsActive = isActive;
                    updated++;
                }
                else
                {
                    var activity = new Activities
                    {
                        ProgramId = program.ProgramId,
                        DepartmentId = dept.DepartmentId,
                        ActivityCode = code,
                        ActivityName = nm,
                        IsActive = isActive
                    };
                    _context.Activities.Add(activity);
                    byKey[key] = activity;
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

        // GET: Activities/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();

            var adminEntityId = GetAdminScopedEntityId();
            var activities = await _context.Activities
                .Include(a => a.Program)
                .FirstOrDefaultAsync(a => a.ActivityId == id);
            if (activities == null) return NotFound();

            if (adminEntityId.HasValue && activities.Program.EntityId != adminEntityId.Value)
            {
                return Forbid();
            }

            PopulateProgramDropDown(selectedId: activities.ProgramId, allowedEntityId: adminEntityId);
            PopulateDepartmentDropDown(selectedId: activities.DepartmentId, allowedEntityId: adminEntityId);
            return View(activities);
        }

        // POST: Activities/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("ActivityId,ProgramId,DepartmentId,ActivityCode,ActivityName,IsActive")] Activities activities)
        {
            if (id != activities.ActivityId) return NotFound();

            var adminEntityId = GetAdminScopedEntityId();

            ModelState.Remove(nameof(activities.Program));
            ModelState.Remove(nameof(activities.Department));

            if (adminEntityId.HasValue)
            {
                var existing = await _context.Activities
                    .AsNoTracking()
                    .Include(a => a.Program)
                    .FirstOrDefaultAsync(a => a.ActivityId == id);
                if (existing == null) return NotFound();
                if (existing.Program.EntityId != adminEntityId.Value) return Forbid();

                var programEntityId = await _context.Programs
                    .AsNoTracking()
                    .Where(p => p.ProgramId == activities.ProgramId)
                    .Select(p => (int?)p.EntityId)
                    .FirstOrDefaultAsync();

                var deptEntityId = await _context.Departments
                    .AsNoTracking()
                    .Where(d => d.DepartmentId == activities.DepartmentId)
                    .Select(d => (int?)d.EntityId)
                    .FirstOrDefaultAsync();

                if (!programEntityId.HasValue || programEntityId.Value != adminEntityId.Value
                    || !deptEntityId.HasValue || deptEntityId.Value != adminEntityId.Value)
                {
                    return Forbid();
                }
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(activities);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!ActivitiesExists(activities.ActivityId)) return NotFound();
                    else throw;
                }
                return RedirectToAction(nameof(Index));
            }
            PopulateProgramDropDown(selectedId: activities.ProgramId, allowedEntityId: adminEntityId);
            PopulateDepartmentDropDown(selectedId: activities.DepartmentId, allowedEntityId: adminEntityId);
            return View(activities);
        }

        // Counts the records that reference this activity. An activity that is already used
        // anywhere in the budget cannot be deleted without destroying financial history, so we
        // report what is blocking the delete instead of letting SQL raise an FK violation.
        private async Task<List<(string Label, int Count)>> GetActivityDependenciesAsync(int activityId)
        {
            var deps = new List<(string Label, int Count)>();

            var budgetLines = await _context.BudgetLines.CountAsync(b => b.ActivityId == activityId);
            if (budgetLines > 0) deps.Add(($"{budgetLines} budget line(s)", budgetLines));

            var hrAlloc = await _context.HrEmployeeCostAllocations.CountAsync(h => h.ActivityId == activityId);
            if (hrAlloc > 0) deps.Add(($"{hrAlloc} HR cost allocation(s)", hrAlloc));

            var kpis = await _context.Kpis.CountAsync(k => k.ActivityId == activityId);
            if (kpis > 0) deps.Add(($"{kpis} KPI(s)", kpis));

            var outputs = await _context.ActivityOutputs.CountAsync(o => o.ActivityId == activityId);
            if (outputs > 0) deps.Add(($"{outputs} activity output(s)", outputs));

            var txns = await _context.AllocationTransactions
                .CountAsync(t => t.SourceActivityId == activityId || t.TargetActivityId == activityId);
            if (txns > 0) deps.Add(($"{txns} cost-allocation transaction(s)", txns));

            return deps;
        }

        // GET: Activities/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null) return NotFound();

            var adminEntityId = GetAdminScopedEntityId();

            var activities = await _context.Activities
                .Include(a => a.Program).ThenInclude(p => p.Entity)
                .Include(a => a.Department).ThenInclude(d => d.Entity)
                .FirstOrDefaultAsync(m => m.ActivityId == id);

            if (activities == null) return NotFound();

            if (adminEntityId.HasValue && activities.Program.EntityId != adminEntityId.Value)
            {
                return Forbid();
            }

            var deps = await GetActivityDependenciesAsync(activities.ActivityId);
            ViewBag.Dependencies = deps.Select(d => d.Label).ToList();
            ViewBag.CanDelete = deps.Count == 0;

            return View(activities);
        }

        // POST: Activities/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var adminEntityId = GetAdminScopedEntityId();
            var activities = await _context.Activities
                .Include(a => a.Program)
                .FirstOrDefaultAsync(a => a.ActivityId == id);
            if (activities != null)
            {
                if (adminEntityId.HasValue && activities.Program.EntityId != adminEntityId.Value)
                {
                    return Forbid();
                }

                var deps = await GetActivityDependenciesAsync(id);
                if (deps.Count > 0)
                {
                    TempData["Error"] = $"'{activities.ActivityCode} - {activities.ActivityName}' cannot be deleted because it is still used by: "
                        + string.Join(", ", deps.Select(d => d.Label))
                        + ". Remove or reassign those records first, or mark the activity as inactive instead so the history is preserved.";
                    return RedirectToAction(nameof(Index));
                }

                try
                {
                    _context.Activities.Remove(activities);
                    await _context.SaveChangesAsync();
                    TempData["Success"] = $"Activity '{activities.ActivityCode}' was deleted.";
                }
                catch (DbUpdateException)
                {
                    TempData["Error"] = $"'{activities.ActivityCode} - {activities.ActivityName}' cannot be deleted because other records still reference it. "
                        + "Mark the activity as inactive instead so the history is preserved.";
                }
            }
            return RedirectToAction(nameof(Index));
        }

        // POST: Activities/Deactivate/5 — the safe alternative to deleting an activity that is in use.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Deactivate(int id)
        {
            var adminEntityId = GetAdminScopedEntityId();
            var activity = await _context.Activities
                .Include(a => a.Program)
                .FirstOrDefaultAsync(a => a.ActivityId == id);

            if (activity == null) return NotFound();
            if (adminEntityId.HasValue && activity.Program.EntityId != adminEntityId.Value) return Forbid();

            activity.IsActive = false;
            await _context.SaveChangesAsync();
            TempData["Success"] = $"Activity '{activity.ActivityCode}' was marked inactive. It is hidden from new entries but its history is preserved.";
            return RedirectToAction(nameof(Index));
        }

        private bool ActivitiesExists(int id) => _context.Activities.Any(e => e.ActivityId == id);

        private void PopulateProgramDropDown(int? selectedId = null, int? allowedEntityId = null)
        {
            var progsQuery = _context.Programs
                .Include(p => p.Entity)
                .Where(p => p.IsActive)
                .AsQueryable();

            if (allowedEntityId.HasValue)
            {
                progsQuery = progsQuery.Where(p => p.EntityId == allowedEntityId.Value);
            }

            var progs = progsQuery
                .OrderBy(p => p.ProgramCode)
                .Select(p => new
                {
                    p.ProgramId,
                    Display = p.ProgramCode + " — " + p.ProgramName + " (" + p.Entity.EntityCode + ")"
                })
                .ToList();

            ViewData["ProgramId"] = new SelectList(progs, "ProgramId", "Display", selectedId);
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

            ViewData["DepartmentId"] = new SelectList(deps, "DepartmentId", "Display", selectedId);
        }
    }
}
