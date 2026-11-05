using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using GovBudget.Models;
using GovBudget.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Controllers
{
    // Access is governed by the MIDYEAR form (see FormPermissionFilter): view rights open the
    // page and download the template; Add/Edit rights are required for Save and Upload.
    [Authorize]
    public class MidYearController : Controller
    {
        private readonly GovBudgetContext _db;
        private readonly GovBudget.Services.ImportHistoryService _history;

        public MidYearController(GovBudgetContext db, GovBudget.Services.ImportHistoryService history)
        {
            _db = db;
            _history = history;
        }

        [HttpGet]
        public async Task<IActionResult> Index(int? year = null)
        {
            var selectedYear = year ?? HttpContext.Session.GetInt("ctxYear") ?? DateTime.Now.Year;
            var entityId = HttpContext.Session.GetInt("ctxEntityId");
            if (!entityId.HasValue)
            {
                var returnUrl = Url.Action(nameof(Index), "MidYear", new { year = selectedYear });
                return RedirectToAction("Select", "Context", new { returnUrl });
            }

            var entity = await _db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.EntityId == entityId.Value);
            var entityLabel = entity == null ? $"Entity {entityId.Value}" : $"{entity.EntityCode} - {entity.EntityName}";

            // Mid-year actuals are stored under the year they belong to (current year),
            // and are reviewed while preparing the NEXT budget year — same rule as
            // ReportsController (actualYear = year - 1).
            var actualYear = selectedYear - 1;

            var rows = await _db.MidYearGlActualForecasts.AsNoTracking()
                .Where(x => x.BudgetYear == actualYear && x.EntityId == entityId.Value)
                .OrderBy(x => x.GLType)
                .ThenBy(x => x.GLCode)
                .Select(x => new MidYearRowVm
                {
                    MidYearId = x.MidYearId,
                    GLCode = x.GLCode,
                    GLType = x.GLType,
                    ActualH1Amount = x.ActualH1Amount,
                    ForecastH2Amount = x.ForecastH2Amount
                })
                .ToListAsync();

            // If nothing is loaded for actualYear but the context year itself has actuals,
            // the user most likely picked the actual year instead of the budget year.
            var contextYearHasActuals = rows.Count == 0 && await _db.MidYearGlActualForecasts.AsNoTracking()
                .AnyAsync(x => x.BudgetYear == selectedYear && x.EntityId == entityId.Value);

            var vm = new MidYearIndexVm
            {
                Year = selectedYear,
                ActualYear = actualYear,
                EntityLabel = entityLabel,
                ContextYearHasActuals = contextYearHasActuals,
                Rows = rows
            };

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(MidYearSaveVm model)
        {
            var selectedYear = HttpContext.Session.GetInt("ctxYear") ?? model.Year;
            var entityId = HttpContext.Session.GetInt("ctxEntityId");
            if (!entityId.HasValue)
            {
                var returnUrl = Url.Action(nameof(Index), "MidYear", new { year = selectedYear });
                return RedirectToAction("Select", "Context", new { returnUrl });
            }

            var userName = User.Identity?.Name ?? "Unknown";
            var ids = (model.Rows ?? new List<MidYearSaveRowVm>()).Select(r => r.MidYearId).Distinct().ToList();
            if (ids.Count == 0)
            {
                TempData["Error"] = "No rows submitted.";
                return RedirectToAction(nameof(Index), new { year = selectedYear });
            }

            var actualYear = selectedYear - 1;
            var dbRows = await _db.MidYearGlActualForecasts
                .Where(x => x.BudgetYear == actualYear && x.EntityId == entityId.Value && ids.Contains(x.MidYearId))
                .ToListAsync();

            var byId = dbRows.ToDictionary(x => x.MidYearId, x => x);
            var updated = 0;

            foreach (var row in model.Rows ?? new List<MidYearSaveRowVm>())
            {
                if (!byId.TryGetValue(row.MidYearId, out var ex))
                {
                    continue;
                }

                // Negative forecasts are valid (deductions, contra / allowance GLs) and are accepted
                // here exactly as they are by the Excel uploads.
                ex.ForecastH2Amount = row.ForecastH2Amount;
                ex.ForecastUpdatedAt = DateTime.UtcNow;
                ex.ForecastUpdatedBy = userName;
                updated++;
            }

            await _db.SaveChangesAsync();
            TempData["Success"] = $"Saved {updated} forecast values.";

            return RedirectToAction(nameof(Index), new { year = selectedYear });
        }

        // Template for the current context (entity + actual year). Pre-filled with any rows
        // already loaded so users can just complete the H2 forecast and upload it back.
        [HttpGet]
        public async Task<IActionResult> Template()
        {
            var selectedYear = HttpContext.Session.GetInt("ctxYear") ?? DateTime.Now.Year;
            var entityId = HttpContext.Session.GetInt("ctxEntityId");
            if (!entityId.HasValue)
            {
                var returnUrl = Url.Action(nameof(Index), "MidYear");
                return RedirectToAction("Select", "Context", new { returnUrl });
            }

            var actualYear = selectedYear - 1;
            var entity = await _db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.EntityId == entityId.Value);
            var entityCode = entity?.EntityCode ?? "";

            var existing = await _db.MidYearGlActualForecasts.AsNoTracking()
                .Where(x => x.BudgetYear == actualYear && x.EntityId == entityId.Value)
                .OrderBy(x => x.GLType).ThenBy(x => x.GLCode)
                .ToListAsync();

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("MidYearActuals");
            var headers = new[] { "BudgetYear", "EntityCode", "GLCode", "GLType", "ActualH1Amount", "ForecastH2Amount" };
            for (var c = 0; c < headers.Length; c++)
            {
                ws.Cell(1, c + 1).Value = headers[c];
            }
            ws.Range(1, 1, 1, headers.Length).Style.Font.Bold = true;

            var r = 2;
            foreach (var x in existing)
            {
                ws.Cell(r, 1).Value = x.BudgetYear;
                ws.Cell(r, 2).Value = entityCode;
                ws.Cell(r, 3).Value = x.GLCode;
                ws.Cell(r, 4).Value = x.GLType;
                ws.Cell(r, 5).Value = x.ActualH1Amount;
                if (x.ForecastH2Amount.HasValue)
                {
                    ws.Cell(r, 6).Value = x.ForecastH2Amount.Value;
                }
                r++;
            }
            ws.Range(2, 5, Math.Max(r, 2), 6).Style.NumberFormat.Format = "#,##0.00";
            ws.Columns(1, headers.Length).AdjustToContents();

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"MidYearActuals_{entityCode}_{actualYear}.xlsx");
        }

        // Upload for the current context only: every row must be for the context entity and
        // the actual year (context year - 1). Blank ForecastH2Amount keeps the existing value.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(10_000_000)]
        public async Task<IActionResult> Upload(IFormFile? file)
        {
            var selectedYear = HttpContext.Session.GetInt("ctxYear") ?? DateTime.Now.Year;
            var entityId = HttpContext.Session.GetInt("ctxEntityId");
            if (!entityId.HasValue)
            {
                var returnUrl = Url.Action(nameof(Index), "MidYear");
                return RedirectToAction("Select", "Context", new { returnUrl });
            }

            // Defence in depth: an entity-scoped user may only load their own entity.
            var entityClaim = User.Claims.FirstOrDefault(c => c.Type == "EntityId")?.Value;
            if (int.TryParse(entityClaim, out var allowedEntityId) && allowedEntityId > 0 && allowedEntityId != entityId.Value)
            {
                return Forbid();
            }

            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Please choose an Excel file to upload.";
                return RedirectToAction(nameof(Index), new { year = selectedYear });
            }

            if (!Path.GetExtension(file.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Only .xlsx files are supported.";
                return RedirectToAction(nameof(Index), new { year = selectedYear });
            }

            var actualYear = selectedYear - 1;
            var entity = await _db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.EntityId == entityId.Value);
            if (entity == null)
            {
                TempData["Error"] = "The selected entity no longer exists.";
                return RedirectToAction(nameof(Index), new { year = selectedYear });
            }

            var glSet = await _db.GLAccounts.AsNoTracking()
                .Select(x => x.GLCode)
                .ToHashSetAsync(StringComparer.OrdinalIgnoreCase);

            var userName = User.Identity?.Name ?? "Unknown";
            var errors = new List<string>();
            var parsed = new List<(int Row, string GLCode, string GLType, decimal Actual, decimal? Forecast)>();

            using (var stream = file.OpenReadStream())
            using (var wb = new XLWorkbook(stream))
            {
                var ws = wb.Worksheets.FirstOrDefault();
                var headerRow = ws?.FirstRowUsed();
                if (ws == null || headerRow == null)
                {
                    TempData["Error"] = "The uploaded file is empty.";
                    return RedirectToAction(nameof(Index), new { year = selectedYear });
                }

                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var cell in headerRow.CellsUsed())
                {
                    var name = (cell.GetString() ?? "").Trim();
                    if (name.Length > 0 && !map.ContainsKey(name)) map[name] = cell.Address.ColumnNumber;
                }

                int Col(params string[] names) => names.Select(n => map.TryGetValue(n, out var c) ? c : 0).FirstOrDefault(c => c > 0);
                var yearCol = Col("BudgetYear", "Year");
                var entityCol = Col("EntityCode", "Entity");
                var glCol = Col("GLCode", "GL", "GLAccountCode");
                var typeCol = Col("GLType", "Type");
                var actualCol = Col("ActualH1Amount", "H1Amount", "Actual");
                var forecastCol = Col("ForecastH2Amount", "H2Amount", "ForecastH2");

                if (yearCol == 0 || entityCol == 0 || glCol == 0 || typeCol == 0 || actualCol == 0)
                {
                    TempData["Error"] = "Missing required columns. Required: BudgetYear, EntityCode, GLCode, GLType, ActualH1Amount (ForecastH2Amount optional).";
                    return RedirectToAction(nameof(Index), new { year = selectedYear });
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var lastRow = ws.LastRowUsed()?.RowNumber() ?? headerRow.RowNumber();
                for (var r = headerRow.RowNumber() + 1; r <= lastRow; r++)
                {
                    var row = ws.Row(r);
                    var yearRaw = row.Cell(yearCol).GetValue<string>().Trim();
                    var entityCode = row.Cell(entityCol).GetString().Trim();
                    var glCode = row.Cell(glCol).GetValue<string>().Trim();
                    var typeRaw = row.Cell(typeCol).GetString().Trim();
                    var actualRaw = row.Cell(actualCol).GetValue<string>().Trim();
                    var forecastRaw = forecastCol > 0 ? row.Cell(forecastCol).GetValue<string>().Trim() : "";

                    if (yearRaw == "" && entityCode == "" && glCode == "" && typeRaw == "" && actualRaw == "" && forecastRaw == "")
                    {
                        continue;
                    }

                    if (!int.TryParse(yearRaw, out var y) || y != actualYear)
                    {
                        errors.Add($"Row {r}: BudgetYear must be {actualYear} (the actual year for budget {selectedYear}), found '{yearRaw}'.");
                        continue;
                    }
                    if (!string.Equals(entityCode, entity.EntityCode, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"Row {r}: EntityCode must be {entity.EntityCode}, found '{entityCode}'.");
                        continue;
                    }
                    if (glCode == "" || !glSet.Contains(glCode))
                    {
                        errors.Add($"Row {r}: Unknown GLCode '{glCode}'.");
                        continue;
                    }
                    if (!seen.Add(glCode))
                    {
                        errors.Add($"Row {r}: GLCode '{glCode}' appears more than once in the file.");
                        continue;
                    }
                    if (!TryNormalizeGlType(typeRaw, out var glType))
                    {
                        errors.Add($"Row {r}: Invalid GLType '{typeRaw}'. Allowed: Revenue, HR, Opex, Capex.");
                        continue;
                    }
                    if (!TryParseDecimal(actualRaw, out var actual))
                    {
                        errors.Add($"Row {r}: Invalid ActualH1Amount '{actualRaw}'.");
                        continue;
                    }

                    decimal? forecast = null;
                    if (forecastRaw != "")
                    {
                        if (!TryParseDecimal(forecastRaw, out var f))
                        {
                            errors.Add($"Row {r}: Invalid ForecastH2Amount '{forecastRaw}'.");
                            continue;
                        }
                        forecast = f;
                    }

                    parsed.Add((r, glCode, glType, actual, forecast));
                }
            }

            if (errors.Count > 0)
            {
                TempData["Error"] = "Upload failed - nothing was saved:\n" + string.Join("\n", errors.Take(50));
                return RedirectToAction(nameof(Index), new { year = selectedYear });
            }
            if (parsed.Count == 0)
            {
                TempData["Error"] = "No valid rows found in the uploaded file.";
                return RedirectToAction(nameof(Index), new { year = selectedYear });
            }

            // Nothing is saved yet: keep the parsed rows in the session and show what would change.
            var pending = new PendingMidYearUpload
            {
                BudgetYear = selectedYear,
                ActualYear = actualYear,
                EntityId = entity.EntityId,
                EntityCode = entity.EntityCode,
                FileName = file.FileName,
                Rows = parsed.Select(p => new PendingMidYearRow { GLCode = p.GLCode, GLType = p.GLType, Actual = p.Actual, Forecast = p.Forecast }).ToList()
            };
            HttpContext.Session.SetString(PendingUploadKey, System.Text.Json.JsonSerializer.Serialize(pending));
            return View("UploadPreview", await BuildPreviewAsync(pending));
        }

        private const string PendingUploadKey = "PendingMidYearUpload";

        private async Task<UploadPreviewVm> BuildPreviewAsync(PendingMidYearUpload pending)
        {
            var existing = await _db.MidYearGlActualForecasts.AsNoTracking()
                .Where(x => x.BudgetYear == pending.ActualYear && x.EntityId == pending.EntityId)
                .ToListAsync();
            var byGl = existing.GroupBy(x => (x.GLCode ?? "").Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First());
            var inFile = pending.Rows.Select(r => r.GLCode.ToUpperInvariant()).ToHashSet();

            var vm = new UploadPreviewVm
            {
                Title = "Review mid-year upload",
                Subtitle = $"{pending.EntityCode} · actuals and forecast for {pending.ActualYear} (budget {pending.BudgetYear}).",
                FileName = pending.FileName,
                ConfirmController = "MidYear",
                ConfirmAction = nameof(ConfirmUpload),
                CancelAction = nameof(CancelUpload),
                KeyHeader = "GL",
                Key2Header = "GL type",
                ValueAHeader = "H1 actual",
                ValueBHeader = "H2 forecast",
                KeptCount = existing.Count(x => !inFile.Contains((x.GLCode ?? "").Trim().ToUpperInvariant())),
                TotalABefore = existing.Sum(x => x.ActualH1Amount),
                TotalBBefore = existing.Sum(x => x.ForecastH2Amount ?? 0m)
            };

            decimal aAfter = vm.TotalABefore, bAfter = vm.TotalBBefore ?? 0m;
            foreach (var r in pending.Rows)
            {
                byGl.TryGetValue(r.GLCode.ToUpperInvariant(), out var ex);
                var newB = r.Forecast ?? ex?.ForecastH2Amount;   // blank H2 keeps the stored forecast
                var status = ex == null ? "New"
                    : (ex.ActualH1Amount == r.Actual && ex.ForecastH2Amount == newB && string.Equals(ex.GLType, r.GLType, StringComparison.OrdinalIgnoreCase)) ? "Unchanged" : "Changed";
                if (status == "New") vm.NewCount++; else if (status == "Changed") vm.ChangedCount++; else vm.UnchangedCount++;

                aAfter += r.Actual - (ex?.ActualH1Amount ?? 0m);
                bAfter += (newB ?? 0m) - (ex?.ForecastH2Amount ?? 0m);
                vm.Rows.Add(new UploadPreviewRow
                {
                    Key = r.GLCode,
                    Key2 = r.GLType,
                    Status = status,
                    ABefore = ex?.ActualH1Amount,
                    AAfter = r.Actual,
                    BBefore = ex?.ForecastH2Amount,
                    BAfter = newB
                });
            }
            vm.TotalAAfter = aAfter;
            vm.TotalBAfter = bAfter;
            return vm;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmUpload()
        {
            var json = HttpContext.Session.GetString(PendingUploadKey);
            HttpContext.Session.Remove(PendingUploadKey);
            var pending = string.IsNullOrEmpty(json) ? null : System.Text.Json.JsonSerializer.Deserialize<PendingMidYearUpload>(json);
            var ctxYear = HttpContext.Session.GetInt("ctxYear");
            var ctxEntity = HttpContext.Session.GetInt("ctxEntityId");
            if (pending == null || pending.Rows.Count == 0)
            {
                TempData["Error"] = "Your upload session expired. Please upload the file again.";
                return RedirectToAction(nameof(Index));
            }
            if (ctxEntity != pending.EntityId || (ctxYear ?? pending.BudgetYear) != pending.BudgetYear)
            {
                TempData["Error"] = "The year or entity was changed after the file was uploaded. Nothing was saved; please upload the file again.";
                return RedirectToAction(nameof(Index));
            }

            var userName = User.Identity?.Name ?? "Unknown";
            var actualYear = pending.ActualYear;

            // Data change and upload log are written in one transaction (see ImportHistoryService).
            var (created, updated) = await _history.RunInTransactionAsync(async () =>
            {
                var rec = new GovBudget.Services.ImportRecorder();
                var existing = await _db.MidYearGlActualForecasts
                    .Where(x => x.BudgetYear == actualYear && x.EntityId == pending.EntityId)
                    .ToListAsync();
                var byGl = existing
                    .GroupBy(x => (x.GLCode ?? "").Trim().ToUpperInvariant())
                    .ToDictionary(g => g.Key, g => g.First());

                int created = 0, updated = 0;
                var now = DateTime.UtcNow;
                foreach (var p in pending.Rows)
                {
                    if (byGl.TryGetValue(p.GLCode.ToUpperInvariant(), out var ex))
                    {
                        rec.AddUpdated(GovBudget.Services.ImportTables.MidYear, ex.MidYearId, ex);
                        ex.GLType = p.GLType;
                        ex.ActualH1Amount = p.Actual;
                        ex.SourceFile = pending.FileName;
                        ex.CreatedBy = userName;
                        if (p.Forecast.HasValue)
                        {
                            ex.ForecastH2Amount = p.Forecast;
                            ex.ForecastUpdatedAt = now;
                            ex.ForecastUpdatedBy = userName;
                        }
                        updated++;
                    }
                    else
                    {
                        var row = new MidYearGlActualForecasts
                        {
                            BudgetYear = actualYear,
                            EntityId = pending.EntityId,
                            GLCode = p.GLCode,
                            GLType = p.GLType,
                            ActualH1Amount = p.Actual,
                            ForecastH2Amount = p.Forecast,
                            ForecastUpdatedAt = p.Forecast.HasValue ? now : null,
                            ForecastUpdatedBy = p.Forecast.HasValue ? userName : null,
                            CreatedBy = userName,
                            SourceFile = pending.FileName
                        };
                        _db.MidYearGlActualForecasts.Add(row);
                        rec.AddCreated(GovBudget.Services.ImportTables.MidYear, () => row.MidYearId);
                        created++;
                    }
                }

                await _db.SaveChangesAsync();
                await _history.SaveBatchAsync(rec, GovBudget.Services.ImportTypes.MidYear, actualYear, pending.EntityId,
                    "Mid-Year page", pending.FileName, userName);
                return (created, updated);
            });

            TempData["Success"] = $"Upload saved for {pending.EntityCode} {actualYear}. Created: {created}. Updated: {updated}.";
            return RedirectToAction(nameof(Index), new { year = pending.BudgetYear });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult CancelUpload()
        {
            HttpContext.Session.Remove(PendingUploadKey);
            TempData["Success"] = "Upload cancelled. Nothing was saved.";
            return RedirectToAction(nameof(Index));
        }

        public class PendingMidYearUpload
        {
            public int BudgetYear { get; set; }
            public int ActualYear { get; set; }
            public int EntityId { get; set; }
            public string EntityCode { get; set; } = "";
            public string FileName { get; set; } = "";
            public List<PendingMidYearRow> Rows { get; set; } = new();
        }

        public class PendingMidYearRow
        {
            public string GLCode { get; set; } = "";
            public string GLType { get; set; } = "";
            public decimal Actual { get; set; }
            public decimal? Forecast { get; set; }
        }

        private static bool TryParseDecimal(string raw, out decimal value)
        {
            var s = (raw ?? "").Trim().Replace(",", "");
            if (s.Length == 0)
            {
                value = 0m;
                return false;
            }
            return decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryNormalizeGlType(string raw, out string normalized)
        {
            normalized = (raw ?? "").Trim().ToUpperInvariant() switch
            {
                "REVENUE" => "REVENUE",
                "REV" => "REVENUE",
                "HR" => "HR",
                "OPEX" => "OPEX",
                "CAPEX" => "CAPEX",
                _ => ""
            };
            return normalized != "";
        }

        public class MidYearIndexVm
        {
            public int Year { get; set; }
            public int ActualYear { get; set; }
            public bool ContextYearHasActuals { get; set; }
            public string EntityLabel { get; set; } = "";
            public List<MidYearRowVm> Rows { get; set; } = new List<MidYearRowVm>();
        }

        public class MidYearRowVm
        {
            public long MidYearId { get; set; }
            public string GLCode { get; set; } = "";
            public string GLType { get; set; } = "";
            public decimal ActualH1Amount { get; set; }
            public decimal? ForecastH2Amount { get; set; }
        }

        public class MidYearSaveVm
        {
            public int Year { get; set; }
            public List<MidYearSaveRowVm>? Rows { get; set; }
        }

        public class MidYearSaveRowVm
        {
            public long MidYearId { get; set; }
            public decimal? ForecastH2Amount { get; set; }
        }
    }
}
