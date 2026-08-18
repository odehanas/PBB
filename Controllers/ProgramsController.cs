using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
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
    public class ProgramsController : Controller
    {
        private readonly GovBudgetContext _context;

        public ProgramsController(GovBudgetContext context)
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

        // GET: Programs
        public async Task<IActionResult> Index()
        {
            var adminEntityId = GetAdminScopedEntityId();
            var list = _context.Programs
                .Include(p => p.Entity)
                .AsQueryable();

            if (adminEntityId.HasValue)
            {
                list = list.Where(p => p.EntityId == adminEntityId.Value);
            }

            list = list.OrderBy(p => p.ProgramCode);

            return View(await list.ToListAsync());
        }

        // GET: Programs/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null) return NotFound();

            var adminEntityId = GetAdminScopedEntityId();
            var programs = await _context.Programs
                .Include(p => p.Entity)
                .FirstOrDefaultAsync(m => m.ProgramId == id);

            if (programs == null) return NotFound();

            if (adminEntityId.HasValue && programs.EntityId != adminEntityId.Value)
            {
                return Forbid();
            }

            return View(programs);
        }

        // GET: Programs/Create
        public IActionResult Create()
        {
            var adminEntityId = GetAdminScopedEntityId();
            PopulateEntityDropDown(selectedId: adminEntityId, allowedEntityId: adminEntityId);
            return View();
        }

        // POST: Programs/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("ProgramId,EntityId,ProgramCode,ProgramName,IsActive,ProgramType,AllocationSequence")] Programs programs)
        {
            var adminEntityId = GetAdminScopedEntityId();
            if (adminEntityId.HasValue)
            {
                programs.EntityId = adminEntityId.Value;
            }

            ModelState.Remove(nameof(programs.Entity));
            programs.ProgramType = string.Equals(programs.ProgramType, "Support", StringComparison.OrdinalIgnoreCase) ? "Support" : "Mandate";
            if (programs.ProgramType != "Support") programs.AllocationSequence = null;

            if (ModelState.IsValid)
            {
                try
                {
                    programs.ProgramCode = (programs.ProgramCode ?? "").Trim();
                    programs.ProgramName = (programs.ProgramName ?? "").Trim();

                    if (string.IsNullOrWhiteSpace(programs.ProgramCode))
                    {
                        ModelState.AddModelError(nameof(programs.ProgramCode), "Program Code is required.");
                    }
                    if (string.IsNullOrWhiteSpace(programs.ProgramName))
                    {
                        ModelState.AddModelError(nameof(programs.ProgramName), "Program Name is required.");
                    }

                    if (ModelState.IsValid)
                    {
                        var exists = await _context.Programs.AsNoTracking()
                            .AnyAsync(p => p.EntityId == programs.EntityId && p.ProgramCode == programs.ProgramCode);
                        if (exists)
                        {
                            ModelState.AddModelError(nameof(programs.ProgramCode), "Program Code already exists for this entity.");
                            TempData["Error"] = "Program Code already exists for this entity.";
                        }
                        else
                        {
                            _context.Add(programs);
                            await _context.SaveChangesAsync();
                            TempData["Success"] = "Program saved.";
                            return RedirectToAction(nameof(Index));
                        }
                    }
                }
                catch (DbUpdateException ex)
                {
                    TempData["Error"] = ex.GetBaseException().Message;
                    ModelState.AddModelError("", $"Could not save program. {ex.GetBaseException().Message}");
                }
                catch (System.Exception ex)
                {
                    TempData["Error"] = ex.GetBaseException().Message;
                    ModelState.AddModelError("", $"Could not save program. {ex.GetBaseException().Message}");
                }
            }

            PopulateEntityDropDown(selectedId: programs.EntityId, allowedEntityId: adminEntityId);
            return View(programs);
        }

        // GET: Programs/Template
        [HttpGet]
        public IActionResult Template()
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Programs");

            ws.Cell(1, 1).Value = "EntityCode";
            ws.Cell(1, 2).Value = "ProgramCode";
            ws.Cell(1, 3).Value = "ProgramName";
            ws.Cell(1, 4).Value = "ProgramType";
            ws.Cell(1, 5).Value = "AllocationSequence";
            ws.Cell(1, 6).Value = "IsActive";

            ws.Cell(2, 1).Value = "ENT001";
            ws.Cell(2, 2).Value = "PRG001";
            ws.Cell(2, 3).Value = "Sample Mandate Program";
            ws.Cell(2, 4).Value = "Mandate";
            ws.Cell(2, 5).Value = "";
            ws.Cell(2, 6).Value = "TRUE";

            ws.Cell(3, 1).Value = "ENT001";
            ws.Cell(3, 2).Value = "PRG900";
            ws.Cell(3, 3).Value = "Sample Support Program";
            ws.Cell(3, 4).Value = "Support";
            ws.Cell(3, 5).Value = 1;
            ws.Cell(3, 6).Value = "TRUE";

            ws.Range(1, 1, 1, 6).Style.Font.Bold = true;
            ws.Columns(1, 6).AdjustToContents();

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Programs_Template.xlsx");
        }

        // POST: Programs/Upload
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
                !headerMap.TryGetValue("ProgramCode", out var codeCol) ||
                !headerMap.TryGetValue("ProgramName", out var nameCol))
            {
                TempData["Error"] = "Template columns must include: EntityCode, ProgramCode, ProgramName. Optional: ProgramType, AllocationSequence, IsActive.";
                return RedirectToAction(nameof(Index));
            }
            headerMap.TryGetValue("ProgramType", out var typeCol);
            headerMap.TryGetValue("AllocationSequence", out var seqCol);
            headerMap.TryGetValue("IsActive", out var activeCol);

            var entityByCode = await _context.Entities.AsNoTracking()
                .Where(e => !string.IsNullOrWhiteSpace(e.EntityCode))
                .ToDictionaryAsync(e => e.EntityCode.Trim(), e => e, StringComparer.OrdinalIgnoreCase);

            var existing = await _context.Programs.ToListAsync();
            var byKey = existing
                .Where(p => !string.IsNullOrWhiteSpace(p.ProgramCode))
                .ToDictionary(p => (p.EntityId, p.ProgramCode.Trim().ToUpperInvariant()), p => p);

            var created = 0;
            var updated = 0;
            var errors = new List<string>();

            var firstDataRow = headerRow.RowNumber() + 1;
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? firstDataRow - 1;

            for (var r = firstDataRow; r <= lastRow; r++)
            {
                var row = ws.Row(r);
                var entityCode = row.Cell(entityCol).GetString().Trim();
                var code = row.Cell(codeCol).GetString().Trim();
                var nm = row.Cell(nameCol).GetString().Trim();
                var typeRaw = typeCol > 0 ? row.Cell(typeCol).GetString().Trim() : "";
                var seqRaw = seqCol > 0 ? row.Cell(seqCol).GetString().Trim() : "";
                var activeRaw = activeCol > 0 ? row.Cell(activeCol).GetString().Trim() : "";
                var isActive = ParseBoolOrDefault(activeRaw, true);

                if (string.IsNullOrWhiteSpace(entityCode) && string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(nm)) continue;
                if (string.IsNullOrWhiteSpace(entityCode)) { errors.Add($"Row {r}: EntityCode is required."); if (errors.Count >= 20) break; continue; }
                if (string.IsNullOrWhiteSpace(code)) { errors.Add($"Row {r}: ProgramCode is required."); if (errors.Count >= 20) break; continue; }
                if (string.IsNullOrWhiteSpace(nm)) { errors.Add($"Row {r}: ProgramName is required."); if (errors.Count >= 20) break; continue; }

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

                var programType = string.Equals(typeRaw, "Support", StringComparison.OrdinalIgnoreCase) ? "Support" : "Mandate";
                int? allocationSequence = null;
                if (programType == "Support" && int.TryParse(seqRaw, out var seq)) allocationSequence = seq;

                var key = (ent.EntityId, code.ToUpperInvariant());
                if (byKey.TryGetValue(key, out var existingProgram))
                {
                    existingProgram.ProgramName = nm;
                    existingProgram.ProgramType = programType;
                    existingProgram.AllocationSequence = allocationSequence;
                    existingProgram.IsActive = isActive;
                    updated++;
                }
                else
                {
                    var program = new Programs
                    {
                        EntityId = ent.EntityId,
                        ProgramCode = code,
                        ProgramName = nm,
                        ProgramType = programType,
                        AllocationSequence = allocationSequence,
                        IsActive = isActive
                    };
                    _context.Programs.Add(program);
                    byKey[key] = program;
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

        // GET: Programs/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();

            var adminEntityId = GetAdminScopedEntityId();
            var programs = await _context.Programs.FindAsync(id);
            if (programs == null) return NotFound();

            if (adminEntityId.HasValue && programs.EntityId != adminEntityId.Value)
            {
                return Forbid();
            }

            PopulateEntityDropDown(selectedId: programs.EntityId, allowedEntityId: adminEntityId);
            return View(programs);
        }

        // POST: Programs/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("ProgramId,EntityId,ProgramCode,ProgramName,IsActive,ProgramType,AllocationSequence")] Programs programs)
        {
            if (id != programs.ProgramId) return NotFound();

            var adminEntityId = GetAdminScopedEntityId();
            if (adminEntityId.HasValue)
            {
                programs.EntityId = adminEntityId.Value;
            }

            ModelState.Remove(nameof(programs.Entity));
            programs.ProgramType = string.Equals(programs.ProgramType, "Support", StringComparison.OrdinalIgnoreCase) ? "Support" : "Mandate";
            if (programs.ProgramType != "Support") programs.AllocationSequence = null;

            if (ModelState.IsValid)
            {
                programs.ProgramCode = (programs.ProgramCode ?? "").Trim();
                programs.ProgramName = (programs.ProgramName ?? "").Trim();

                var exists = await _context.Programs.AsNoTracking()
                    .AnyAsync(p => p.ProgramId != programs.ProgramId
                                   && p.EntityId == programs.EntityId
                                   && p.ProgramCode == programs.ProgramCode);
                if (exists)
                {
                    ModelState.AddModelError(nameof(programs.ProgramCode), "Program Code already exists for this entity.");
                }
                else
                {
                try
                {
                    _context.Update(programs);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!ProgramsExists(programs.ProgramId))
                        return NotFound();
                    else
                        throw;
                }
                catch (DbUpdateException ex)
                {
                    ModelState.AddModelError("", $"Could not save program. {ex.GetBaseException().Message}");
                }
                return RedirectToAction(nameof(Index));
                }
            }

            PopulateEntityDropDown(selectedId: programs.EntityId, allowedEntityId: adminEntityId);
            return View(programs);
        }

        // GET: Programs/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null) return NotFound();

            var adminEntityId = GetAdminScopedEntityId();
            var programs = await _context.Programs
                .Include(p => p.Entity)
                .FirstOrDefaultAsync(m => m.ProgramId == id);

            if (programs == null) return NotFound();

            if (adminEntityId.HasValue && programs.EntityId != adminEntityId.Value)
            {
                return Forbid();
            }

            return View(programs);
        }

        // POST: Programs/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var adminEntityId = GetAdminScopedEntityId();
            var programs = await _context.Programs.FindAsync(id);
            if (programs != null)
            {
                if (adminEntityId.HasValue && programs.EntityId != adminEntityId.Value)
                {
                    return Forbid();
                }
                _context.Programs.Remove(programs);
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        private bool ProgramsExists(int id)
        {
            return _context.Programs.Any(e => e.ProgramId == id);
        }

        private void PopulateEntityDropDown(int? selectedId = null, int? allowedEntityId = null)
        {
            // NOTE: Using a plain ASCII hyphen for the display to avoid any Unicode surprises
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
                    Display = e.EntityCode + " - " + e.EntityName
                })
                .ToList();

            ViewData["EntityId"] = new SelectList(ents, "EntityId", "Display", selectedId);
        }
    }
}
