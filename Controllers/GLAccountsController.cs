using System.Linq;
using System.Collections.Generic;
using System.IO;
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
    public class GLAccountsController : Controller
    {
        private readonly GovBudgetContext _context;

        public GLAccountsController(GovBudgetContext context)
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

        // GET: GLAccounts
        public async Task<IActionResult> Index()
        {
            return View(await _context.GLAccounts.ToListAsync());
        }

        [HttpGet]
        public IActionResult Template()
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("GLAccounts");

            ws.Cell(1, 1).Value = "GLCode";
            ws.Cell(1, 2).Value = "GLName";
            ws.Cell(1, 3).Value = "GLType";

            ws.Cell(2, 1).Value = "4000";
            ws.Cell(2, 2).Value = "Sample Revenue";
            ws.Cell(2, 3).Value = "REVENUE";

            ws.Cell(3, 1).Value = "6000";
            ws.Cell(3, 2).Value = "Sample Operating Expense";
            ws.Cell(3, 3).Value = "OPEX";

            ws.Cell(4, 1).Value = "7000";
            ws.Cell(4, 2).Value = "Sample Capital Expense";
            ws.Cell(4, 3).Value = "CAPEX";

            ws.Cell(6, 1).Value =
                "Note: GLType must be exactly one of: REVENUE, OPEX, CAPEX, HR. " +
                "These match the Budget Entry tabs; any other value (e.g. 'EXPENSE') will hide the linked items from the dropdowns.";
            ws.Cell(6, 1).Style.Font.Italic = true;
            ws.Cell(6, 1).Style.Font.FontColor = XLColor.Gray;

            ws.Range(1, 1, 1, 3).Style.Font.Bold = true;
            ws.Columns(1, 3).AdjustToContents();

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            var bytes = stream.ToArray();
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "GLAccounts_Template.xlsx");
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

            if (!Path.GetExtension(file.FileName).Equals(".xlsx", System.StringComparison.OrdinalIgnoreCase))
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

            var headerMap = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var cell in headerRow.CellsUsed())
            {
                var name = (cell.GetString() ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    headerMap[name] = cell.Address.ColumnNumber;
                }
            }

            if (!headerMap.TryGetValue("GLCode", out var glCodeCol) ||
                !headerMap.TryGetValue("GLName", out var glNameCol) ||
                !headerMap.TryGetValue("GLType", out var glTypeCol))
            {
                TempData["Error"] = "Template columns must include: GLCode, GLName, GLType.";
                return RedirectToAction(nameof(Index));
            }

            var existing = await _context.GLAccounts.ToListAsync();
            var byCode = existing
                .Where(x => !string.IsNullOrWhiteSpace(x.GLCode))
                .ToDictionary(x => x.GLCode.Trim(), x => x, System.StringComparer.OrdinalIgnoreCase);

            var created = 0;
            var updated = 0;
            var errors = new List<string>();

            var firstDataRowNumber = headerRow.RowNumber() + 1;
            var lastRowNumber = ws.LastRowUsed()?.RowNumber() ?? firstDataRowNumber - 1;

            for (var r = firstDataRowNumber; r <= lastRowNumber; r++)
            {
                var row = ws.Row(r);
                var glCode = row.Cell(glCodeCol).GetString().Trim();
                var glName = row.Cell(glNameCol).GetString().Trim();
                var glType = row.Cell(glTypeCol).GetString().Trim();

                if (string.IsNullOrWhiteSpace(glCode) && string.IsNullOrWhiteSpace(glName) && string.IsNullOrWhiteSpace(glType))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(glCode))
                {
                    errors.Add($"Row {r}: GLCode is required.");
                    if (errors.Count >= 20) break;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(glName))
                {
                    errors.Add($"Row {r}: GLName is required.");
                    if (errors.Count >= 20) break;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(glType))
                {
                    errors.Add($"Row {r}: GLType is required.");
                    if (errors.Count >= 20) break;
                    continue;
                }

                var normalizedType = NormalizeGlType(glType);
                if (normalizedType == null)
                {
                    errors.Add($"Row {r}: GLType '{glType}' is invalid. Allowed values: REVENUE, OPEX, CAPEX, HR.");
                    if (errors.Count >= 20) break;
                    continue;
                }

                if (byCode.TryGetValue(glCode, out var existingGl))
                {
                    existingGl.GLName = glName;
                    existingGl.GLType = normalizedType;
                    updated++;
                }
                else
                {
                    _context.GLAccounts.Add(new GLAccounts
                    {
                        GLCode = glCode,
                        GLName = glName,
                        GLType = normalizedType
                    });
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

        // Maps common variants to the exact GLType values used by Budget Entry tabs.
        private static string? NormalizeGlType(string? raw)
        {
            var t = (raw ?? "").Trim().ToUpperInvariant();
            return t switch
            {
                "REVENUE" => "REVENUE",
                "REV" => "REVENUE",
                "INCOME" => "REVENUE",
                "OPEX" => "OPEX",
                "OPERATING" => "OPEX",
                "OPERATIONAL" => "OPEX",
                "EXPENSE" => "OPEX",
                "CAPEX" => "CAPEX",
                "CAPITAL" => "CAPEX",
                "HR" => "HR",
                _ => null
            };
        }

        // GET: GLAccounts/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var gLAccounts = await _context.GLAccounts
                .FirstOrDefaultAsync(m => m.GLAccountId == id);
            if (gLAccounts == null)
            {
                return NotFound();
            }

            return View(gLAccounts);
        }

        // GET: GLAccounts/Create
        public IActionResult Create()
        {
            return View();
        }

        // POST: GLAccounts/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to.
        // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("GLAccountId,GLCode,GLName,GLType")] GLAccounts gLAccounts)
        {
            if (ModelState.IsValid)
            {
                _context.Add(gLAccounts);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            return View(gLAccounts);
        }

        // GET: GLAccounts/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var gLAccounts = await _context.GLAccounts.FindAsync(id);
            if (gLAccounts == null)
            {
                return NotFound();
            }
            return View(gLAccounts);
        }

        // POST: GLAccounts/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to.
        // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("GLAccountId,GLCode,GLName,GLType")] GLAccounts gLAccounts)
        {
            if (id != gLAccounts.GLAccountId)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(gLAccounts);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!GLAccountsExists(gLAccounts.GLAccountId))
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
            return View(gLAccounts);
        }

        // GET: GLAccounts/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var gLAccounts = await _context.GLAccounts
                .FirstOrDefaultAsync(m => m.GLAccountId == id);
            if (gLAccounts == null)
            {
                return NotFound();
            }

            return View(gLAccounts);
        }

        // POST: GLAccounts/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var gLAccounts = await _context.GLAccounts.FindAsync(id);
            if (gLAccounts != null)
            {
                _context.GLAccounts.Remove(gLAccounts);
            }

            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool GLAccountsExists(int id)
        {
            return _context.GLAccounts.Any(e => e.GLAccountId == id);
        }
    }
}
