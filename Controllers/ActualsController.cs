using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ClosedXML.Excel;
using GovBudget.Models;
using GovBudget.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Controllers
{
    /// <summary>
    /// Current-year ACTUALS import (from SAP GL/MM exports) + Budget vs Actual reporting.
    /// Phase 1: reliable at GL -> Category -> Item. HR actuals ride in as GL-view rows.
    /// Activity/Department derivation and HR employee x allocation-rate are Phase 2.
    /// Additive and entity-scoped, mirroring PerformanceController's access model.
    /// </summary>
    [Authorize(Roles = "ADMIN,SYSADMIN")]
    public class ActualsController : Controller
    {
        private const string SourceGl = "SAP_GL";
        private const string SourceMm = "SAP_MM";
        private const string SourceHrEmp = "HR_EMP";
        private const string SessionKey = "PendingActualUpload";
        private const string HrSessionKey = "PendingHrActualUpload";
        private readonly GovBudgetContext _db;

        public ActualsController(GovBudgetContext db) { _db = db; }

        // ---------------- entity-scope helpers (same pattern as PerformanceController) ----------------
        private int? GetEntityClaimId()
        {
            var entityClaim = User.Claims.FirstOrDefault(c => c.Type == "EntityId")?.Value;
            if (!int.TryParse(entityClaim, out var eid) || eid <= 0) return null;
            return eid;
        }

        private bool IsGlobalAdmin()
        {
            if (User.IsInRole("SYSADMIN")) return true;
            if (!User.IsInRole("ADMIN")) return false;
            return !GetEntityClaimId().HasValue;
        }

        // Global admin: honor requested entity (null = all). Entity admin: forced to claim (-1 if none).
        private int? EffectiveEntityId(int? requested)
        {
            if (IsGlobalAdmin())
                return (requested.HasValue && requested.Value > 0) ? requested : (int?)null;
            return GetEntityClaimId() ?? -1;
        }

        private int ResolveYear(int? year) => year ?? HttpContext.Session.GetInt("ctxYear") ?? DateTime.Now.Year;

        private List<SelectListItem> YearOptions(int selected)
        {
            var thisYear = DateTime.Now.Year;
            return new[] { thisYear - 2, thisYear - 1, thisYear, thisYear + 1 }
                .Select(y => new SelectListItem(y.ToString(), y.ToString(), y == selected))
                .ToList();
        }

        private async Task<List<SelectListItem>> EntityOptions(int? selected)
        {
            var q = _db.Entities.AsNoTracking().Where(e => e.IsActive).AsQueryable();
            var global = IsGlobalAdmin();
            if (!global)
            {
                var myId = GetEntityClaimId();
                q = q.Where(e => myId.HasValue && e.EntityId == myId.Value);
            }
            var list = await q.OrderBy(e => e.EntityCode)
                .Select(e => new SelectListItem(e.EntityCode + " - " + e.EntityName, e.EntityId.ToString(), selected.HasValue && e.EntityId == selected.Value))
                .ToListAsync();
            if (global) list.Insert(0, new SelectListItem("All entities", "", !selected.HasValue));
            return list;
        }

        // ---------------- Import hub ----------------
        [HttpGet]
        public async Task<IActionResult> Index(int? year = null, int? entityId = null)
        {
            var selectedYear = ResolveYear(year);
            var scope = EffectiveEntityId(entityId);

            var batches = new List<ActualImportBatches>();
            if (!(scope.HasValue && scope.Value <= 0))
            {
                var q = _db.ActualImportBatches.AsNoTracking().Where(b => b.BudgetYear == selectedYear);
                if (scope.HasValue) q = q.Where(b => b.EntityId == scope.Value);
                batches = await q.OrderByDescending(b => b.ImportedAt).Take(50).ToListAsync();
            }

            var entityMap = await _db.Entities.AsNoTracking().ToDictionaryAsync(e => e.EntityId, e => e.EntityCode + " - " + e.EntityName);
            ViewBag.EntityMap = entityMap;
            ViewBag.Year = selectedYear;
            ViewBag.YearOptions = YearOptions(selectedYear);
            ViewBag.EntityOptions = await EntityOptions(entityId);
            ViewBag.SelectedEntityId = entityId;
            ViewBag.IsGlobalAdmin = IsGlobalAdmin();
            return View(batches);
        }

        // ---------------- Templates ----------------
        [HttpGet]
        public async Task<IActionResult> TemplateGl(int? year = null, int? entityId = null)
        {
            var selectedYear = ResolveYear(year);
            var scope = EffectiveEntityId(entityId);
            using var wb = new XLWorkbook();

            var ws = wb.Worksheets.Add("Actuals");
            var headers = new[] { "EntityCode", "GLCode", "Month (1-12)", "Amount" };
            WriteHeader(ws, headers);
            ws.Cell(2, 1).Value = "ENT01";
            ws.Cell(2, 2).Value = "5000";
            ws.Cell(2, 3).Value = 1;
            ws.Cell(2, 4).Value = 12500;
            ws.Row(2).Style.Font.Italic = true;
            ws.Row(2).Style.Font.FontColor = XLColor.Gray;
            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents();

            await AddReferenceSheet(wb, scope, includeItems: false);

            return WorkbookFile(wb, $"Actuals_GL_Template_{selectedYear}.xlsx");
        }

        [HttpGet]
        public async Task<IActionResult> TemplateMm(int? year = null, int? entityId = null)
        {
            var selectedYear = ResolveYear(year);
            var scope = EffectiveEntityId(entityId);
            using var wb = new XLWorkbook();

            var ws = wb.Worksheets.Add("Actuals");
            var headers = new[] { "EntityCode", "ItemCode", "GLCode (optional)", "Month (1-12)", "Amount" };
            WriteHeader(ws, headers);
            ws.Cell(2, 1).Value = "ENT01";
            ws.Cell(2, 2).Value = "ITM-001";
            ws.Cell(2, 3).Value = "";
            ws.Cell(2, 4).Value = 1;
            ws.Cell(2, 5).Value = 8000;
            ws.Row(2).Style.Font.Italic = true;
            ws.Row(2).Style.Font.FontColor = XLColor.Gray;
            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents();

            await AddReferenceSheet(wb, scope, includeItems: true);

            return WorkbookFile(wb, $"Actuals_MM_Item_Template_{selectedYear}.xlsx");
        }

        [HttpGet]
        public async Task<IActionResult> TemplateHrEmp(int? year = null, int? entityId = null)
        {
            var selectedYear = ResolveYear(year);
            var scope = EffectiveEntityId(entityId);
            using var wb = new XLWorkbook();

            var ws = wb.Worksheets.Add("HR Actuals");
            var headers = new[] { "EntityCode", "EmployeeCode", "Month (1-12)", "Amount" };
            WriteHeader(ws, headers);
            ws.Cell(2, 1).Value = "ENT01";
            ws.Cell(2, 2).Value = "EMP-001";
            ws.Cell(2, 3).Value = 1;
            ws.Cell(2, 4).Value = 15000;
            ws.Row(2).Style.Font.Italic = true;
            ws.Row(2).Style.Font.FontColor = XLColor.Gray;
            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents();

            // Reference: budgeted employees in scope (EmployeeCode must match HrEmployeeCosts.EmployeeId).
            var empQ = _db.HrEmployeeCosts.AsNoTracking().Where(h => h.BudgetYear == selectedYear);
            if (scope.HasValue && scope.Value > 0) empQ = empQ.Where(h => h.EntityId == scope.Value);
            var emps = await empQ.OrderBy(h => h.EmployeeId)
                .Select(h => new { h.EmployeeId, h.EmployeeName, h.GLCode, h.EntityName, h.AnnualCost })
                .ToListAsync();

            var refWs = wb.Worksheets.Add("Employees");
            refWs.Cell(1, 1).Value = "Use the exact EmployeeCode. Actuals are allocated to activities using each employee's budgeted allocation split.";
            refWs.Cell(1, 1).Style.Font.Bold = true;
            var rh = new[] { "EmployeeCode", "Employee Name", "Salary GL", "Entity", "Annual Budget" };
            for (int c = 0; c < rh.Length; c++) refWs.Cell(3, c + 1).Value = rh[c];
            refWs.Range(3, 1, 3, rh.Length).Style.Font.Bold = true;
            int rr = 4;
            foreach (var e in emps)
            {
                refWs.Cell(rr, 1).Value = e.EmployeeId;
                refWs.Cell(rr, 2).Value = e.EmployeeName;
                refWs.Cell(rr, 3).Value = e.GLCode;
                refWs.Cell(rr, 4).Value = e.EntityName;
                refWs.Cell(rr, 5).Value = e.AnnualCost;
                rr++;
            }
            refWs.Columns().AdjustToContents();

            return WorkbookFile(wb, $"Actuals_HR_Employee_Template_{selectedYear}.xlsx");
        }

        // ---------------- Upload (step 1: parse + detect overwrite) ----------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Upload(IFormFile? file, string source, int year, int? entityId = null)
        {
            var selectedYear = year > 0 ? year : ResolveYear(null);
            source = (source == SourceMm) ? SourceMm : SourceGl;

            if (file == null || file.Length == 0)
            {
                TempData["ActualError"] = "Please choose an .xlsx file to upload.";
                return RedirectToAction(nameof(Index), new { year = selectedYear, entityId });
            }

            var global = IsGlobalAdmin();
            var myEntity = GetEntityClaimId();
            if (!global && !myEntity.HasValue)
            {
                TempData["ActualError"] = "Your account is not scoped to an entity, so you cannot import actuals.";
                return RedirectToAction(nameof(Index), new { year = selectedYear, entityId });
            }

            // Lookups
            var entities = await _db.Entities.AsNoTracking().ToListAsync();
            var entityByCode = entities
                .GroupBy(e => e.EntityCode.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().EntityId, StringComparer.OrdinalIgnoreCase);
            var glByCode = await _db.GLAccounts.AsNoTracking()
                .ToDictionaryAsync(g => g.GLCode.Trim(), g => g, StringComparer.OrdinalIgnoreCase);
            var itemByCode = await _db.Items.AsNoTracking()
                .Include(i => i.GLAccount)
                .ToListAsync();
            var itemMap = itemByCode
                .GroupBy(i => i.ItemCode.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var rows = new List<ActualPostings>();
            var errors = new List<string>();

            try
            {
                using var stream = file.OpenReadStream();
                using var wb = new XLWorkbook(stream);
                var ws = wb.Worksheet(1);
                var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

                for (int r = 2; r <= lastRow; r++)
                {
                    string Get(int c) => ws.Cell(r, c).GetString().Trim();

                    string entityCode, glCode, itemCode = null!, monthStr, amountStr;
                    if (source == SourceMm)
                    {
                        entityCode = Get(1); itemCode = Get(2); glCode = Get(3); monthStr = Get(4); amountStr = Get(5);
                    }
                    else
                    {
                        entityCode = Get(1); glCode = Get(2); monthStr = Get(3); amountStr = Get(4);
                    }

                    if (string.IsNullOrWhiteSpace(entityCode) && string.IsNullOrWhiteSpace(glCode) && string.IsNullOrWhiteSpace(itemCode))
                        continue; // blank row

                    // resolve entity
                    int eid;
                    if (!global) eid = myEntity!.Value;
                    else if (string.IsNullOrWhiteSpace(entityCode) || !entityByCode.TryGetValue(entityCode, out eid))
                    { errors.Add($"Row {r}: unknown Entity code '{entityCode}'."); continue; }

                    // resolve item (MM path)
                    Items? item = null;
                    if (source == SourceMm)
                    {
                        if (string.IsNullOrWhiteSpace(itemCode) || !itemMap.TryGetValue(itemCode, out item))
                        { errors.Add($"Row {r}: unknown Item code '{itemCode}'."); continue; }
                        if (string.IsNullOrWhiteSpace(glCode)) glCode = item.GLAccount?.GLCode ?? "";
                    }

                    if (string.IsNullOrWhiteSpace(glCode))
                    { errors.Add($"Row {r}: GL code is required."); continue; }

                    glByCode.TryGetValue(glCode, out var gl);
                    if (gl == null)
                    { errors.Add($"Row {r}: unknown GL code '{glCode}'."); continue; }

                    if (!int.TryParse(monthStr, out var month) || month < 1 || month > 12)
                    { errors.Add($"Row {r}: Month must be 1-12 (was '{monthStr}')."); continue; }

                    if (!decimal.TryParse(amountStr, out var amount))
                    { errors.Add($"Row {r}: Amount is not a number ('{amountStr}')."); continue; }

                    rows.Add(new ActualPostings
                    {
                        BudgetYear = selectedYear,
                        PeriodMonth = (byte)month,
                        EntityId = eid,
                        GLCode = gl.GLCode,
                        GLType = gl.GLType,
                        ItemId = item?.ItemId,
                        ItemCode = item?.ItemCode,
                        Amount = amount,
                        Source = source,
                        SourceFile = file.FileName
                    });
                }
            }
            catch (Exception ex)
            {
                TempData["ActualError"] = "Could not read the file. Make sure it is the .xlsx template (data on the first sheet). " + ex.Message;
                return RedirectToAction(nameof(Index), new { year = selectedYear, entityId });
            }

            if (rows.Count == 0)
            {
                var msg = "No valid rows found.";
                if (errors.Count > 0) msg += " " + string.Join(" | ", errors.Take(15));
                TempData["ActualError"] = msg;
                return RedirectToAction(nameof(Index), new { year = selectedYear, entityId });
            }

            // Detect existing postings in the same scope (year + entity set + source) => overwrite confirmation
            var affectedEntities = rows.Select(x => x.EntityId).Distinct().ToList();
            var existing = await _db.ActualPostings.AsNoTracking()
                .Where(p => p.BudgetYear == selectedYear && p.Source == source && affectedEntities.Contains(p.EntityId))
                .ToListAsync();

            if (existing.Count > 0)
            {
                HttpContext.Session.SetString(SessionKey, JsonSerializer.Serialize(new PendingActualUpload
                {
                    Source = source,
                    Year = selectedYear,
                    RequestEntityId = entityId,
                    Rows = rows
                }));

                var entityMap = entities.ToDictionary(e => e.EntityId, e => e.EntityCode + " - " + e.EntityName);
                var vm = new ActualOverwriteConfirmVm
                {
                    Source = source,
                    Year = selectedYear,
                    RequestEntityId = entityId,
                    ExistingCount = existing.Count,
                    ExistingTotal = existing.Sum(x => x.Amount),
                    NewCount = rows.Count,
                    NewTotal = rows.Sum(x => x.Amount),
                    Errors = errors,
                    Scopes = affectedEntities.Select(eid => new ActualOverwriteScopeVm
                    {
                        EntityLabel = entityMap.TryGetValue(eid, out var lbl) ? lbl : eid.ToString(),
                        ExistingCount = existing.Count(x => x.EntityId == eid),
                        ExistingTotal = existing.Where(x => x.EntityId == eid).Sum(x => x.Amount),
                        NewCount = rows.Count(x => x.EntityId == eid),
                        NewTotal = rows.Where(x => x.EntityId == eid).Sum(x => x.Amount),
                        ExistingMonths = existing.Where(x => x.EntityId == eid)
                            .Select(x => (int)x.PeriodMonth).Distinct().OrderBy(m => m).ToList(),
                        NewMonths = rows.Where(x => x.EntityId == eid)
                            .Select(x => (int)x.PeriodMonth).Distinct().OrderBy(m => m).ToList()
                    }).ToList()
                };
                return View("ConfirmOverwrite", vm);
            }

            // No conflicts: straight insert
            await ApplyAsync(rows, selectedYear, source, affectedEntities, deleteExisting: false, file.FileName);
            TempData["ActualResult"] = BuildResultMessage(rows.Count, 0, errors);
            return RedirectToAction(nameof(Index), new { year = selectedYear, entityId });
        }

        // ---------------- Confirm overwrite (step 2) ----------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmUpload()
        {
            // HR employee actuals confirmation takes precedence when that flow is pending.
            var hrJson = HttpContext.Session.GetString(HrSessionKey);
            if (!string.IsNullOrEmpty(hrJson))
            {
                var hrPending = JsonSerializer.Deserialize<PendingHrActualUpload>(hrJson)!;
                HttpContext.Session.Remove(HrSessionKey);
                if (!IsGlobalAdmin())
                {
                    var myId = GetEntityClaimId();
                    if (!myId.HasValue) return Forbid();
                    hrPending.Rows = hrPending.Rows.Where(r => r.EntityId == myId.Value).ToList();
                }
                var hrAffected = hrPending.Rows.Select(r => r.EntityId).Distinct().ToList();
                var hrReplaced = await ApplyHrAsync(hrPending.Rows, hrPending.Year, hrAffected, deleteExisting: true,
                    hrPending.Rows.FirstOrDefault()?.SourceFile);
                TempData["ActualResult"] = $"HR overwrite complete: {hrReplaced} previous row(s) replaced with {hrPending.Rows.Count} new row(s).";
                return RedirectToAction(nameof(Index), new { year = hrPending.Year, entityId = hrPending.RequestEntityId });
            }

            var json = HttpContext.Session.GetString(SessionKey);
            if (string.IsNullOrEmpty(json))
            {
                TempData["ActualError"] = "The pending upload has expired. Please upload the file again.";
                return RedirectToAction(nameof(Index));
            }
            var pending = JsonSerializer.Deserialize<PendingActualUpload>(json)!;
            HttpContext.Session.Remove(SessionKey);

            // Re-enforce entity scope for entity admins
            if (!IsGlobalAdmin())
            {
                var myId = GetEntityClaimId();
                if (!myId.HasValue) return Forbid();
                pending.Rows = pending.Rows.Where(r => r.EntityId == myId.Value).ToList();
            }

            var affected = pending.Rows.Select(r => r.EntityId).Distinct().ToList();
            var replaced = await ApplyAsync(pending.Rows, pending.Year, pending.Source, affected, deleteExisting: true,
                pending.Rows.FirstOrDefault()?.SourceFile);

            TempData["ActualResult"] = $"Overwrite complete: {replaced} previous row(s) replaced with {pending.Rows.Count} new row(s).";
            return RedirectToAction(nameof(Index), new { year = pending.Year, entityId = pending.RequestEntityId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult CancelUpload()
        {
            HttpContext.Session.Remove(SessionKey);
            HttpContext.Session.Remove(HrSessionKey);
            TempData["ActualResult"] = "Upload cancelled. Nothing was changed.";
            return RedirectToAction(nameof(Index));
        }

        // ---------------- HR employee actuals upload (step 1) ----------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadHrEmp(IFormFile? file, int year, int? entityId = null)
        {
            var selectedYear = year > 0 ? year : ResolveYear(null);
            if (file == null || file.Length == 0)
            {
                TempData["ActualError"] = "Please choose an .xlsx file to upload.";
                return RedirectToAction(nameof(Index), new { year = selectedYear, entityId });
            }

            var global = IsGlobalAdmin();
            var myEntity = GetEntityClaimId();
            if (!global && !myEntity.HasValue)
            {
                TempData["ActualError"] = "Your account is not scoped to an entity, so you cannot import actuals.";
                return RedirectToAction(nameof(Index), new { year = selectedYear, entityId });
            }

            var entities = await _db.Entities.AsNoTracking().ToListAsync();
            var entityByCode = entities
                .GroupBy(e => e.EntityCode.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().EntityId, StringComparer.OrdinalIgnoreCase);

            // Budgeted employees for the year: (EntityId, EmployeeId) -> (EmployeeCostId, GLCode)
            var empList = await _db.HrEmployeeCosts.AsNoTracking()
                .Where(h => h.BudgetYear == selectedYear && h.EntityId != null)
                .Select(h => new { h.EmployeeCostId, h.EntityId, h.EmployeeId, h.GLCode })
                .ToListAsync();
            var empMap = empList
                .GroupBy(h => (h.EntityId!.Value, (h.EmployeeId ?? "").Trim().ToUpperInvariant()))
                .ToDictionary(g => g.Key, g => g.First());

            var rows = new List<HrActualPostings>();
            var errors = new List<string>();
            try
            {
                using var stream = file.OpenReadStream();
                using var wb = new XLWorkbook(stream);
                var ws = wb.Worksheet(1);
                var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

                for (int r = 2; r <= lastRow; r++)
                {
                    string Get(int c) => ws.Cell(r, c).GetString().Trim();
                    var entityCode = Get(1);
                    var empCode = Get(2);
                    var monthStr = Get(3);
                    var amountStr = Get(4);

                    if (string.IsNullOrWhiteSpace(entityCode) && string.IsNullOrWhiteSpace(empCode)) continue;

                    int eid;
                    if (!global) eid = myEntity!.Value;
                    else if (string.IsNullOrWhiteSpace(entityCode) || !entityByCode.TryGetValue(entityCode, out eid))
                    { errors.Add($"Row {r}: unknown Entity code '{entityCode}'."); continue; }

                    if (string.IsNullOrWhiteSpace(empCode))
                    { errors.Add($"Row {r}: EmployeeCode is required."); continue; }
                    if (!int.TryParse(monthStr, out var month) || month < 1 || month > 12)
                    { errors.Add($"Row {r}: Month must be 1-12 (was '{monthStr}')."); continue; }
                    if (!decimal.TryParse(amountStr, out var amount))
                    { errors.Add($"Row {r}: Amount is not a number ('{amountStr}')."); continue; }

                    empMap.TryGetValue((eid, empCode.ToUpperInvariant()), out var emp);
                    rows.Add(new HrActualPostings
                    {
                        BudgetYear = selectedYear,
                        PeriodMonth = (byte)month,
                        EntityId = eid,
                        EmployeeCode = empCode,
                        EmployeeCostId = emp?.EmployeeCostId,
                        GLCode = emp?.GLCode,
                        Amount = amount,
                        Source = SourceHrEmp,
                        SourceFile = file.FileName
                    });
                }
            }
            catch (Exception ex)
            {
                TempData["ActualError"] = "Could not read the file. Make sure it is the .xlsx template (data on the first sheet). " + ex.Message;
                return RedirectToAction(nameof(Index), new { year = selectedYear, entityId });
            }

            if (rows.Count == 0)
            {
                var msg = "No valid rows found.";
                if (errors.Count > 0) msg += " " + string.Join(" | ", errors.Take(15));
                TempData["ActualError"] = msg;
                return RedirectToAction(nameof(Index), new { year = selectedYear, entityId });
            }

            var unmatched = rows.Count(x => x.EmployeeCostId == null);
            if (unmatched > 0)
                errors.Add($"{unmatched} row(s) had an EmployeeCode with no matching budgeted employee - their amounts count toward HR totals but cannot be allocated to activities.");

            var affectedEntities = rows.Select(x => x.EntityId).Distinct().ToList();
            var existing = await _db.HrActualPostings.AsNoTracking()
                .Where(p => p.BudgetYear == selectedYear && affectedEntities.Contains(p.EntityId))
                .ToListAsync();

            if (existing.Count > 0)
            {
                HttpContext.Session.SetString(HrSessionKey, JsonSerializer.Serialize(new PendingHrActualUpload
                {
                    Year = selectedYear,
                    RequestEntityId = entityId,
                    Rows = rows
                }));
                var entityMap = entities.ToDictionary(e => e.EntityId, e => e.EntityCode + " - " + e.EntityName);
                var vm = new ActualOverwriteConfirmVm
                {
                    Source = SourceHrEmp,
                    Year = selectedYear,
                    RequestEntityId = entityId,
                    ExistingCount = existing.Count,
                    ExistingTotal = existing.Sum(x => x.Amount),
                    NewCount = rows.Count,
                    NewTotal = rows.Sum(x => x.Amount),
                    Errors = errors,
                    Scopes = affectedEntities.Select(eid => new ActualOverwriteScopeVm
                    {
                        EntityLabel = entityMap.TryGetValue(eid, out var lbl) ? lbl : eid.ToString(),
                        ExistingCount = existing.Count(x => x.EntityId == eid),
                        ExistingTotal = existing.Where(x => x.EntityId == eid).Sum(x => x.Amount),
                        NewCount = rows.Count(x => x.EntityId == eid),
                        NewTotal = rows.Where(x => x.EntityId == eid).Sum(x => x.Amount),
                        ExistingMonths = existing.Where(x => x.EntityId == eid)
                            .Select(x => (int)x.PeriodMonth).Distinct().OrderBy(m => m).ToList(),
                        NewMonths = rows.Where(x => x.EntityId == eid)
                            .Select(x => (int)x.PeriodMonth).Distinct().OrderBy(m => m).ToList()
                    }).ToList()
                };
                return View("ConfirmOverwrite", vm);
            }

            await ApplyHrAsync(rows, selectedYear, affectedEntities, deleteExisting: false, file.FileName);
            TempData["ActualResult"] = BuildResultMessage(rows.Count, 0, errors);
            return RedirectToAction(nameof(Index), new { year = selectedYear, entityId });
        }

        private async Task<int> ApplyHrAsync(List<HrActualPostings> rows, int year, List<int> entityIds, bool deleteExisting, string? sourceFile)
        {
            var userName = User.Identity?.Name;
            var strategy = _db.Database.CreateExecutionStrategy();
            int deleted = 0;

            await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync();

                if (deleteExisting)
                {
                    deleted = await _db.HrActualPostings
                        .Where(p => p.BudgetYear == year && entityIds.Contains(p.EntityId))
                        .ExecuteDeleteAsync();
                }

                foreach (var eid in entityIds)
                {
                    var scopeRows = rows.Where(r => r.EntityId == eid).ToList();
                    if (scopeRows.Count == 0) continue;

                    var batch = new ActualImportBatches
                    {
                        BudgetYear = year,
                        EntityId = eid,
                        Source = SourceHrEmp,
                        PeriodFrom = (byte)scopeRows.Min(r => r.PeriodMonth),
                        PeriodTo = (byte)scopeRows.Max(r => r.PeriodMonth),
                        RowsImported = scopeRows.Count,
                        TotalAmount = scopeRows.Sum(r => r.Amount),
                        SourceFile = sourceFile,
                        ImportedBy = userName
                    };
                    _db.ActualImportBatches.Add(batch);
                    await _db.SaveChangesAsync();

                    foreach (var row in scopeRows) { row.ImportBatchId = batch.ActualImportBatchId; row.CreatedBy = userName; }
                    _db.HrActualPostings.AddRange(scopeRows);
                    await _db.SaveChangesAsync();
                }

                _db.AuditLogs.Add(new AuditLogs
                {
                    UserName = userName ?? "system",
                    Action = deleteExisting ? "HR_ACTUAL_REPLACE" : "HR_ACTUAL_IMPORT",
                    EntityName = "HrActualPostings",
                    RecordId = $"{SourceHrEmp} {year}"
                });
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            });
            return deleted;
        }

        // ---------------- Forecast-to-complete entry ----------------
        [HttpGet]
        public async Task<IActionResult> Forecast(int? year = null, int? entityId = null)
        {
            var selectedYear = ResolveYear(year);
            var scope = EffectiveEntityId(entityId);

            var vm = new ForecastEditVm { Year = selectedYear, RequestEntityId = entityId };

            // Forecast-to-complete is written per entity. Global admins must pick a specific entity.
            if (!scope.HasValue)
            {
                vm.NeedsEntity = true;
            }
            else if (scope.Value > 0)
            {
                vm.EntityId = scope.Value;
                var bva = await BuildBudgetVsActual(selectedYear, scope.Value);
                vm.Rows = bva.Groups.SelectMany(g => g.Rows).Select(r => new ForecastRowVm
                {
                    GLCode = r.GLCode,
                    GLName = r.GLName,
                    Category = r.Category,
                    Budget = r.Budget,
                    ActualYtd = r.ActualYtd,
                    Forecast = r.Forecast
                }).OrderBy(r => r.Category).ThenBy(r => r.GLCode).ToList();
            }

            ViewBag.Year = selectedYear;
            ViewBag.YearOptions = YearOptions(selectedYear);
            ViewBag.EntityOptions = await EntityOptions(entityId);
            ViewBag.SelectedEntityId = entityId;
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForecastSave(int year, int entityId, List<string>? glCodes, List<string>? forecasts, List<string>? notes)
        {
            if (year <= 0) year = ResolveYear(null);

            // Enforce entity scope for entity admins.
            if (!IsGlobalAdmin())
            {
                var myId = GetEntityClaimId();
                if (!myId.HasValue) return Forbid();
                entityId = myId.Value;
            }
            if (entityId <= 0)
            {
                TempData["ActualError"] = "Please select a specific entity before saving forecasts.";
                return RedirectToAction(nameof(Forecast), new { year });
            }

            glCodes ??= new(); forecasts ??= new(); notes ??= new();
            var glMeta = await _db.GLAccounts.AsNoTracking()
                .ToDictionaryAsync(g => g.GLCode, g => g.GLType, StringComparer.OrdinalIgnoreCase);

            var userName = User.Identity?.Name;
            var strategy = _db.Database.CreateExecutionStrategy();
            int saved = 0, cleared = 0;

            await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync();

                var existing = await _db.ActualForecasts
                    .Where(f => f.BudgetYear == year && f.EntityId == entityId)
                    .ToListAsync();
                var byCode = existing.ToDictionary(f => f.GLCode, StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < glCodes.Count; i++)
                {
                    var code = (glCodes[i] ?? "").Trim();
                    if (string.IsNullOrEmpty(code)) continue;
                    var rawVal = i < forecasts.Count ? forecasts[i] : null;
                    var note = i < notes.Count ? notes[i] : null;
                    var hasVal = decimal.TryParse(rawVal, out var val);

                    byCode.TryGetValue(code, out var row);
                    if (!hasVal || val == 0m)
                    {
                        // Blank/zero clears an existing forecast (keeps the table tidy).
                        if (row != null) { _db.ActualForecasts.Remove(row); cleared++; }
                        continue;
                    }

                    if (row == null)
                    {
                        _db.ActualForecasts.Add(new ActualForecasts
                        {
                            BudgetYear = year,
                            EntityId = entityId,
                            GLCode = code,
                            GLType = glMeta.TryGetValue(code, out var t) ? t : null,
                            ForecastRemaining = val,
                            Notes = string.IsNullOrWhiteSpace(note) ? null : note!.Trim(),
                            UpdatedBy = userName
                        });
                    }
                    else
                    {
                        row.ForecastRemaining = val;
                        row.Notes = string.IsNullOrWhiteSpace(note) ? null : note!.Trim();
                        row.UpdatedAt = DateTime.UtcNow;
                        row.UpdatedBy = userName;
                    }
                    saved++;
                }
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            });

            TempData["ActualResult"] = $"Forecast saved: {saved} GL line(s) updated" + (cleared > 0 ? $", {cleared} cleared." : ".");
            return RedirectToAction(nameof(Forecast), new { year, entityId });
        }

        // ---------------- Forecast-to-complete: Excel template + upload ----------------
        // The template is pre-filled with the entity's GL lines, budget, actual YTD and any
        // forecast already captured, so the user only types the remaining-spend column.
        [HttpGet]
        public async Task<IActionResult> ForecastTemplate(int? year = null, int? entityId = null)
        {
            var selectedYear = ResolveYear(year);
            var scope = EffectiveEntityId(entityId);

            if (!scope.HasValue || scope.Value <= 0)
            {
                TempData["ActualError"] = "Forecasts are captured per entity. Please select a specific entity before downloading the template.";
                return RedirectToAction(nameof(Forecast), new { year = selectedYear, entityId });
            }

            var entity = await _db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.EntityId == scope.Value);
            if (entity == null)
            {
                TempData["ActualError"] = "The selected entity no longer exists.";
                return RedirectToAction(nameof(Forecast), new { year = selectedYear });
            }

            var bva = await BuildBudgetVsActual(selectedYear, scope.Value);
            var rows = bva.Groups.SelectMany(g => g.Rows)
                .OrderBy(r => r.Category).ThenBy(r => r.GLCode)
                .ToList();

            var notesByGl = await _db.ActualForecasts.AsNoTracking()
                .Where(f => f.BudgetYear == selectedYear && f.EntityId == scope.Value)
                .ToDictionaryAsync(f => f.GLCode, f => f.Notes, StringComparer.OrdinalIgnoreCase);

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Forecast");
            var headers = new[]
            {
                "EntityCode", "GLCode", "GL Name", "Category",
                "Budget", "Actual YTD", "Remaining", "Forecast to Complete", "Notes"
            };
            WriteHeader(ws, headers);

            int r = 2;
            foreach (var row in rows)
            {
                ws.Cell(r, 1).Value = entity.EntityCode;
                ws.Cell(r, 2).Value = row.GLCode;
                ws.Cell(r, 3).Value = row.GLName;
                ws.Cell(r, 4).Value = row.Category;
                ws.Cell(r, 5).Value = row.Budget;
                ws.Cell(r, 6).Value = row.ActualYtd;
                ws.Cell(r, 7).Value = row.Budget - row.ActualYtd;
                if (row.Forecast != 0m) ws.Cell(r, 8).Value = row.Forecast;
                if (notesByGl.TryGetValue(row.GLCode, out var note) && !string.IsNullOrWhiteSpace(note))
                {
                    ws.Cell(r, 9).Value = note;
                }
                r++;
            }

            // Only the last two columns are meant to be typed in.
            ws.Range(1, 8, Math.Max(r - 1, 1), 9).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF9DB");
            ws.Column(5).Style.NumberFormat.Format = "#,##0.00";
            ws.Column(6).Style.NumberFormat.Format = "#,##0.00";
            ws.Column(7).Style.NumberFormat.Format = "#,##0.00";
            ws.Column(8).Style.NumberFormat.Format = "#,##0.00";
            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents();

            var info = wb.Worksheets.Add("Instructions");
            var lines = new[]
            {
                $"Forecast to Complete — {entity.EntityCode} - {entity.EntityName} — FY {selectedYear}",
                "",
                "1. Fill the 'Forecast to Complete' column (shaded) with the spend still expected for the rest of the year.",
                "2. 'Notes' is optional and free text (max 400 characters).",
                "3. Do not change the header row, the GLCode column or the sheet name.",
                "4. Leave a row blank to keep it unchanged; enter 0 to clear a forecast that was captured before.",
                "5. Extra rows you add must use a GLCode that exists in the chart of accounts.",
                "6. Full year = Actual YTD + Forecast to Complete. Budget, Actual YTD and Remaining are shown for reference only and are ignored on upload.",
                "",
                "Upload the completed file on the Forecast to Complete screen."
            };
            for (int i = 0; i < lines.Length; i++) info.Cell(i + 1, 1).Value = lines[i];
            info.Cell(1, 1).Style.Font.Bold = true;
            info.Columns().AdjustToContents();

            return WorkbookFile(wb, $"ForecastToComplete_{entity.EntityCode}_{selectedYear}.xlsx");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForecastUpload(IFormFile? file, int year, int? entityId = null)
        {
            var selectedYear = year > 0 ? year : ResolveYear(null);
            var scope = EffectiveEntityId(entityId);

            if (!scope.HasValue || scope.Value <= 0)
            {
                TempData["ActualError"] = "Forecasts are captured per entity. Please select a specific entity before uploading.";
                return RedirectToAction(nameof(Forecast), new { year = selectedYear, entityId });
            }

            if (file == null || file.Length == 0)
            {
                TempData["ActualError"] = "Please choose an .xlsx file to upload.";
                return RedirectToAction(nameof(Forecast), new { year = selectedYear, entityId });
            }

            var targetEntityId = scope.Value;
            var glTypeByCode = await _db.GLAccounts.AsNoTracking()
                .ToDictionaryAsync(g => g.GLCode, g => g.GLType, StringComparer.OrdinalIgnoreCase);

            var parsed = new List<(string GlCode, decimal? Value, string? Note)>();
            var errors = new List<string>();

            try
            {
                using var stream = file.OpenReadStream();
                using var wb = new XLWorkbook(stream);
                var ws = wb.Worksheets.FirstOrDefault(x => string.Equals(x.Name, "Forecast", StringComparison.OrdinalIgnoreCase))
                         ?? wb.Worksheet(1);

                // Column positions are read from the header row so a reordered file still works.
                var colByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var headerRow = ws.Row(1);
                var lastCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 0;
                for (int c = 1; c <= lastCol; c++)
                {
                    var name = new string((headerRow.Cell(c).GetString() ?? "")
                        .Where(char.IsLetterOrDigit).ToArray());
                    if (name.Length > 0 && !colByName.ContainsKey(name)) colByName[name] = c;
                }

                int Col(params string[] names)
                {
                    foreach (var n in names)
                    {
                        if (colByName.TryGetValue(n, out var c)) return c;
                    }
                    return 0;
                }

                var glCol = Col("GLCode", "GL", "GLAccountCode");
                var valCol = Col("ForecasttoComplete", "Forecast", "ForecastRemaining");
                var noteCol = Col("Notes", "Note", "Comment");

                if (glCol == 0 || valCol == 0)
                {
                    TempData["ActualError"] = "The file is missing required columns. Please use the downloaded template (columns 'GLCode' and 'Forecast to Complete').";
                    return RedirectToAction(nameof(Forecast), new { year = selectedYear, entityId });
                }

                var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
                for (int rw = 2; rw <= lastRow; rw++)
                {
                    var glCode = ws.Cell(rw, glCol).GetString().Trim();
                    if (string.IsNullOrWhiteSpace(glCode)) continue;

                    if (!glTypeByCode.ContainsKey(glCode))
                    {
                        errors.Add($"Row {rw}: unknown GL code '{glCode}'.");
                        continue;
                    }

                    var raw = ws.Cell(rw, valCol).GetString().Trim();
                    var note = noteCol > 0 ? ws.Cell(rw, noteCol).GetString().Trim() : "";

                    decimal? value = null;
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        if (!decimal.TryParse(raw, out var v))
                        {
                            errors.Add($"Row {rw}: Forecast to Complete is not a number ('{raw}').");
                            continue;
                        }
                        value = v;
                    }

                    // A blank forecast with no note leaves the line untouched.
                    if (value == null && string.IsNullOrWhiteSpace(note)) continue;

                    if (note.Length > 400) note = note.Substring(0, 400);
                    parsed.Add((glCode, value, string.IsNullOrWhiteSpace(note) ? null : note));
                }
            }
            catch (Exception ex)
            {
                TempData["ActualError"] = "Could not read the file. Make sure it is the .xlsx template. " + ex.Message;
                return RedirectToAction(nameof(Forecast), new { year = selectedYear, entityId });
            }

            if (parsed.Count == 0)
            {
                var msg = "No forecast values found in the file.";
                if (errors.Count > 0) msg += " " + string.Join(" | ", errors.Take(15));
                TempData["ActualError"] = msg;
                return RedirectToAction(nameof(Forecast), new { year = selectedYear, entityId });
            }

            var userName = User.Identity?.Name;
            var strategy = _db.Database.CreateExecutionStrategy();
            int saved = 0, cleared = 0;

            await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync();

                var existing = await _db.ActualForecasts
                    .Where(f => f.BudgetYear == selectedYear && f.EntityId == targetEntityId)
                    .ToListAsync();
                var byCode = existing.ToDictionary(f => f.GLCode, StringComparer.OrdinalIgnoreCase);

                // Last row wins if the same GL appears twice in the file.
                foreach (var row in parsed)
                {
                    byCode.TryGetValue(row.GlCode, out var current);

                    if (row.Value == null || row.Value.Value == 0m)
                    {
                        if (current != null)
                        {
                            _db.ActualForecasts.Remove(current);
                            byCode.Remove(row.GlCode);
                            cleared++;
                        }
                        continue;
                    }

                    if (current == null)
                    {
                        var added = new ActualForecasts
                        {
                            BudgetYear = selectedYear,
                            EntityId = targetEntityId,
                            GLCode = row.GlCode,
                            GLType = glTypeByCode.TryGetValue(row.GlCode, out var t) ? t : null,
                            ForecastRemaining = row.Value.Value,
                            Notes = row.Note,
                            UpdatedBy = userName
                        };
                        _db.ActualForecasts.Add(added);
                        byCode[row.GlCode] = added;
                    }
                    else
                    {
                        current.ForecastRemaining = row.Value.Value;
                        if (row.Note != null) current.Notes = row.Note;
                        current.UpdatedAt = DateTime.UtcNow;
                        current.UpdatedBy = userName;
                    }
                    saved++;
                }

                _db.AuditLogs.Add(new AuditLogs
                {
                    UserName = userName ?? "system",
                    Action = "FORECAST_IMPORT",
                    EntityName = "ActualForecasts",
                    RecordId = $"{selectedYear} E{targetEntityId}",
                    Details = $"Forecast to complete uploaded from '{file.FileName}': {saved} saved, {cleared} cleared."
                });

                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            });

            var result = $"Forecast upload complete: {saved} GL line(s) updated" + (cleared > 0 ? $", {cleared} cleared." : ".");
            if (errors.Count > 0)
            {
                result += $" {errors.Count} row(s) skipped: " + string.Join(" | ", errors.Take(12)) + (errors.Count > 12 ? " ..." : "");
            }
            TempData["ActualResult"] = result;
            return RedirectToAction(nameof(Forecast), new { year = selectedYear, entityId });
        }

        // ---------------- Budget vs Actual report ----------------
        // Dimensions supported by the drill-down. "gl"/"category" are POSTED (exact, from SAP GL grain).
        // item/activity/program/department/project are DERIVED by budget-share (except item, which is
        // exact where the actuals file carried an ItemId).
        private static readonly string[] BvaDims = { "gl", "tree", "item", "activity", "program", "department", "project" };

        [HttpGet]
        public async Task<IActionResult> BudgetVsActual(int? year = null, int? entityId = null, string dim = "gl")
        {
            var selectedYear = ResolveYear(year);
            dim = (dim ?? "gl").Trim().ToLowerInvariant();
            if (!BvaDims.Contains(dim)) dim = "gl";

            var vm = await BuildBudgetVsActual(selectedYear, entityId);
            ViewBag.Year = selectedYear;
            ViewBag.YearOptions = YearOptions(selectedYear);
            ViewBag.EntityOptions = await EntityOptions(entityId);
            ViewBag.SelectedEntityId = entityId;
            ViewBag.Dim = dim;
            ViewBag.Tree = dim == "tree" ? await BuildProgramActivityTree(selectedYear, entityId) : null;
            ViewBag.Breakdown = (dim == "gl" || dim == "tree") ? null : await BuildDerivedBreakdown(dim, selectedYear, entityId);
            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> BudgetVsActualExport(int? year = null, int? entityId = null)
        {
            var selectedYear = ResolveYear(year);
            var vm = await BuildBudgetVsActual(selectedYear, entityId);

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Budget vs Actual");
            var headers = new[] { "Category", "GL Code", "GL Name", "Budget", "Actual YTD", "Forecast", "Full Year", "Variance", "Variance %" };
            WriteHeader(ws, headers);
            int r = 2;
            foreach (var g in vm.Groups)
            {
                foreach (var row in g.Rows)
                {
                    ws.Cell(r, 1).Value = g.Category;
                    ws.Cell(r, 2).Value = row.GLCode;
                    ws.Cell(r, 3).Value = row.GLName;
                    ws.Cell(r, 4).Value = row.Budget;
                    ws.Cell(r, 5).Value = row.ActualYtd;
                    ws.Cell(r, 6).Value = row.Forecast;
                    ws.Cell(r, 7).Value = row.FullYear;
                    ws.Cell(r, 8).Value = row.Variance;
                    if (row.VariancePct.HasValue) ws.Cell(r, 9).Value = row.VariancePct.Value / 100m;
                    ws.Cell(r, 9).Style.NumberFormat.Format = "0.0%";
                    r++;
                }
                // subtotal
                ws.Cell(r, 1).Value = g.Category + " total";
                ws.Cell(r, 4).Value = g.Budget;
                ws.Cell(r, 5).Value = g.ActualYtd;
                ws.Cell(r, 6).Value = g.Forecast;
                ws.Cell(r, 7).Value = g.FullYear;
                ws.Cell(r, 8).Value = g.Variance;
                ws.Range(r, 1, r, 9).Style.Font.Bold = true;
                r++;
            }
            ws.Cell(r, 1).Value = "GRAND TOTAL";
            ws.Cell(r, 4).Value = vm.Groups.Sum(g => g.Budget);
            ws.Cell(r, 5).Value = vm.Groups.Sum(g => g.ActualYtd);
            ws.Cell(r, 6).Value = vm.Groups.Sum(g => g.Forecast);
            ws.Cell(r, 7).Value = vm.Groups.Sum(g => g.FullYear);
            ws.Cell(r, 8).Value = vm.Groups.Sum(g => g.Variance);
            ws.Range(r, 1, r, 9).Style.Font.Bold = true;
            ws.Range(r, 1, r, 9).Style.Fill.BackgroundColor = XLColor.FromHtml(GovBudget.Utils.BrandColors.SubtotalHex);
            ws.Column(4).Style.NumberFormat.Format = "#,##0";
            ws.Column(5).Style.NumberFormat.Format = "#,##0";
            ws.Column(6).Style.NumberFormat.Format = "#,##0";
            ws.Column(7).Style.NumberFormat.Format = "#,##0";
            ws.Column(8).Style.NumberFormat.Format = "#,##0";
            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents();

            return WorkbookFile(wb, $"BudgetVsActual_{selectedYear}.xlsx");
        }

        private async Task<BudgetVsActualVm> BuildBudgetVsActual(int year, int? entityId)
        {
            var vm = new BudgetVsActualVm { Year = year };
            var scope = EffectiveEntityId(entityId);
            if (scope.HasValue && scope.Value <= 0) return vm; // entity admin with no entity

            var glMeta = await _db.GLAccounts.AsNoTracking()
                .ToDictionaryAsync(g => g.GLCode, g => new { g.GLName, g.GLType }, StringComparer.OrdinalIgnoreCase);

            // Budget (non-HR) rolled to GL from BudgetLines -> Items -> GLAccounts
            var budgetQ =
                from bl in _db.BudgetLines.AsNoTracking()
                join it in _db.Items.AsNoTracking() on bl.ItemId equals it.ItemId
                join gl in _db.GLAccounts.AsNoTracking() on it.GLAccountId equals gl.GLAccountId
                where bl.BudgetYear == year
                select new { bl.EntityId, gl.GLCode, gl.GLType, bl.Amount };
            if (scope.HasValue) budgetQ = budgetQ.Where(x => x.EntityId == scope.Value);
            var budgetByGl = (await budgetQ.ToListAsync())
                .GroupBy(x => x.GLCode)
                .ToDictionary(g => g.Key, g => (Type: g.First().GLType, Amount: g.Sum(x => x.Amount)), StringComparer.OrdinalIgnoreCase);

            // Budget (HR) rolled to GL from HrEmployeeCosts
            var hrQ = _db.HrEmployeeCosts.AsNoTracking().Where(h => h.BudgetYear == year);
            if (scope.HasValue) hrQ = hrQ.Where(h => h.EntityId == scope.Value);
            var hrByGl = (await hrQ.Select(h => new { h.GLCode, h.AnnualCost }).ToListAsync())
                .GroupBy(x => x.GLCode)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.AnnualCost), StringComparer.OrdinalIgnoreCase);

            // Actuals rolled to GL from ActualPostings
            var actQ = _db.ActualPostings.AsNoTracking().Where(p => p.BudgetYear == year);
            if (scope.HasValue) actQ = actQ.Where(p => p.EntityId == scope.Value);
            var actByGl = (await actQ.Select(p => new { p.GLCode, p.Amount }).ToListAsync())
                .GroupBy(x => x.GLCode)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount), StringComparer.OrdinalIgnoreCase);

            // HR employee actuals roll up to their salary GL (kept separate from GL-view postings to avoid double counting).
            var hrActQ = _db.HrActualPostings.AsNoTracking().Where(p => p.BudgetYear == year && p.GLCode != null);
            if (scope.HasValue) hrActQ = hrActQ.Where(p => p.EntityId == scope.Value);
            foreach (var g in (await hrActQ.Select(p => new { p.GLCode, p.Amount }).ToListAsync())
                         .GroupBy(x => x.GLCode!, StringComparer.OrdinalIgnoreCase))
                actByGl[g.Key] = actByGl.GetValueOrDefault(g.Key) + g.Sum(x => x.Amount);

            // Forecast-to-complete
            var fcQ = _db.ActualForecasts.AsNoTracking().Where(f => f.BudgetYear == year);
            if (scope.HasValue) fcQ = fcQ.Where(f => f.EntityId == scope.Value);
            var fcByGl = (await fcQ.Select(f => new { f.GLCode, f.ForecastRemaining }).ToListAsync())
                .GroupBy(x => x.GLCode)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.ForecastRemaining), StringComparer.OrdinalIgnoreCase);

            // Union of all GL codes seen
            var allCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in budgetByGl.Keys) allCodes.Add(k);
            foreach (var k in hrByGl.Keys) allCodes.Add(k);
            foreach (var k in actByGl.Keys) allCodes.Add(k);
            foreach (var k in fcByGl.Keys) allCodes.Add(k);

            var rows = new List<BvaRow>();
            foreach (var code in allCodes)
            {
                decimal budget = 0m;
                string category;
                if (budgetByGl.TryGetValue(code, out var b)) { budget += b.Amount; category = string.IsNullOrWhiteSpace(b.Type) ? "OTHER" : b.Type; }
                else category = glMeta.TryGetValue(code, out var m0) ? (m0.GLType ?? "OTHER") : "OTHER";
                if (hrByGl.TryGetValue(code, out var hb)) { budget += hb; category = "HR"; }

                var actual = actByGl.TryGetValue(code, out var a) ? a : 0m;
                var forecast = fcByGl.TryGetValue(code, out var f) ? f : 0m;
                var fullYear = actual + forecast;
                var variance = budget - fullYear;

                rows.Add(new BvaRow
                {
                    GLCode = code,
                    GLName = glMeta.TryGetValue(code, out var m) ? m.GLName : "",
                    Category = category,
                    Budget = budget,
                    ActualYtd = actual,
                    Forecast = forecast,
                    FullYear = fullYear,
                    Variance = variance,
                    VariancePct = budget != 0 ? Math.Round((variance / budget) * 100m, 1) : (decimal?)null
                });
            }

            var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            { { "REVENUE", 0 }, { "OPEX", 1 }, { "CAPEX", 2 }, { "HR", 3 } };
            vm.Groups = rows
                .GroupBy(x => x.Category)
                .OrderBy(g => order.TryGetValue(g.Key, out var o) ? o : 99)
                .ThenBy(g => g.Key)
                .Select(g => new BvaCategoryGroup
                {
                    Category = g.Key,
                    Rows = g.OrderBy(x => x.GLCode).ToList(),
                    Budget = g.Sum(x => x.Budget),
                    ActualYtd = g.Sum(x => x.ActualYtd),
                    Forecast = g.Sum(x => x.Forecast),
                    FullYear = g.Sum(x => x.FullYear),
                    Variance = g.Sum(x => x.Variance)
                })
                .ToList();
            return vm;
        }

        // Derived drill-down for a single dimension. Splits each GL's actual + forecast across the
        // dimension members in proportion to their budget on that GL (budget-share). HR is attributed
        // via employee cost allocations. Item is exact where the actuals carried an ItemId.
        // Keyed derived rows for one dimension (Key = dimension member id, or -1 for Unassigned).
        // Shared by the flat drill-down and the Programme -> Activity cascade.
        private async Task<List<(int Key, BvaBreakdownRow Row)>> BuildDerivedRows(string dim, int year, int? entityId)
        {
            const int Unassigned = -1;
            var scope = EffectiveEntityId(entityId);
            if (scope.HasValue && scope.Value <= 0) return new List<(int Key, BvaBreakdownRow Row)>();

            // Non-HR budget projected to GL, resolving programme fallback in memory.
            var blQ =
                from bl in _db.BudgetLines.AsNoTracking()
                join it in _db.Items.AsNoTracking() on bl.ItemId equals it.ItemId
                join gl in _db.GLAccounts.AsNoTracking() on it.GLAccountId equals gl.GLAccountId
                join act in _db.Activities.AsNoTracking() on bl.ActivityId equals act.ActivityId into actJ
                from act in actJ.DefaultIfEmpty()
                where bl.BudgetYear == year
                select new
                {
                    bl.EntityId, gl.GLCode, bl.ItemId, bl.ActivityId,
                    bl.ProgramId, ActProgramId = (int?)(act == null ? (int?)null : act.ProgramId),
                    bl.DepartmentId, bl.ProjectId, bl.Amount
                };
            if (scope.HasValue) blQ = blQ.Where(x => x.EntityId == scope.Value);
            var blRows = await blQ.ToListAsync();

            // HR budget via allocations (carries activity/programme/department/project attribution).
            var hrQ =
                from a in _db.HrEmployeeCostAllocations.AsNoTracking()
                join emp in _db.HrEmployeeCosts.AsNoTracking() on a.EmployeeCostId equals emp.EmployeeCostId
                join act in _db.Activities.AsNoTracking() on a.ActivityId equals act.ActivityId
                where emp.BudgetYear == year && emp.EntityId != null
                select new { EntityId = emp.EntityId!.Value, emp.GLCode, a.ActivityId, act.ProgramId, act.DepartmentId, a.ProjectId, a.AllocatedAmount };
            if (scope.HasValue) hrQ = hrQ.Where(x => x.EntityId == scope.Value);
            var hrRows = await hrQ.ToListAsync();

            // Actuals (with optional ItemId) and forecast-to-complete, by GL.
            var actQ = _db.ActualPostings.AsNoTracking().Where(p => p.BudgetYear == year);
            if (scope.HasValue) actQ = actQ.Where(p => p.EntityId == scope.Value);
            var actList = await actQ.Select(p => new { p.GLCode, p.ItemId, p.Amount }).ToListAsync();

            var fcQ = _db.ActualForecasts.AsNoTracking().Where(f => f.BudgetYear == year);
            if (scope.HasValue) fcQ = fcQ.Where(f => f.EntityId == scope.Value);
            var fcByGl = (await fcQ.Select(f => new { f.GLCode, f.ForecastRemaining }).ToListAsync())
                .GroupBy(x => x.GLCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.ForecastRemaining), StringComparer.OrdinalIgnoreCase);

            bool isItem = dim == "item";
            // For item, actuals tagged with an ItemId are exact; only the untagged remainder is derived.
            var exactByKey = isItem
                ? actList.Where(x => x.ItemId != null)
                    .GroupBy(x => x.ItemId!.Value).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount))
                : new Dictionary<int, decimal>();
            var postedItems = new HashSet<int>(exactByKey.Keys);
            var actForDerive = (isItem ? actList.Where(x => x.ItemId == null) : actList)
                .GroupBy(x => x.GLCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount), StringComparer.OrdinalIgnoreCase);

            // Build budget-by-(key -> gl).
            var budgetByKeyGl = new Dictionary<int, Dictionary<string, decimal>>();
            void AddBudget(int key, string? gl, decimal amt)
            {
                if (string.IsNullOrWhiteSpace(gl) || amt == 0m) return;
                if (!budgetByKeyGl.TryGetValue(key, out var m)) { m = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase); budgetByKeyGl[key] = m; }
                m[gl!] = m.GetValueOrDefault(gl!) + amt;
            }
            foreach (var x in blRows)
            {
                int key = dim switch
                {
                    "item" => x.ItemId,
                    "activity" => x.ActivityId ?? Unassigned,
                    "program" => (x.ProgramId ?? x.ActProgramId) ?? Unassigned,
                    "department" => x.DepartmentId,
                    "project" => x.ProjectId ?? Unassigned,
                    _ => Unassigned
                };
                AddBudget(key, x.GLCode, x.Amount);
            }
            if (!isItem)
            {
                foreach (var x in hrRows)
                {
                    int key = dim switch
                    {
                        "activity" => x.ActivityId,
                        "program" => x.ProgramId,
                        "department" => x.DepartmentId,
                        "project" => x.ProjectId ?? Unassigned,
                        _ => Unassigned
                    };
                    AddBudget(key, x.GLCode, x.AllocatedAmount);
                }
            }

            // Per-GL total budget for the share denominator.
            var totalBudgetByGl = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in budgetByKeyGl)
                foreach (var g in kv.Value)
                    totalBudgetByGl[g.Key] = totalBudgetByGl.GetValueOrDefault(g.Key) + g.Value;

            var actualDerived = new Dictionary<int, decimal>();
            var fcDerived = new Dictionary<int, decimal>();
            decimal unattributedActual = 0m, unattributedFc = 0m;

            var glUnion = new HashSet<string>(actForDerive.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var k in fcByGl.Keys) glUnion.Add(k);
            foreach (var gl in glUnion)
            {
                var aAmt = actForDerive.GetValueOrDefault(gl);
                var fAmt = fcByGl.GetValueOrDefault(gl);
                var totB = totalBudgetByGl.GetValueOrDefault(gl);
                if (totB <= 0m) { unattributedActual += aAmt; unattributedFc += fAmt; continue; }
                foreach (var kv in budgetByKeyGl)
                {
                    if (!kv.Value.TryGetValue(gl, out var kb) || kb == 0m) continue;
                    var ratio = kb / totB;
                    actualDerived[kv.Key] = actualDerived.GetValueOrDefault(kv.Key) + aAmt * ratio;
                    fcDerived[kv.Key] = fcDerived.GetValueOrDefault(kv.Key) + fAmt * ratio;
                }
            }

            // EXACT HR: per-employee actuals x budgeted allocation rate (overrides budget-share for HR).
            var (hrByKey, hrUnassigned) = await ComputeHrExactByDim(dim, year, scope, Unassigned);
            foreach (var kv in hrByKey)
                actualDerived[kv.Key] = actualDerived.GetValueOrDefault(kv.Key) + kv.Value;
            unattributedActual += hrUnassigned;

            // Fold unattributed actuals/forecast (GLs with no dimension budget) into the Unassigned bucket so totals reconcile.
            actualDerived[Unassigned] = actualDerived.GetValueOrDefault(Unassigned) + unattributedActual;
            fcDerived[Unassigned] = fcDerived.GetValueOrDefault(Unassigned) + unattributedFc;

            // Resolve labels for the keys present.
            var keys = new HashSet<int>(budgetByKeyGl.Keys);
            foreach (var k in actualDerived.Keys) keys.Add(k);
            foreach (var k in fcDerived.Keys) keys.Add(k);
            foreach (var k in exactByKey.Keys) keys.Add(k);
            keys.Remove(Unassigned);
            var labels = await ResolveDimLabels(dim, keys.ToList());

            var rows = new List<(int Key, BvaBreakdownRow Row)>();
            void EmitRow(int key, string label, bool derived)
            {
                var budget = budgetByKeyGl.TryGetValue(key, out var gm) ? gm.Values.Sum() : 0m;
                var actual = Math.Round(actualDerived.GetValueOrDefault(key) + exactByKey.GetValueOrDefault(key), 2, MidpointRounding.AwayFromZero);
                var forecast = Math.Round(fcDerived.GetValueOrDefault(key), 2, MidpointRounding.AwayFromZero);
                if (budget == 0m && actual == 0m && forecast == 0m) return;
                var fullYear = actual + forecast;
                var variance = budget - fullYear;
                rows.Add((key, new BvaBreakdownRow
                {
                    Label = label,
                    Budget = budget,
                    ActualYtd = actual,
                    Forecast = forecast,
                    FullYear = fullYear,
                    Variance = variance,
                    VariancePct = budget != 0 ? Math.Round(variance / budget * 100m, 1) : (decimal?)null,
                    IsDerived = derived
                }));
            }

            foreach (var key in keys)
            {
                labels.TryGetValue(key, out var lbl);
                var derived = isItem ? !postedItems.Contains(key) : true;
                EmitRow(key, lbl ?? $"#{key}", derived);
            }
            // Overhead / unattributed actuals + unmatched HR (EmitRow self-skips when everything is zero).
            EmitRow(Unassigned, "Unassigned / overhead", true);

            return rows;
        }

        private async Task<BvaBreakdownVm> BuildDerivedBreakdown(string dim, int year, int? entityId)
        {
            var keyed = await BuildDerivedRows(dim, year, entityId);
            return new BvaBreakdownVm
            {
                Dim = dim,
                Rows = keyed.Select(k => k.Row)
                    .OrderByDescending(r => r.Budget).ThenByDescending(r => r.FullYear).ToList()
            };
        }

        // Programme -> Activity cascade: activity-level variance rolled up into each programme.
        // Uses the activity-keyed derivation (HR exact by allocation rate; other categories by budget-share).
        private async Task<BvaTreeVm> BuildProgramActivityTree(int year, int? entityId)
        {
            var tree = new BvaTreeVm();
            var actRows = await BuildDerivedRows("activity", year, entityId);
            if (actRows.Count == 0) return tree;

            var activityIds = actRows.Where(x => x.Key > 0).Select(x => x.Key).Distinct().ToList();
            var actMeta = await _db.Activities.AsNoTracking()
                .Where(a => activityIds.Contains(a.ActivityId))
                .Select(a => new { a.ActivityId, a.ProgramId })
                .ToListAsync();
            var actToProgram = actMeta.ToDictionary(a => a.ActivityId, a => a.ProgramId);

            var programIds = actToProgram.Values.Distinct().ToList();
            var progLabels = await _db.Programs.AsNoTracking()
                .Where(p => programIds.Contains(p.ProgramId))
                .ToDictionaryAsync(p => p.ProgramId, p => p.ProgramCode + " - " + p.ProgramName);

            const int NoProgram = -100;
            var nodes = new Dictionary<int, BvaProgramNode>();
            BvaProgramNode NodeFor(int programId, string label)
            {
                if (!nodes.TryGetValue(programId, out var n))
                {
                    n = new BvaProgramNode { Label = label };
                    nodes[programId] = n;
                }
                return n;
            }

            foreach (var (key, row) in actRows)
            {
                if (key > 0 && actToProgram.TryGetValue(key, out var pid))
                {
                    var label = progLabels.TryGetValue(pid, out var pl) ? pl : $"Programme #{pid}";
                    NodeFor(pid, label).Activities.Add(row);
                }
                else
                {
                    // Activity-less costs (programme-direct or truly unassigned) roll into a catch-all group.
                    NodeFor(NoProgram, "Unassigned / no activity").Activities.Add(row);
                }
            }

            tree.Programs = nodes.Values
                .OrderBy(n => n.Label == "Unassigned / no activity" ? 1 : 0)
                .ThenByDescending(n => n.Budget)
                .ToList();
            foreach (var n in tree.Programs)
                n.Activities = n.Activities.OrderByDescending(a => a.Budget).ThenByDescending(a => a.FullYear).ToList();
            return tree;
        }

        private async Task<Dictionary<int, string>> ResolveDimLabels(string dim, List<int> keys)
        {
            var map = new Dictionary<int, string>();
            if (keys.Count == 0) return map;
            switch (dim)
            {
                case "item":
                    map = await _db.Items.AsNoTracking().Where(i => keys.Contains(i.ItemId))
                        .ToDictionaryAsync(i => i.ItemId, i => i.ItemCode + " - " + i.ItemName);
                    break;
                case "activity":
                    map = await _db.Activities.AsNoTracking().Where(a => keys.Contains(a.ActivityId))
                        .ToDictionaryAsync(a => a.ActivityId, a => a.ActivityCode + " - " + a.ActivityName);
                    break;
                case "program":
                    map = await _db.Programs.AsNoTracking().Where(p => keys.Contains(p.ProgramId))
                        .ToDictionaryAsync(p => p.ProgramId, p => p.ProgramCode + " - " + p.ProgramName);
                    break;
                case "department":
                    map = await _db.Departments.AsNoTracking().Where(d => keys.Contains(d.DepartmentId))
                        .ToDictionaryAsync(d => d.DepartmentId, d => d.DeptCode + " - " + d.DeptName);
                    break;
                case "project":
                    map = await _db.Projects.AsNoTracking().Where(p => keys.Contains(p.ProjectId))
                        .ToDictionaryAsync(p => p.ProjectId, p => p.ProjectCode + " - " + p.ProjectName);
                    break;
            }
            return map;
        }

        // Exact HR actuals per dimension member = per-employee actual x budgeted allocation share.
        // Employees with no allocations fall back to their own department (department dim) or Unassigned.
        private async Task<(Dictionary<int, decimal> byKey, decimal unassigned)> ComputeHrExactByDim(string dim, int year, int? scope, int unassignedKey)
        {
            var result = new Dictionary<int, decimal>();
            decimal unassigned = 0m;

            var actQ = _db.HrActualPostings.AsNoTracking().Where(p => p.BudgetYear == year);
            if (scope.HasValue) actQ = actQ.Where(p => p.EntityId == scope.Value);
            var actList = await actQ.Select(p => new { p.EmployeeCostId, p.Amount }).ToListAsync();
            if (actList.Count == 0) return (result, unassigned);

            // Item view has no employee->item link: all HR actual is unattributed.
            if (dim == "item")
                return (result, actList.Sum(x => x.Amount));

            var empActual = actList.Where(x => x.EmployeeCostId != null)
                .GroupBy(x => x.EmployeeCostId!.Value)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
            unassigned += actList.Where(x => x.EmployeeCostId == null).Sum(x => x.Amount);
            if (empActual.Count == 0) return (result, unassigned);

            var empIds = empActual.Keys.ToList();
            var allocRows = await (
                from a in _db.HrEmployeeCostAllocations.AsNoTracking()
                join act in _db.Activities.AsNoTracking() on a.ActivityId equals act.ActivityId
                where empIds.Contains(a.EmployeeCostId)
                select new { a.EmployeeCostId, a.ActivityId, act.ProgramId, act.DepartmentId, a.ProjectId, a.AllocatedAmount }
            ).ToListAsync();
            var allocByEmp = allocRows.GroupBy(a => a.EmployeeCostId).ToDictionary(g => g.Key, g => g.ToList());

            var empDeptMap = (await _db.HrEmployeeCosts.AsNoTracking()
                    .Where(h => empIds.Contains(h.EmployeeCostId))
                    .Select(h => new { h.EmployeeCostId, h.DepartmentId }).ToListAsync())
                .ToDictionary(x => x.EmployeeCostId, x => x.DepartmentId);

            foreach (var kv in empActual)
            {
                var empId = kv.Key;
                var actual = kv.Value;
                allocByEmp.TryGetValue(empId, out var allocs);
                var totalAlloc = allocs?.Sum(a => a.AllocatedAmount) ?? 0m;
                if (allocs == null || allocs.Count == 0 || totalAlloc <= 0m)
                {
                    if (dim == "department" && empDeptMap.TryGetValue(empId, out var dId) && dId.HasValue)
                        result[dId.Value] = result.GetValueOrDefault(dId.Value) + actual;
                    else
                        unassigned += actual;
                    continue;
                }
                foreach (var a in allocs)
                {
                    int key = dim switch
                    {
                        "activity" => a.ActivityId,
                        "program" => a.ProgramId,
                        "department" => a.DepartmentId,
                        "project" => a.ProjectId ?? unassignedKey,
                        _ => unassignedKey
                    };
                    result[key] = result.GetValueOrDefault(key) + actual * (a.AllocatedAmount / totalAlloc);
                }
            }
            return (result, unassigned);
        }

        // ---------------- persistence (execution-strategy safe transaction) ----------------
        private async Task<int> ApplyAsync(List<ActualPostings> rows, int year, string source, List<int> entityIds, bool deleteExisting, string? sourceFile)
        {
            var userName = User.Identity?.Name;
            var strategy = _db.Database.CreateExecutionStrategy();
            int deleted = 0;

            await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync();

                if (deleteExisting)
                {
                    deleted = await _db.ActualPostings
                        .Where(p => p.BudgetYear == year && p.Source == source && entityIds.Contains(p.EntityId))
                        .ExecuteDeleteAsync();
                }

                // Create a batch per affected entity for audit/scoping
                foreach (var eid in entityIds)
                {
                    var scopeRows = rows.Where(r => r.EntityId == eid).ToList();
                    if (scopeRows.Count == 0) continue;

                    var batch = new ActualImportBatches
                    {
                        BudgetYear = year,
                        EntityId = eid,
                        Source = source,
                        PeriodFrom = (byte)scopeRows.Min(r => r.PeriodMonth),
                        PeriodTo = (byte)scopeRows.Max(r => r.PeriodMonth),
                        RowsImported = scopeRows.Count,
                        TotalAmount = scopeRows.Sum(r => r.Amount),
                        SourceFile = sourceFile,
                        ImportedBy = userName
                    };
                    _db.ActualImportBatches.Add(batch);
                    await _db.SaveChangesAsync();

                    foreach (var row in scopeRows)
                    {
                        row.ImportBatchId = batch.ActualImportBatchId;
                        row.CreatedBy = userName;
                    }
                    _db.ActualPostings.AddRange(scopeRows);
                    await _db.SaveChangesAsync();
                }

                _db.AuditLogs.Add(new AuditLogs
                {
                    UserName = userName ?? "system",
                    Action = deleteExisting ? "ACTUAL_REPLACE" : "ACTUAL_IMPORT",
                    EntityName = "ActualPostings",
                    RecordId = $"{source} {year}"
                });
                await _db.SaveChangesAsync();

                await tx.CommitAsync();
            });

            return deleted;
        }

        private static string BuildResultMessage(int inserted, int replaced, List<string> errors)
        {
            var msg = $"Import complete: {inserted} row(s) added.";
            if (errors.Count > 0)
                msg += $" {errors.Count} row(s) skipped: " + string.Join(" | ", errors.Take(12)) + (errors.Count > 12 ? " ..." : "");
            return msg;
        }

        // ---------------- ClosedXML helpers ----------------
        private static void WriteHeader(IXLWorksheet ws, string[] headers)
        {
            for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
            var head = ws.Range(1, 1, 1, headers.Length).Style;
            head.Font.Bold = true;
            head.Fill.BackgroundColor = XLColor.FromHtml(GovBudget.Utils.BrandColors.HeaderHex);
            head.Font.FontColor = XLColor.White;
        }

        private async Task AddReferenceSheet(XLWorkbook wb, int? scope, bool includeItems)
        {
            var entitiesQ = _db.Entities.AsNoTracking().AsQueryable();
            if (scope.HasValue && scope.Value > 0) entitiesQ = entitiesQ.Where(e => e.EntityId == scope.Value);
            var entities = await entitiesQ.OrderBy(e => e.EntityCode).ToListAsync();
            var gls = await _db.GLAccounts.AsNoTracking().OrderBy(g => g.GLCode).ToListAsync();

            var ws = wb.Worksheets.Add("Reference");
            ws.Cell(1, 1).Value = "Use these exact codes. Do not edit the header row on the Actuals sheet. One row = one GL (or item) for one month.";
            ws.Cell(1, 1).Style.Font.Bold = true;

            int rr = 3;
            ws.Cell(rr, 1).Value = "ENTITIES"; ws.Cell(rr, 1).Style.Font.Bold = true; rr++;
            ws.Cell(rr, 1).Value = "EntityCode"; ws.Cell(rr, 2).Value = "Entity Name";
            ws.Range(rr, 1, rr, 2).Style.Font.Bold = true; rr++;
            foreach (var e in entities) { ws.Cell(rr, 1).Value = e.EntityCode; ws.Cell(rr, 2).Value = e.EntityName; rr++; }

            rr += 1;
            ws.Cell(rr, 1).Value = "GL ACCOUNTS"; ws.Cell(rr, 1).Style.Font.Bold = true; rr++;
            ws.Cell(rr, 1).Value = "GLCode"; ws.Cell(rr, 2).Value = "GL Name"; ws.Cell(rr, 3).Value = "Type";
            ws.Range(rr, 1, rr, 3).Style.Font.Bold = true; rr++;
            foreach (var g in gls) { ws.Cell(rr, 1).Value = g.GLCode; ws.Cell(rr, 2).Value = g.GLName; ws.Cell(rr, 3).Value = g.GLType; rr++; }

            if (includeItems)
            {
                var items = await _db.Items.AsNoTracking().Include(i => i.GLAccount)
                    .Where(i => i.IsActive).OrderBy(i => i.ItemCode).ToListAsync();
                rr += 1;
                ws.Cell(rr, 1).Value = "ITEMS"; ws.Cell(rr, 1).Style.Font.Bold = true; rr++;
                ws.Cell(rr, 1).Value = "ItemCode"; ws.Cell(rr, 2).Value = "Item Name"; ws.Cell(rr, 3).Value = "GLCode";
                ws.Range(rr, 1, rr, 3).Style.Font.Bold = true; rr++;
                foreach (var i in items) { ws.Cell(rr, 1).Value = i.ItemCode; ws.Cell(rr, 2).Value = i.ItemName; ws.Cell(rr, 3).Value = i.GLAccount?.GLCode ?? ""; rr++; }
            }
            ws.Columns().AdjustToContents();
        }

        private static IActionResult WorkbookFile(XLWorkbook wb, string fileName)
        {
            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return new FileContentResult(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet") { FileDownloadName = fileName };
        }
    }

    // ---------------- namespace-level DTO/VM types ----------------
    public class PendingActualUpload
    {
        public string Source { get; set; } = "SAP_GL";
        public int Year { get; set; }
        public int? RequestEntityId { get; set; }
        public List<ActualPostings> Rows { get; set; } = new();
    }

    public class PendingHrActualUpload
    {
        public int Year { get; set; }
        public int? RequestEntityId { get; set; }
        public List<HrActualPostings> Rows { get; set; } = new();
    }

    public class ActualOverwriteScopeVm
    {
        public string EntityLabel { get; set; } = "";
        public int ExistingCount { get; set; }
        public decimal ExistingTotal { get; set; }
        public int NewCount { get; set; }
        public decimal NewTotal { get; set; }

        // Month coverage. Actuals are replaced for the whole budget year, so a cumulative
        // year-to-date file is expected. Any existing month absent from the upload would be
        // dropped, so it is surfaced here before the user confirms.
        public List<int> ExistingMonths { get; set; } = new();
        public List<int> NewMonths { get; set; } = new();
        public List<int> DroppedMonths => ExistingMonths.Except(NewMonths).OrderBy(m => m).ToList();
        public bool HasDroppedMonths => DroppedMonths.Count > 0;
    }

    public class ActualOverwriteConfirmVm
    {
        public string Source { get; set; } = "SAP_GL";
        public int Year { get; set; }
        public int? RequestEntityId { get; set; }
        public int ExistingCount { get; set; }
        public decimal ExistingTotal { get; set; }
        public int NewCount { get; set; }
        public decimal NewTotal { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<ActualOverwriteScopeVm> Scopes { get; set; } = new();
        public bool AnyDroppedMonths => Scopes.Any(s => s.HasDroppedMonths);
    }

    public class BvaRow
    {
        public string GLCode { get; set; } = "";
        public string GLName { get; set; } = "";
        public string Category { get; set; } = "";
        public decimal Budget { get; set; }
        public decimal ActualYtd { get; set; }
        public decimal Forecast { get; set; }
        public decimal FullYear { get; set; }
        public decimal Variance { get; set; }
        public decimal? VariancePct { get; set; }
    }

    public class BvaCategoryGroup
    {
        public string Category { get; set; } = "";
        public List<BvaRow> Rows { get; set; } = new();
        public decimal Budget { get; set; }
        public decimal ActualYtd { get; set; }
        public decimal Forecast { get; set; }
        public decimal FullYear { get; set; }
        public decimal Variance { get; set; }
    }

    public class BudgetVsActualVm
    {
        public int Year { get; set; }
        public List<BvaCategoryGroup> Groups { get; set; } = new();
    }

    public class BvaBreakdownRow
    {
        public string Label { get; set; } = "";
        public decimal Budget { get; set; }
        public decimal ActualYtd { get; set; }
        public decimal Forecast { get; set; }
        public decimal FullYear { get; set; }
        public decimal Variance { get; set; }
        public decimal? VariancePct { get; set; }
        // True = value derived by budget-share; False = exact posted figure.
        public bool IsDerived { get; set; }
    }

    public class BvaBreakdownVm
    {
        public string Dim { get; set; } = "gl";
        public List<BvaBreakdownRow> Rows { get; set; } = new();
        public decimal Budget => Rows.Sum(r => r.Budget);
        public decimal ActualYtd => Rows.Sum(r => r.ActualYtd);
        public decimal Forecast => Rows.Sum(r => r.Forecast);
        public decimal FullYear => Rows.Sum(r => r.FullYear);
        public decimal Variance => Rows.Sum(r => r.Variance);
        public bool AnyDerived => Rows.Any(r => r.IsDerived);
    }

    // Programme -> Activity cascade. Programme totals are the roll-up of their activity rows.
    public class BvaProgramNode
    {
        public string Label { get; set; } = "";
        public List<BvaBreakdownRow> Activities { get; set; } = new();
        public decimal Budget => Activities.Sum(a => a.Budget);
        public decimal ActualYtd => Activities.Sum(a => a.ActualYtd);
        public decimal Forecast => Activities.Sum(a => a.Forecast);
        public decimal FullYear => Activities.Sum(a => a.FullYear);
        public decimal Variance => Activities.Sum(a => a.Variance);
        public decimal? VariancePct => Budget != 0 ? Math.Round(Variance / Budget * 100m, 1) : (decimal?)null;
        public bool AnyDerived => Activities.Any(a => a.IsDerived);
    }

    public class BvaTreeVm
    {
        public List<BvaProgramNode> Programs { get; set; } = new();
        public decimal Budget => Programs.Sum(p => p.Budget);
        public decimal ActualYtd => Programs.Sum(p => p.ActualYtd);
        public decimal Forecast => Programs.Sum(p => p.Forecast);
        public decimal FullYear => Programs.Sum(p => p.FullYear);
        public decimal Variance => Programs.Sum(p => p.Variance);
    }

    public class ForecastRowVm
    {
        public string GLCode { get; set; } = "";
        public string GLName { get; set; } = "";
        public string Category { get; set; } = "";
        public decimal Budget { get; set; }
        public decimal ActualYtd { get; set; }
        public decimal Forecast { get; set; }
        // Convenience default so the user can start from remaining budget.
        public decimal SuggestedForecast => Math.Max(Budget - ActualYtd, 0m);
    }

    public class ForecastEditVm
    {
        public int Year { get; set; }
        public int? RequestEntityId { get; set; }
        public int EntityId { get; set; }
        public bool NeedsEntity { get; set; }
        public List<ForecastRowVm> Rows { get; set; } = new();
    }
}
