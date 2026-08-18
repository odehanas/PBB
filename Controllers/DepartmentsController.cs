using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
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
    public class DepartmentsController : Controller
    {
        private readonly GovBudgetContext _context;

        public DepartmentsController(GovBudgetContext context)
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

        // GET: Departments
        public async Task<IActionResult> Index()
        {
            var adminEntityId = GetAdminScopedEntityId();
            var govBudgetContext = _context.Departments.Include(d => d.Entity).AsQueryable();
            if (adminEntityId.HasValue)
            {
                govBudgetContext = govBudgetContext.Where(d => d.EntityId == adminEntityId.Value);
            }
            return View(await govBudgetContext.ToListAsync());
        }

        // GET: Departments/Template
        [HttpGet]
        public IActionResult Template()
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("CostCenters");

            ws.Cell(1, 1).Value = "EntityCode";
            ws.Cell(1, 2).Value = "DeptCode";
            ws.Cell(1, 3).Value = "DeptName";
            ws.Cell(1, 4).Value = "IsActive";

            ws.Cell(2, 1).Value = "ENT001";
            ws.Cell(2, 2).Value = "CC001";
            ws.Cell(2, 3).Value = "Sample Cost Center";
            ws.Cell(2, 4).Value = "TRUE";

            ws.Range(1, 1, 1, 4).Style.Font.Bold = true;
            ws.Columns(1, 4).AdjustToContents();

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "CostCenters_Template.xlsx");
        }

        // POST: Departments/Upload
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
                !headerMap.TryGetValue("DeptCode", out var deptCodeCol) ||
                !headerMap.TryGetValue("DeptName", out var deptNameCol))
            {
                TempData["Error"] = "Template columns must include: EntityCode, DeptCode, DeptName. Optional: IsActive.";
                return RedirectToAction(nameof(Index));
            }
            headerMap.TryGetValue("IsActive", out var activeCol);

            var entityByCode = await _context.Entities.AsNoTracking()
                .Where(e => !string.IsNullOrWhiteSpace(e.EntityCode))
                .ToDictionaryAsync(e => e.EntityCode.Trim(), e => e, StringComparer.OrdinalIgnoreCase);

            var existing = await _context.Departments.ToListAsync();
            var byKey = existing
                .Where(d => !string.IsNullOrWhiteSpace(d.DeptCode))
                .ToDictionary(d => (d.EntityId, d.DeptCode.Trim().ToUpperInvariant()), d => d);

            var created = 0;
            var updated = 0;
            var errors = new List<string>();

            var firstDataRow = headerRow.RowNumber() + 1;
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? firstDataRow - 1;

            for (var r = firstDataRow; r <= lastRow; r++)
            {
                var row = ws.Row(r);
                var entityCode = row.Cell(entityCol).GetString().Trim();
                var deptCode = row.Cell(deptCodeCol).GetString().Trim();
                var deptName = row.Cell(deptNameCol).GetString().Trim();
                var activeRaw = activeCol > 0 ? row.Cell(activeCol).GetString().Trim() : "";
                var isActive = ParseBoolOrDefault(activeRaw, true);

                if (string.IsNullOrWhiteSpace(entityCode) && string.IsNullOrWhiteSpace(deptCode) && string.IsNullOrWhiteSpace(deptName)) continue;
                if (string.IsNullOrWhiteSpace(entityCode)) { errors.Add($"Row {r}: EntityCode is required."); if (errors.Count >= 20) break; continue; }
                if (string.IsNullOrWhiteSpace(deptCode)) { errors.Add($"Row {r}: DeptCode is required."); if (errors.Count >= 20) break; continue; }
                if (string.IsNullOrWhiteSpace(deptName)) { errors.Add($"Row {r}: DeptName is required."); if (errors.Count >= 20) break; continue; }

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

                var key = (ent.EntityId, deptCode.ToUpperInvariant());
                if (byKey.TryGetValue(key, out var existingDept))
                {
                    existingDept.DeptName = deptName;
                    existingDept.IsActive = isActive;
                    updated++;
                }
                else
                {
                    var dept = new Departments { EntityId = ent.EntityId, DeptCode = deptCode, DeptName = deptName, IsActive = isActive };
                    _context.Departments.Add(dept);
                    byKey[key] = dept;
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

        // GET: Departments/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var adminEntityId = GetAdminScopedEntityId();
            var departments = await _context.Departments
                .Include(d => d.Entity)
                .FirstOrDefaultAsync(m => m.DepartmentId == id);
            if (departments == null)
            {
                return NotFound();
            }

            if (adminEntityId.HasValue && departments.EntityId != adminEntityId.Value)
            {
                return Forbid();
            }

            return View(departments);
        }

        // GET: Departments/Create
        public IActionResult Create()
        {
            var adminEntityId = GetAdminScopedEntityId();
            var entitiesQuery = _context.Entities.AsQueryable();
            if (adminEntityId.HasValue)
            {
                entitiesQuery = entitiesQuery.Where(e => e.EntityId == adminEntityId.Value);
            }
            var entities = entitiesQuery.Select(e => new { e.EntityId, Display = e.EntityCode + " - " + e.EntityName }).ToList();
            ViewData["EntityId"] = new SelectList(entities, "EntityId", "Display", adminEntityId);
            return View();
        }

        // POST: Departments/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to.
        // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("DepartmentId,EntityId,DeptCode,DeptName,IsActive")] Departments departments)
        {
            var adminEntityId = GetAdminScopedEntityId();
            if (adminEntityId.HasValue && departments.EntityId != adminEntityId.Value)
            {
                return Forbid();
            }

            ModelState.Remove(nameof(departments.Entity));

            if (ModelState.IsValid)
            {
                _context.Add(departments);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            var entitiesQuery = _context.Entities.AsQueryable();
            if (adminEntityId.HasValue)
            {
                entitiesQuery = entitiesQuery.Where(e => e.EntityId == adminEntityId.Value);
                departments.EntityId = adminEntityId.Value;
            }
            var entities = entitiesQuery.Select(e => new { e.EntityId, Display = e.EntityCode + " - " + e.EntityName }).ToList();
            ViewData["EntityId"] = new SelectList(entities, "EntityId", "Display", departments.EntityId);
            return View(departments);
        }

        // GET: Departments/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var departments = await _context.Departments.FindAsync(id);
            if (departments == null)
            {
                return NotFound();
            }
            var adminEntityId = GetAdminScopedEntityId();
            if (adminEntityId.HasValue && departments.EntityId != adminEntityId.Value)
            {
                return Forbid();
            }
            var entitiesQuery = _context.Entities.AsQueryable();
            if (adminEntityId.HasValue)
            {
                entitiesQuery = entitiesQuery.Where(e => e.EntityId == adminEntityId.Value);
            }
            var entities = entitiesQuery.Select(e => new { e.EntityId, Display = e.EntityCode + " - " + e.EntityName }).ToList();
            ViewData["EntityId"] = new SelectList(entities, "EntityId", "Display", departments.EntityId);
            return View(departments);
        }

        // POST: Departments/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to.
        // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("DepartmentId,EntityId,DeptCode,DeptName,IsActive")] Departments departments)
        {
            if (id != departments.DepartmentId)
            {
                return NotFound();
            }

            var adminEntityId = GetAdminScopedEntityId();
            if (adminEntityId.HasValue && departments.EntityId != adminEntityId.Value)
            {
                return Forbid();
            }

            ModelState.Remove(nameof(departments.Entity));

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(departments);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!DepartmentsExists(departments.DepartmentId))
                    {
                        return NotFound();
                    }
                    else
                    {
                        throw;
                    }
                }
                return RedirectToAction(nameof(Index));
            }
            var entitiesQuery = _context.Entities.AsQueryable();
            if (adminEntityId.HasValue)
            {
                entitiesQuery = entitiesQuery.Where(e => e.EntityId == adminEntityId.Value);
                departments.EntityId = adminEntityId.Value;
            }
            var entities = entitiesQuery.Select(e => new { e.EntityId, Display = e.EntityCode + " - " + e.EntityName }).ToList();
            ViewData["EntityId"] = new SelectList(entities, "EntityId", "Display", departments.EntityId);
            return View(departments);
        }

        // GET: Departments/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var adminEntityId = GetAdminScopedEntityId();
            var departments = await _context.Departments
                .Include(d => d.Entity)
                .FirstOrDefaultAsync(m => m.DepartmentId == id);
            if (departments == null)
            {
                return NotFound();
            }

            if (adminEntityId.HasValue && departments.EntityId != adminEntityId.Value)
            {
                return Forbid();
            }

            return View(departments);
        }

        // POST: Departments/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var departments = await _context.Departments.FindAsync(id);
            if (departments != null)
            {
                var adminEntityId = GetAdminScopedEntityId();
                if (adminEntityId.HasValue && departments.EntityId != adminEntityId.Value)
                {
                    return Forbid();
                }
                _context.Departments.Remove(departments);
            }

            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool DepartmentsExists(int id)
        {
            return _context.Departments.Any(e => e.DepartmentId == id);
        }
    }
}
