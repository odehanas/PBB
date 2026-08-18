using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using GovBudget.Models;

namespace GovBudget.Controllers
{
    [Authorize(Policy = "AdminOnly")]
    public class ItemsController : Controller
    {
        private readonly GovBudgetContext _context;

        // Session key holding the parsed rows awaiting overwrite confirmation.
        private const string PendingImportKey = "PendingItemImport";

        public ItemsController(GovBudgetContext context)
        {
            _context = context;
        }

        private bool IsGlobalAdmin()
        {
            var entityClaim = User.Claims.FirstOrDefault(c => c.Type == "EntityId")?.Value;
            var hasEntityScope = int.TryParse(entityClaim, out var entityId) && entityId > 0;
            return User.IsInRole("SYSADMIN") || (User.IsInRole("ADMIN") && !hasEntityScope);
        }

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            if (!IsGlobalAdmin())
            {
                context.Result = Forbid();
                return;
            }

            base.OnActionExecuting(context);
        }

        // GET: Items?q=...
        // Free-text filter over item code / name and the GL code or name behind the item.
        public async Task<IActionResult> Index(string? q)
        {
            var items = _context.Items
                .Include(i => i.GLAccount)
                .AsQueryable();

            q = q?.Trim();
            if (!string.IsNullOrWhiteSpace(q))
            {
                items = items.Where(i => EF.Functions.Like(i.ItemCode, $"%{q}%")
                                      || EF.Functions.Like(i.ItemName, $"%{q}%")
                                      || EF.Functions.Like(i.GLAccount.GLCode, $"%{q}%")
                                      || EF.Functions.Like(i.GLAccount.GLName, $"%{q}%"));
            }

            ViewBag.Query = q;
            ViewBag.TotalCount = await _context.Items.CountAsync();
            // Counted over ALL items, not the filtered view, because "Activate All" acts globally.
            ViewBag.InactiveCount = await _context.Items.CountAsync(i => !i.IsActive);
            return View(await items.OrderBy(i => i.ItemCode).ToListAsync());
        }

        [HttpGet]
        public IActionResult Template()
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Items");

            ws.Cell(1, 1).Value = "ItemCode";
            ws.Cell(1, 2).Value = "ItemName";
            ws.Cell(1, 3).Value = "GLCode";
            ws.Cell(1, 4).Value = "IsActive";

            ws.Cell(2, 1).Value = "ITEM001";
            ws.Cell(2, 2).Value = "Sample Item";
            ws.Cell(2, 3).Value = "6000";
            ws.Cell(2, 4).Value = "TRUE";

            ws.Range(1, 1, 1, 4).Style.Font.Bold = true;
            ws.Columns(1, 4).AdjustToContents();

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            var bytes = stream.ToArray();
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Items_Template.xlsx");
        }

        // GET: Items/Export
        // Exports the full item list. The first four columns match the upload template, so an
        // exported file can be edited and uploaded straight back; GLName/GLType are informational.
        [HttpGet]
        public async Task<IActionResult> Export(string? q)
        {
            var query = _context.Items.AsNoTracking()
                .Include(i => i.GLAccount)
                .AsQueryable();

            q = q?.Trim();
            if (!string.IsNullOrWhiteSpace(q))
            {
                query = query.Where(i => EF.Functions.Like(i.ItemCode, $"%{q}%")
                                      || EF.Functions.Like(i.ItemName, $"%{q}%")
                                      || EF.Functions.Like(i.GLAccount.GLCode, $"%{q}%")
                                      || EF.Functions.Like(i.GLAccount.GLName, $"%{q}%"));
            }

            var items = await query.OrderBy(i => i.ItemCode).ToListAsync();

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Items");

            var headers = new[] { "ItemCode", "ItemName", "GLCode", "IsActive", "GLName", "GLType" };
            for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
            var head = ws.Range(1, 1, 1, headers.Length).Style;
            head.Font.Bold = true;
            head.Fill.BackgroundColor = XLColor.FromHtml(GovBudget.Utils.BrandColors.HeaderHex);
            head.Font.FontColor = XLColor.White;

            int r = 2;
            foreach (var i in items)
            {
                ws.Cell(r, 1).Value = i.ItemCode;
                ws.Cell(r, 2).Value = i.ItemName;
                ws.Cell(r, 3).Value = i.GLAccount?.GLCode ?? "";
                ws.Cell(r, 4).Value = i.IsActive ? "TRUE" : "FALSE";
                ws.Cell(r, 5).Value = i.GLAccount?.GLName ?? "";
                ws.Cell(r, 6).Value = i.GLAccount?.GLType ?? "";
                r++;
            }

            ws.SheetView.FreezeRows(1);
            if (items.Count > 0) ws.Range(1, 1, r - 1, headers.Length).SetAutoFilter();
            ws.Columns(1, headers.Length).AdjustToContents();

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return File(stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"Items_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
        }

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
                if (!string.IsNullOrWhiteSpace(name))
                {
                    headerMap[name] = cell.Address.ColumnNumber;
                }
            }

            if (!headerMap.TryGetValue("ItemCode", out var itemCodeCol) ||
                !headerMap.TryGetValue("ItemName", out var itemNameCol) ||
                !headerMap.TryGetValue("GLCode", out var glCodeCol))
            {
                TempData["Error"] = "Template columns must include: ItemCode, ItemName, GLCode. Optional: IsActive.";
                return RedirectToAction(nameof(Index));
            }

            headerMap.TryGetValue("IsActive", out var isActiveCol);

            var gls = await _context.GLAccounts.AsNoTracking().ToListAsync();
            var glByCode = gls
                .Where(g => !string.IsNullOrWhiteSpace(g.GLCode))
                .ToDictionary(g => g.GLCode.Trim(), g => g, StringComparer.OrdinalIgnoreCase);

            var errors = new List<string>();

            // Parse + validate each row into a candidate. Deduplicate by ItemCode (last row wins)
            // so a file that repeats a code does not create duplicate inserts.
            var candidates = new Dictionary<string, ItemImportRow>(StringComparer.OrdinalIgnoreCase);

            var firstDataRowNumber = headerRow.RowNumber() + 1;
            var lastRowNumber = ws.LastRowUsed()?.RowNumber() ?? firstDataRowNumber - 1;

            for (var r = firstDataRowNumber; r <= lastRowNumber; r++)
            {
                var row = ws.Row(r);
                var itemCode = row.Cell(itemCodeCol).GetString().Trim();
                var itemName = row.Cell(itemNameCol).GetString().Trim();
                var glCode = row.Cell(glCodeCol).GetString().Trim();

                var isActiveRaw = isActiveCol > 0 ? row.Cell(isActiveCol).GetString().Trim() : "";
                var isActive = ParseBoolOrDefault(isActiveRaw, true);

                if (string.IsNullOrWhiteSpace(itemCode) && string.IsNullOrWhiteSpace(itemName) && string.IsNullOrWhiteSpace(glCode))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(itemCode))
                {
                    errors.Add($"Row {r}: ItemCode is required.");
                    if (errors.Count >= 20) break;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(itemName))
                {
                    errors.Add($"Row {r}: ItemName is required.");
                    if (errors.Count >= 20) break;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(glCode))
                {
                    errors.Add($"Row {r}: GLCode is required.");
                    if (errors.Count >= 20) break;
                    continue;
                }
                if (!glByCode.TryGetValue(glCode, out var gl))
                {
                    errors.Add($"Row {r}: GLCode '{glCode}' was not found in GL Accounts.");
                    if (errors.Count >= 20) break;
                    continue;
                }

                candidates[itemCode] = new ItemImportRow
                {
                    ItemCode = itemCode,
                    ItemName = itemName,
                    GLAccountId = gl.GLAccountId,
                    GLCode = gl.GLCode,
                    IsActive = isActive
                };
            }

            if (errors.Count > 0)
            {
                TempData["Error"] = "Upload failed:\n" + string.Join("\n", errors);
                return RedirectToAction(nameof(Index));
            }

            var rows = candidates.Values.ToList();
            if (rows.Count == 0)
            {
                TempData["Error"] = "No item rows were found in the uploaded file.";
                return RedirectToAction(nameof(Index));
            }

            var existingByCode = await BuildExistingItemMapAsync();
            var conflicts = rows.Where(x => existingByCode.ContainsKey(x.ItemCode.Trim())).ToList();
            var newRows = rows.Where(x => !existingByCode.ContainsKey(x.ItemCode.Trim())).ToList();

            // If any imported ItemCode already exists, DO NOT overwrite yet — ask the admin to
            // confirm. New codes and untouched existing items are handled once confirmed.
            if (conflicts.Count > 0)
            {
                HttpContext.Session.SetString(PendingImportKey, JsonSerializer.Serialize(rows));

                var vm = new ItemImportConfirmVm
                {
                    NewCount = newRows.Count,
                    Conflicts = conflicts
                        .Select(c =>
                        {
                            var ex = existingByCode[c.ItemCode.Trim()];
                            return new ItemImportConflictVm
                            {
                                ItemCode = c.ItemCode,
                                ExistingName = ex.ItemName,
                                NewName = c.ItemName,
                                NewGLCode = c.GLCode
                            };
                        })
                        .OrderBy(c => c.ItemCode, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };
                return View("ConfirmUpload", vm);
            }

            // No conflicts: only new items are added; existing items stay untouched.
            var (created, updated) = await ApplyItemImportAsync(rows);
            TempData["Success"] = $"Upload complete. Created: {created}. Updated: {updated}.";
            return RedirectToAction(nameof(Index));
        }

        // Second step: the admin confirmed overwriting the existing item codes.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmUpload()
        {
            var json = HttpContext.Session.GetString(PendingImportKey);
            HttpContext.Session.Remove(PendingImportKey);
            if (string.IsNullOrEmpty(json))
            {
                TempData["Error"] = "Your import session expired. Please upload the file again.";
                return RedirectToAction(nameof(Index));
            }

            var rows = JsonSerializer.Deserialize<List<ItemImportRow>>(json) ?? new List<ItemImportRow>();
            if (rows.Count == 0)
            {
                TempData["Error"] = "There was nothing to import.";
                return RedirectToAction(nameof(Index));
            }

            var (created, updated) = await ApplyItemImportAsync(rows);
            TempData["Success"] = $"Import confirmed. Created: {created}. Overwritten: {updated}.";
            return RedirectToAction(nameof(Index));
        }

        // The admin chose not to overwrite: discard the pending import, change nothing.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult CancelUpload()
        {
            HttpContext.Session.Remove(PendingImportKey);
            TempData["Success"] = "Import cancelled. No items were changed.";
            return RedirectToAction(nameof(Index));
        }

        // Builds a case-insensitive ItemCode -> entity map from the database, tolerating any
        // duplicate codes already stored (keeps the first).
        private async Task<Dictionary<string, Items>> BuildExistingItemMapAsync()
        {
            var existingItems = await _context.Items.ToListAsync();
            return existingItems
                .Where(i => !string.IsNullOrWhiteSpace(i.ItemCode))
                .GroupBy(i => i.ItemCode.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

        // Applies parsed rows: overwrites items whose ItemCode already exists, inserts the rest.
        // Existing items NOT present in the file are never modified.
        private async Task<(int created, int updated)> ApplyItemImportAsync(List<ItemImportRow> rows)
        {
            var existingByCode = await BuildExistingItemMapAsync();
            var created = 0;
            var updated = 0;

            foreach (var x in rows)
            {
                if (existingByCode.TryGetValue(x.ItemCode.Trim(), out var existing))
                {
                    existing.ItemName = x.ItemName;
                    existing.GLAccountId = x.GLAccountId;
                    existing.IsActive = x.IsActive;
                    updated++;
                }
                else
                {
                    _context.Items.Add(new Items
                    {
                        ItemCode = x.ItemCode,
                        ItemName = x.ItemName,
                        GLAccountId = x.GLAccountId,
                        IsActive = x.IsActive
                    });
                    created++;
                }
            }

            await _context.SaveChangesAsync();
            return (created, updated);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ActivateAll()
        {
            var affected = await _context.Items
                .Where(i => !i.IsActive)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.IsActive, true));

            TempData["Success"] = $"Activated {affected} item(s). They will now appear in Budget Entry (under the tab matching their GL account type).";
            return RedirectToAction(nameof(Index));
        }

        // GET: Items/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null) return NotFound();

            var item = await _context.Items
                .Include(i => i.GLAccount)
                .FirstOrDefaultAsync(m => m.ItemId == id);
            if (item == null) return NotFound();

            return View(item);
        }

        // GET: Items/Create
        public IActionResult Create()
        {
            PopulateGLDropDown();
            return View();
        }

        // POST: Items/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("ItemId,ItemCode,ItemName,GLAccountId,IsActive")] Items items)
        {
            // The GLAccount navigation is not posted; exclude it so the non-nullable
            // reference doesn't produce a hidden "required" validation error.
            ModelState.Remove(nameof(Items.GLAccount));

            if (items.GLAccountId <= 0)
            {
                ModelState.AddModelError(nameof(Items.GLAccountId), "Please select a GL Account.");
            }

            var code = (items.ItemCode ?? "").Trim();
            if (!string.IsNullOrEmpty(code) &&
                await _context.Items.AnyAsync(i => i.ItemCode == code))
            {
                ModelState.AddModelError(nameof(Items.ItemCode), $"An item with code '{code}' already exists.");
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Add(items);
                    await _context.SaveChangesAsync();
                    TempData["Success"] = $"Item '{items.ItemCode} - {items.ItemName}' created.";
                    return RedirectToAction(nameof(Index));
                }
                catch (DbUpdateException ex)
                {
                    ModelState.AddModelError(string.Empty,
                        "Could not save the item: " + (ex.InnerException?.Message ?? ex.Message));
                }
            }
            PopulateGLDropDown(items.GLAccountId);
            return View(items);
        }

        // GET: Items/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();

            var items = await _context.Items.FindAsync(id);
            if (items == null) return NotFound();

            PopulateGLDropDown(items.GLAccountId);
            return View(items);
        }

        // POST: Items/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("ItemId,ItemCode,ItemName,GLAccountId,IsActive")] Items items)
        {
            if (id != items.ItemId) return NotFound();

            ModelState.Remove(nameof(Items.GLAccount));

            if (items.GLAccountId <= 0)
            {
                ModelState.AddModelError(nameof(Items.GLAccountId), "Please select a GL Account.");
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(items);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!ItemsExists(items.ItemId)) return NotFound();
                    else throw;
                }
                catch (DbUpdateException ex)
                {
                    ModelState.AddModelError(string.Empty,
                        "Could not save the item: " + (ex.InnerException?.Message ?? ex.Message));
                    PopulateGLDropDown(items.GLAccountId);
                    return View(items);
                }
                TempData["Success"] = $"Item '{items.ItemCode} - {items.ItemName}' updated.";
                return RedirectToAction(nameof(Index));
            }
            PopulateGLDropDown(items.GLAccountId);
            return View(items);
        }

        // GET: Items/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null) return NotFound();

            var items = await _context.Items
                .Include(i => i.GLAccount)
                .FirstOrDefaultAsync(m => m.ItemId == id);
            if (items == null) return NotFound();

            return View(items);
        }

        // POST: Items/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var items = await _context.Items.FindAsync(id);
            if (items != null)
            {
                _context.Items.Remove(items);
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        // POST: Items/QuickCreate  (AJAX, admin only) — used by the Budget Entry screen to add
        // an item inline without leaving the page. Returns JSON.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> QuickCreate(string itemCode, string itemName, int glAccountId, string? category)
        {
            itemCode = (itemCode ?? "").Trim();
            itemName = (itemName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(itemCode) || string.IsNullOrWhiteSpace(itemName))
                return Json(new { ok = false, error = "Item code and name are required." });
            if (itemCode.Length > 30)
                return Json(new { ok = false, error = "Item code must be 30 characters or fewer." });
            if (itemName.Length > 200)
                return Json(new { ok = false, error = "Item name must be 200 characters or fewer." });

            var gl = await _context.GLAccounts.FirstOrDefaultAsync(g => g.GLAccountId == glAccountId);
            if (gl == null)
                return Json(new { ok = false, error = "Please choose a valid GL account." });

            if (!string.IsNullOrWhiteSpace(category) &&
                !string.Equals(gl.GLType, category, StringComparison.OrdinalIgnoreCase))
                return Json(new { ok = false, error = $"The selected GL account is '{gl.GLType}', not {category}. Pick a {category} GL account." });

            if (await _context.Items.AnyAsync(i => i.ItemCode == itemCode))
                return Json(new { ok = false, error = $"An item with code '{itemCode}' already exists." });

            var item = new Items
            {
                ItemCode = itemCode,
                ItemName = itemName,
                GLAccountId = glAccountId,
                IsActive = true
            };

            try
            {
                _context.Items.Add(item);
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                return Json(new { ok = false, error = "Could not save item: " + (ex.InnerException?.Message ?? ex.Message) });
            }

            return Json(new
            {
                ok = true,
                itemId = item.ItemId,
                display = $"{item.ItemCode} - {item.ItemName}",
                glType = gl.GLType
            });
        }

        private bool ItemsExists(int id) => _context.Items.Any(e => e.ItemId == id);

        private void PopulateGLDropDown(int? selectedId = null)
        {
            var gls = _context.GLAccounts
                .OrderBy(g => g.GLCode)
                .Select(g => new
                {
                    g.GLAccountId,
                    Display = g.GLCode + " — " + g.GLName
                })
                .ToList();

            ViewData["GLAccountId"] = new SelectList(gls, "GLAccountId", "Display", selectedId);
        }

        private static bool ParseBoolOrDefault(string? value, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;
            var v = value.Trim();
            if (bool.TryParse(v, out var b)) return b;
            if (string.Equals(v, "1", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "0", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "no", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(v, "y", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "n", StringComparison.OrdinalIgnoreCase)) return false;
            return defaultValue;
        }
    }

    // Parsed + validated item row held in session between upload and overwrite confirmation.
    public class ItemImportRow
    {
        public string ItemCode { get; set; } = "";
        public string ItemName { get; set; } = "";
        public int GLAccountId { get; set; }
        public string GLCode { get; set; } = "";
        public bool IsActive { get; set; } = true;
    }

    public class ItemImportConflictVm
    {
        public string ItemCode { get; set; } = "";
        public string ExistingName { get; set; } = "";
        public string NewName { get; set; } = "";
        public string NewGLCode { get; set; } = "";
    }

    public class ItemImportConfirmVm
    {
        public int NewCount { get; set; }
        public List<ItemImportConflictVm> Conflicts { get; set; } = new();
    }
}
