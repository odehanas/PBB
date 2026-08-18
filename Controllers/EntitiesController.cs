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
    public class EntitiesController : Controller
    {
        private readonly GovBudgetContext _context;

        public EntitiesController(GovBudgetContext context)
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

        // GET: Entities
        public async Task<IActionResult> Index()
        {
            var adminEntityId = GetAdminScopedEntityId();
            var q = _context.Entities.AsNoTracking().AsQueryable();
            if (adminEntityId.HasValue)
            {
                q = q.Where(e => e.EntityId == adminEntityId.Value);
            }
            return View(await q.ToListAsync());
        }

        // GET: Entities/Template
        [HttpGet]
        public IActionResult Template()
        {
            if (!IsGlobalAdmin()) return Forbid();

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Entities");

            ws.Cell(1, 1).Value = "EntityCode";
            ws.Cell(1, 2).Value = "EntityName";
            ws.Cell(1, 3).Value = "IsActive";

            ws.Cell(2, 1).Value = "ENT001";
            ws.Cell(2, 2).Value = "Sample Entity";
            ws.Cell(2, 3).Value = "TRUE";

            ws.Range(1, 1, 1, 3).Style.Font.Bold = true;
            ws.Columns(1, 3).AdjustToContents();

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Entities_Template.xlsx");
        }

        // POST: Entities/Upload
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Upload(IFormFile? file)
        {
            if (!IsGlobalAdmin()) return Forbid();

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

            if (!headerMap.TryGetValue("EntityCode", out var codeCol) ||
                !headerMap.TryGetValue("EntityName", out var nameCol))
            {
                TempData["Error"] = "Template columns must include: EntityCode, EntityName. Optional: IsActive.";
                return RedirectToAction(nameof(Index));
            }
            headerMap.TryGetValue("IsActive", out var activeCol);

            var existing = await _context.Entities.ToListAsync();
            var byCode = existing
                .Where(x => !string.IsNullOrWhiteSpace(x.EntityCode))
                .ToDictionary(x => x.EntityCode.Trim(), x => x, StringComparer.OrdinalIgnoreCase);

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
                var activeRaw = activeCol > 0 ? row.Cell(activeCol).GetString().Trim() : "";
                var isActive = ParseBoolOrDefault(activeRaw, true);

                if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(nm)) continue;
                if (string.IsNullOrWhiteSpace(code)) { errors.Add($"Row {r}: EntityCode is required."); if (errors.Count >= 20) break; continue; }
                if (string.IsNullOrWhiteSpace(nm)) { errors.Add($"Row {r}: EntityName is required."); if (errors.Count >= 20) break; continue; }

                if (byCode.TryGetValue(code, out var ex))
                {
                    ex.EntityName = nm;
                    ex.IsActive = isActive;
                    updated++;
                }
                else
                {
                    var ent = new Entities { EntityCode = code, EntityName = nm, IsActive = isActive };
                    _context.Entities.Add(ent);
                    byCode[code] = ent;
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

        // GET: Entities/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var adminEntityId = GetAdminScopedEntityId();
            if (adminEntityId.HasValue && id.Value != adminEntityId.Value)
            {
                return Forbid();
            }

            var entities = await _context.Entities
                .FirstOrDefaultAsync(m => m.EntityId == id);
            if (entities == null)
            {
                return NotFound();
            }

            return View(entities);
        }

        // GET: Entities/Create
        public IActionResult Create()
        {
            if (!IsGlobalAdmin())
            {
                return Forbid();
            }
            return View();
        }

        // POST: Entities/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to.
        // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("EntityId,EntityCode,EntityName,IsActive")] Entities entities)
        {
            if (!IsGlobalAdmin())
            {
                return Forbid();
            }
            if (ModelState.IsValid)
            {
                _context.Add(entities);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            return View(entities);
        }

        // GET: Entities/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            if (!IsGlobalAdmin())
            {
                return Forbid();
            }

            var entities = await _context.Entities.FindAsync(id);
            if (entities == null)
            {
                return NotFound();
            }
            return View(entities);
        }

        // POST: Entities/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to.
        // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("EntityId,EntityCode,EntityName,IsActive")] Entities entities)
        {
            if (id != entities.EntityId)
            {
                return NotFound();
            }

            if (!IsGlobalAdmin())
            {
                return Forbid();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(entities);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!EntitiesExists(entities.EntityId))
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
            return View(entities);
        }

        // GET: Entities/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            if (!IsGlobalAdmin())
            {
                return Forbid();
            }

            var entities = await _context.Entities
                .FirstOrDefaultAsync(m => m.EntityId == id);
            if (entities == null)
            {
                return NotFound();
            }

            return View(entities);
        }

        // POST: Entities/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            if (!IsGlobalAdmin())
            {
                return Forbid();
            }

            var entities = await _context.Entities.FindAsync(id);
            if (entities != null)
            {
                _context.Entities.Remove(entities);
            }

            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool EntitiesExists(int id)
        {
            return _context.Entities.Any(e => e.EntityId == id);
        }
    }
}
