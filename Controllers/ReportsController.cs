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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Controllers
{
    [Authorize]
    public class ReportsController : Controller
    {
        private readonly GovBudgetContext _db;

        public ReportsController(GovBudgetContext db)
        {
            _db = db;
        }

        // ---------- Report Builder catalog (safe, fixed set of dimensions & measures) ----------

        private static string OrEmpty(string code, string name)
        {
            code = (code ?? "").Trim();
            name = (name ?? "").Trim();
            if (code.Length == 0 && name.Length == 0) return "(none)";
            if (code.Length == 0) return name;
            if (name.Length == 0) return code;
            return code + " - " + name;
        }

        private static readonly Dictionary<string, (string Label, Func<LedgerEntry, string> Selector)> BuilderDimensions =
            new()
            {
                ["entity"] = ("Entity", e => OrEmpty(e.EntityCode, e.EntityName)),
                ["category"] = ("Category", e => string.IsNullOrWhiteSpace(e.CategoryCode) ? "(none)" : e.CategoryCode),
                ["gltype"] = ("GL Type", e => string.IsNullOrWhiteSpace(e.GLType) ? "(none)" : e.GLType),
                ["gl"] = ("GL Account", e => OrEmpty(e.GLCode, e.GLName)),
                ["program"] = ("Programme", e => OrEmpty(e.ProgramCode, e.ProgramName)),
                ["programtype"] = ("Program Type", e => string.IsNullOrWhiteSpace(e.ProgramType) ? "Mandate" : e.ProgramType),
                ["activity"] = ("Activity", e => OrEmpty(e.ActivityCode, e.ActivityName)),
                ["project"] = ("Project", e => OrEmpty(e.ProjectCode, e.ProjectName)),
                ["item"] = ("Item", e => OrEmpty(e.ItemCode, e.ItemName))
            };

        private static readonly Dictionary<string, (string Label, Func<LedgerEntry, decimal> Selector)> BuilderMeasures =
            new()
            {
                ["amount"] = ("Budget Amount", e => e.Amount),
                ["forecast1"] = ("Forecast 1", e => e.Forecast1Amount),
                ["forecast2"] = ("Forecast 2", e => e.Forecast2Amount),
                ["quantity"] = ("Quantity", e => e.Quantity),
                ["budgeth1"] = ("Budget H1 (Jan-Jun)", e => e.BudgetH1),
                ["actualh1"] = ("Mid-Year Actual H1", e => e.ActualH1Amount),
                ["varianceh1"] = ("Variance H1 (Budget - Actual)", e => e.VarianceH1)
            };

        // Month columns (budget distribution M01..M12). Used as a special column dimension.
        private const string MonthColKey = "month";
        private static readonly (string Label, Func<LedgerEntry, decimal> Selector)[] MonthColumns =
        {
            ("Jan", e => e.M01), ("Feb", e => e.M02), ("Mar", e => e.M03), ("Apr", e => e.M04),
            ("May", e => e.M05), ("Jun", e => e.M06), ("Jul", e => e.M07), ("Aug", e => e.M08),
            ("Sep", e => e.M09), ("Oct", e => e.M10), ("Nov", e => e.M11), ("Dec", e => e.M12)
        };

        private static List<SelectListItem> DimensionOptions(string? selected, bool includeNone, bool includeMonth = false)
        {
            var list = new List<SelectListItem>();
            if (includeNone) list.Add(new SelectListItem("(none)", "", string.IsNullOrEmpty(selected)));
            foreach (var kv in BuilderDimensions)
                list.Add(new SelectListItem(kv.Value.Label, kv.Key, kv.Key == selected));
            if (includeMonth)
                list.Add(new SelectListItem("Month (Jan-Dec)", MonthColKey, selected == MonthColKey));
            return list;
        }

        private static List<SelectListItem> MeasureOptions(string? selected)
        {
            return BuilderMeasures
                .Select(kv => new SelectListItem(kv.Value.Label, kv.Key, kv.Key == selected))
                .ToList();
        }

        [HttpGet]
        public async Task<IActionResult> Index(string report = "income", int? year = null, int? entityId = null)
        {
            report = NormalizeReport(report);
            var thisYear = DateTime.Now.Year;
            var selectedYear = year ?? HttpContext.Session.GetInt("ctxYear") ?? thisYear;

            var isAdmin = User.IsInRole("ADMIN");
            var isSysAdmin = User.IsInRole("SYSADMIN");
            var isAdminLike = isAdmin || isSysAdmin;
            var scopedEntityId = GetEntityClaimId();
            var isGlobalAdmin = IsGlobalAdmin(isAdmin, isSysAdmin, scopedEntityId);
            var effectiveEntityId = ResolveEntityScope(isAdminLike, isGlobalAdmin, entityId);

            var activeScenario = await GetActiveScenario(selectedYear);
            ViewBag.ActiveScenarioName = activeScenario?.ScenarioName;

            var years = new[] { thisYear - 1, thisYear, thisYear + 1, thisYear + 2 }
                .Select(y => new SelectListItem(y.ToString(), y.ToString(), y == selectedYear))
                .ToList();

            var entitiesQuery = _db.Entities
                .AsNoTracking()
                .OrderBy(e => e.EntityCode)
                .AsQueryable();

            if (!isGlobalAdmin && effectiveEntityId.HasValue)
            {
                entitiesQuery = entitiesQuery.Where(e => e.EntityId == effectiveEntityId.Value);
            }

            var entities = await entitiesQuery
                .Select(e => new SelectListItem(e.EntityCode + " - " + e.EntityName, e.EntityId.ToString()))
                .ToListAsync();

            var entityOptions = new List<SelectListItem>();
            if (isGlobalAdmin)
            {
                entityOptions.Add(new SelectListItem("All Entities", "", !effectiveEntityId.HasValue));
            }
            entityOptions.AddRange(entities);
            foreach (var opt in entityOptions)
            {
                if (effectiveEntityId.HasValue && opt.Value == effectiveEntityId.Value.ToString())
                {
                    opt.Selected = true;
                }
            }

            var vm = new ReportsIndexVm
            {
                Report = report,
                Year = selectedYear,
                IsAdmin = isAdminLike,
                EntityId = effectiveEntityId,
                YearOptions = years,
                EntityOptions = entityOptions
            };

            if (report == "income")
            {
                vm.Income = await BuildIncomeStatement(selectedYear, effectiveEntityId);
            }
            else if (report == "gl")
            {
                vm.GlSummary = await BuildGlSummary(selectedYear, effectiveEntityId);
            }
            else if (report == "projects")
            {
                vm.ProjectCosts = await BuildProjectCosts(selectedYear, effectiveEntityId);
            }
            else if (report == "activities")
            {
                vm.ActivityCosts = await BuildActivityCosts(selectedYear, effectiveEntityId);
            }
            else if (report == "activitiesalloc")
            {
                vm.ActivityCosts = await BuildActivityCostsAfterAllocation(selectedYear, effectiveEntityId);
                vm.AfterAllocation = true;
            }
            else if (report == "hralloc")
            {
                vm.HrAllocations = await BuildHrAllocations(selectedYear, effectiveEntityId);

                var importedQuery = _db.HrEmployeeCosts.AsNoTracking().Where(x => x.BudgetYear == selectedYear);
                if (effectiveEntityId.HasValue)
                {
                    importedQuery = importedQuery.Where(x => x.EntityId == effectiveEntityId.Value);
                }

                var allocatedQuery =
                    from a in _db.HrEmployeeCostAllocations.AsNoTracking()
                    join emp in _db.HrEmployeeCosts.AsNoTracking() on a.EmployeeCostId equals emp.EmployeeCostId
                    where emp.BudgetYear == selectedYear
                    select new { emp.EntityId, a.AllocatedAmount };
                if (effectiveEntityId.HasValue)
                {
                    allocatedQuery = allocatedQuery.Where(x => x.EntityId == effectiveEntityId.Value);
                }

                var importedTotal = await importedQuery.SumAsync(x => (decimal?)x.AnnualCost) ?? 0m;
                var allocatedTotal = await allocatedQuery.SumAsync(x => (decimal?)x.AllocatedAmount) ?? 0m;
                ViewBag.HrImportedTotal = importedTotal;
                ViewBag.HrAllocatedTotal = allocatedTotal;
                ViewBag.HrUnallocatedTotal = importedTotal - allocatedTotal;
            }
            else if (report == "hrrate")
            {
                vm.HrHourlyRates = await BuildHrHourlyRates(selectedYear, effectiveEntityId);
            }
            else if (report == "entitysummary")
            {
                vm.EntitySummary = await BuildEntityBudgetSummary(selectedYear, effectiveEntityId);
            }
            else if (report == "trend")
            {
                vm.TrendSummary = await BuildTrendSummary(selectedYear, effectiveEntityId);
            }

            return View(vm);
        }

        // ---------- Executive deck: Performance-Based vs Traditional budgeting ----------
        // Renders a reveal.js slide deck (Views/Reports/Presentation.cshtml) populated with
        // REAL figures for the selected year/entity, reusing the same builders as the reports.
        [HttpGet]
        public async Task<IActionResult> Presentation(int? year = null, int? entityId = null)
        {
            var thisYear = DateTime.Now.Year;
            var selectedYear = year ?? HttpContext.Session.GetInt("ctxYear") ?? thisYear;

            var isAdmin = User.IsInRole("ADMIN");
            var isSysAdmin = User.IsInRole("SYSADMIN");
            var isAdminLike = isAdmin || isSysAdmin;
            var scopedEntityId = GetEntityClaimId();
            var isGlobalAdmin = IsGlobalAdmin(isAdmin, isSysAdmin, scopedEntityId);
            var effectiveEntityId = ResolveEntityScope(isAdminLike, isGlobalAdmin, entityId);
            var entityLabel = await GetEntityLabel(effectiveEntityId);

            // Traditional (input) lens: totals by category.
            var income = await BuildIncomeStatement(selectedYear, effectiveEntityId);

            // PBB (output) lens: full cost per activity after step-down allocation, rolled up
            // to programmes. Total expense is unchanged — allocation only redistributes it.
            var activitiesAlloc = await BuildActivityCostsAfterAllocation(selectedYear, effectiveEntityId);
            var programmes = activitiesAlloc
                .GroupBy(a => new { a.ProgramCode, a.ProgramName })
                .Select(g => new PbbProgrammeRowVm
                {
                    ProgramCode = g.Key.ProgramCode,
                    ProgramName = g.Key.ProgramName,
                    ActivityCount = g.Select(x => x.ActivityId).Distinct().Count(),
                    Revenue = g.Sum(x => x.Revenue),
                    Expense = g.Sum(x => x.TotalExpense)
                })
                .OrderByDescending(p => p.Expense)
                .ToList();

            // Structural counts for the "how PBB links money to results" story.
            // Guarded: PBB tables may be empty on a fresh install.
            int programmeCount = 0, activityCount = 0, kpiCount = 0, kpiWithTarget = 0,
                kpiCostLinked = 0, mandateCount = 0, supportCount = 0;
            try
            {
                var progQ = _db.Programs.AsNoTracking().AsQueryable();
                if (effectiveEntityId.HasValue) progQ = progQ.Where(p => p.EntityId == effectiveEntityId.Value);
                var progList = await progQ.Select(p => new { p.ProgramId, p.ProgramType }).ToListAsync();
                programmeCount = progList.Count;
                mandateCount = progList.Count(p => (p.ProgramType ?? "Mandate").Equals("Mandate", StringComparison.OrdinalIgnoreCase));
                supportCount = programmeCount - mandateCount;

                var progIds = progList.Select(p => p.ProgramId).ToList();
                activityCount = await _db.Activities.AsNoTracking().CountAsync(a => progIds.Contains(a.ProgramId));

                var kpiQ = _db.Kpis.AsNoTracking().Where(k => k.BudgetYear == selectedYear);
                if (effectiveEntityId.HasValue) kpiQ = kpiQ.Where(k => k.EntityId == effectiveEntityId.Value);
                var kpiList = await kpiQ.Select(k => new { k.KpiId, k.Target }).ToListAsync();
                kpiCount = kpiList.Count;
                kpiWithTarget = kpiList.Count(k => k.Target.HasValue);

                var kpiIds = kpiList.Select(k => k.KpiId).ToList();
                kpiCostLinked = await _db.KpiCostLinks.AsNoTracking()
                    .Where(l => kpiIds.Contains(l.KpiId))
                    .Select(l => l.KpiId).Distinct().CountAsync();
            }
            catch { /* optional PBB tables missing/empty – deck still renders */ }

            var vm = new PbbPresentationVm
            {
                Year = selectedYear,
                EntityLabel = entityLabel,
                IsAllEntities = !effectiveEntityId.HasValue,
                EntityId = effectiveEntityId,
                IsAdmin = isAdminLike,
                YearOptions = new[] { thisYear - 1, thisYear, thisYear + 1, thisYear + 2 }
                    .Select(y => new SelectListItem(y.ToString(), y.ToString(), y == selectedYear)).ToList(),
                EntityOptions = await BuildEntityOptions(isGlobalAdmin, effectiveEntityId),
                TotalRevenue = income.TotalRevenue,
                TotalHr = income.TotalHr,
                TotalOpex = income.TotalOpex,
                TotalCapex = income.TotalCapex,
                TotalExpense = income.TotalExpense,
                SurplusDeficit = income.SurplusDeficit,
                Programmes = programmes,
                ProgrammeCount = programmeCount,
                MandateProgrammeCount = mandateCount,
                SupportProgrammeCount = supportCount,
                ActivityCount = activityCount,
                KpiCount = kpiCount,
                KpiWithTargetCount = kpiWithTarget,
                KpiCostLinkedCount = kpiCostLinked
            };

            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> Export(string report = "income", int? year = null, int? entityId = null)
        {
            report = NormalizeReport(report);
            var thisYear = DateTime.Now.Year;
            var selectedYear = year ?? HttpContext.Session.GetInt("ctxYear") ?? thisYear;

            var isAdmin = User.IsInRole("ADMIN");
            var isSysAdmin = User.IsInRole("SYSADMIN");
            var isAdminLike = isAdmin || isSysAdmin;
            var scopedEntityId = GetEntityClaimId();
            var isGlobalAdmin = IsGlobalAdmin(isAdmin, isSysAdmin, scopedEntityId);
            var effectiveEntityId = ResolveEntityScope(isAdminLike, isGlobalAdmin, entityId);
            var entityLabel = await GetEntityLabel(effectiveEntityId);

            using var wb = new XLWorkbook();

            if (report == "income")
            {
                var income = await BuildIncomeStatement(selectedYear, effectiveEntityId);
                BuildIncomeWorksheet(wb, income, selectedYear, entityLabel);
            }
            else if (report == "gl")
            {
                var gl = await BuildGlSummary(selectedYear, effectiveEntityId);
                BuildGlWorksheet(wb, gl, selectedYear, entityLabel);
                var txns = await GetCostTransactionRows(selectedYear, effectiveEntityId);
                var importedHr = await GetImportedHrRows(selectedYear, effectiveEntityId);
                BuildGlTransactionsWorksheet(wb, txns, importedHr, selectedYear, entityLabel);
            }
            else if (report == "projects")
            {
                var projects = await BuildProjectCosts(selectedYear, effectiveEntityId);
                BuildProjectsWorksheet(wb, projects, selectedYear, entityLabel);
                var txns = await GetCostTransactionRows(selectedYear, effectiveEntityId);
                BuildProjectTransactionsWorksheet(wb, txns, selectedYear, entityLabel);
            }
            else if (report == "activities")
            {
                var activities = await BuildActivityCosts(selectedYear, effectiveEntityId);
                BuildActivitiesWorksheet(wb, activities, selectedYear, entityLabel);
                var txns = await GetCostTransactionRows(selectedYear, effectiveEntityId);
                var realloc = await GetReallocationExportRows(selectedYear, effectiveEntityId);
                BuildActivityTransactionsWorksheet(wb, txns, realloc, selectedYear, entityLabel);
            }
            else if (report == "activitiesalloc")
            {
                var activities = await BuildActivityCostsAfterAllocation(selectedYear, effectiveEntityId);
                BuildActivitiesWorksheet(wb, activities, selectedYear, entityLabel);
                var realloc = await GetReallocationExportRows(selectedYear, effectiveEntityId);
                var txns = await GetCostTransactionRows(selectedYear, effectiveEntityId);
                BuildActivityTransactionsWorksheet(wb, txns, realloc, selectedYear, entityLabel);
            }
            else if (report == "hralloc")
            {
                var hr = await BuildHrAllocations(selectedYear, effectiveEntityId);
                BuildHrAllocationsWorksheet(wb, hr, selectedYear, entityLabel);
            }
            else if (report == "hrrate")
            {
                var rates = await BuildHrHourlyRates(selectedYear, effectiveEntityId);
                BuildHrHourlyRateWorksheet(wb, rates, selectedYear, entityLabel);
            }
            else if (report == "entitysummary")
            {
                var summary = await BuildEntityBudgetSummary(selectedYear, effectiveEntityId);
                BuildEntitySummaryWorksheet(wb, summary, selectedYear, entityLabel);
            }
            else if (report == "trend")
            {
                var trend = await BuildTrendSummary(selectedYear, effectiveEntityId);
                BuildTrendWorksheet(wb, trend, selectedYear, entityLabel);
            }

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            var bytes = stream.ToArray();

            var fileName = $"Report_{report}_{selectedYear}_{entityLabel}.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        // ---------- Report Builder ----------

        [HttpGet]
        public async Task<IActionResult> Builder(int? year = null, int? entityId = null,
            string? rowDim = null, string? colDim = null, string measure = "amount",
            string? category = null, bool includeHr = false, string chartType = "table", bool run = false,
            int? savedId = null, string categoryMode = "Include", List<string>? categories = null,
            string? programType = null, string costBasis = "Direct")
        {
            var thisYear = DateTime.Now.Year;

            // Load a saved configuration (owned by the current user) and apply it.
            var owner = User.Identity?.Name ?? "";
            if (savedId.HasValue)
            {
                var saved = await _db.SavedReports.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.SavedReportId == savedId.Value && s.OwnerUser == owner);
                if (saved != null)
                {
                    year = saved.BudgetYear ?? year;
                    entityId = saved.EntityId ?? entityId;
                    rowDim = saved.RowDim;
                    colDim = saved.ColDim;
                    measure = saved.Measure;
                    category = saved.Category;
                    includeHr = saved.IncludeHr;
                    chartType = saved.ChartType;
                    categoryMode = string.IsNullOrWhiteSpace(saved.CategoryMode) ? "Include" : saved.CategoryMode;
                    categories = string.IsNullOrWhiteSpace(saved.CategoriesCsv)
                        ? new List<string>()
                        : saved.CategoriesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    programType = saved.ProgramTypeFilter;
                    costBasis = string.IsNullOrWhiteSpace(saved.CostBasis) ? "Direct" : saved.CostBasis;
                    run = true;
                }
            }

            var selectedCategories = NormalizeCategories(categories, category);

            var selectedYear = year ?? HttpContext.Session.GetInt("ctxYear") ?? thisYear;

            var isAdmin = User.IsInRole("ADMIN");
            var isSysAdmin = User.IsInRole("SYSADMIN");
            var isAdminLike = isAdmin || isSysAdmin;
            var scopedEntityId = GetEntityClaimId();
            var isGlobalAdmin = IsGlobalAdmin(isAdmin, isSysAdmin, scopedEntityId);
            var effectiveEntityId = ResolveEntityScope(isAdminLike, isGlobalAdmin, entityId);

            var vm = new ReportBuilderVm
            {
                Year = selectedYear,
                IsAdmin = isAdminLike,
                EntityId = effectiveEntityId,
                RowDim = string.IsNullOrWhiteSpace(rowDim) ? "entity" : rowDim,
                ColDim = colDim ?? "",
                Measure = BuilderMeasures.ContainsKey(measure) ? measure : "amount",
                Category = category ?? "",
                CategoryMode = string.Equals(categoryMode, "Exclude", StringComparison.OrdinalIgnoreCase) ? "Exclude" : "Include",
                SelectedCategories = selectedCategories,
                ProgramType = programType ?? "",
                CostBasis = string.Equals(costBasis, "Total", StringComparison.OrdinalIgnoreCase) ? "Total" : "Direct",
                IncludeHr = includeHr,
                ChartType = string.IsNullOrWhiteSpace(chartType) ? "table" : chartType,
                YearOptions = new[] { thisYear - 1, thisYear, thisYear + 1, thisYear + 2 }
                    .Select(y => new SelectListItem(y.ToString(), y.ToString(), y == selectedYear)).ToList(),
                EntityOptions = await BuildEntityOptions(isGlobalAdmin, effectiveEntityId),
                CategoryOptions = await BuildCategoryOptions(category),
                CategoryCodes = await BuildCategoryCodes(),
                RowDimOptions = DimensionOptions(string.IsNullOrWhiteSpace(rowDim) ? "entity" : rowDim, includeNone: false),
                ColDimOptions = DimensionOptions(colDim, includeNone: true, includeMonth: true),
                MeasureOptions = MeasureOptions(BuilderMeasures.ContainsKey(measure) ? measure : "amount")
            };

            vm.SavedId = savedId;
            try
            {
                vm.SavedReports = await _db.SavedReports.AsNoTracking()
                    .Where(s => s.OwnerUser == owner)
                    .OrderBy(s => s.Name)
                    .ToListAsync();
            }
            catch
            {
                // core.SavedReports may not exist yet (migration not applied).
                // Degrade gracefully: the rest of the builder still works.
                vm.SavedReports = new List<SavedReports>();
                vm.SavedReportsUnavailable = true;
            }

            if (run && BuilderDimensions.ContainsKey(vm.RowDim))
            {
                var hrMode = includeHr ? HrLedgerMode.AllocatedOnly : HrLedgerMode.None;
                var rows = await GetLedgerEntries(selectedYear, effectiveEntityId, hrMode);
                if (vm.CostBasis == "Total")
                    rows = await ApplyAllocations(rows, selectedYear, effectiveEntityId);
                rows = ApplyBuilderFilters(rows, vm.CategoryMode, vm.SelectedCategories, vm.ProgramType);
                vm.Result = ComputePivot(rows, vm.RowDim, vm.ColDim, vm.Measure);
                vm.ChartJson = BuildChartJson(vm.Result, vm.ChartType);
            }

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveReport(string name, int? year, int? entityId,
            string rowDim, string? colDim, string measure, string? category, bool includeHr, string chartType,
            string categoryMode = "Include", List<string>? categories = null, string? programType = null,
            string costBasis = "Direct")
        {
            var owner = User.Identity?.Name ?? "";
            var selectedCategories = NormalizeCategories(categories, category);
            var categoriesCsv = selectedCategories.Count > 0 ? string.Join(",", selectedCategories) : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(owner))
                return RedirectToAction(nameof(Builder), new { year, entityId, rowDim, colDim, measure, includeHr, chartType, categoryMode, categories = selectedCategories, programType, costBasis, run = true });

            if (!BuilderDimensions.ContainsKey(rowDim ?? "")) rowDim = "entity";
            if (!BuilderMeasures.ContainsKey(measure ?? "")) measure = "amount";

            var saved = new SavedReports
            {
                OwnerUser = owner,
                Name = name.Trim(),
                BudgetYear = year,
                EntityId = entityId,
                RowDim = rowDim,
                ColDim = string.IsNullOrWhiteSpace(colDim) ? null : colDim,
                Measure = measure,
                Category = selectedCategories.Count == 1 ? selectedCategories[0] : null,
                IncludeHr = includeHr,
                ChartType = string.IsNullOrWhiteSpace(chartType) ? "table" : chartType,
                CategoryMode = string.Equals(categoryMode, "Exclude", StringComparison.OrdinalIgnoreCase) ? "Exclude" : "Include",
                CategoriesCsv = categoriesCsv,
                ProgramTypeFilter = string.IsNullOrWhiteSpace(programType) ? null : programType,
                CostBasis = string.Equals(costBasis, "Total", StringComparison.OrdinalIgnoreCase) ? "Total" : "Direct"
            };
            _db.SavedReports.Add(saved);
            await _db.SaveChangesAsync();

            return RedirectToAction(nameof(Builder), new { savedId = saved.SavedReportId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteReport(int id)
        {
            var owner = User.Identity?.Name ?? "";
            var saved = await _db.SavedReports.FirstOrDefaultAsync(s => s.SavedReportId == id && s.OwnerUser == owner);
            if (saved != null)
            {
                _db.SavedReports.Remove(saved);
                await _db.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Builder));
        }

        [HttpGet]
        public async Task<IActionResult> BuilderExport(int? year = null, int? entityId = null,
            string rowDim = "entity", string? colDim = null, string measure = "amount",
            string? category = null, bool includeHr = false, string categoryMode = "Include",
            List<string>? categories = null, string? programType = null, string costBasis = "Direct")
        {
            var thisYear = DateTime.Now.Year;
            var selectedYear = year ?? HttpContext.Session.GetInt("ctxYear") ?? thisYear;

            var isAdmin = User.IsInRole("ADMIN");
            var isSysAdmin = User.IsInRole("SYSADMIN");
            var isAdminLike = isAdmin || isSysAdmin;
            var scopedEntityId = GetEntityClaimId();
            var isGlobalAdmin = IsGlobalAdmin(isAdmin, isSysAdmin, scopedEntityId);
            var effectiveEntityId = ResolveEntityScope(isAdminLike, isGlobalAdmin, entityId);
            var entityLabel = await GetEntityLabel(effectiveEntityId);

            if (!BuilderDimensions.ContainsKey(rowDim)) rowDim = "entity";
            if (!BuilderMeasures.ContainsKey(measure)) measure = "amount";

            var hrMode = includeHr ? HrLedgerMode.AllocatedOnly : HrLedgerMode.None;
            var rows = await GetLedgerEntries(selectedYear, effectiveEntityId, hrMode);
            if (string.Equals(costBasis, "Total", StringComparison.OrdinalIgnoreCase))
                rows = await ApplyAllocations(rows, selectedYear, effectiveEntityId);
            var selectedCategories = NormalizeCategories(categories, category);
            rows = ApplyBuilderFilters(rows, categoryMode, selectedCategories, programType);
            var result = ComputePivot(rows, rowDim, colDim, measure);

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Report");
            ws.Cell(1, 1).Value = $"{result.MeasureLabel} by {result.RowDimLabel}" + (result.Pivoted ? $" x {result.ColDimLabel}" : "");
            ws.Cell(2, 1).Value = $"Year: {selectedYear}    Entity: {entityLabel}";
            ws.Range(1, 1, 1, Math.Max(2, result.ColumnKeys.Count + 2)).Merge().Style.Font.Bold = true;
            ws.Range(1, 1, 1, Math.Max(2, result.ColumnKeys.Count + 2)).Style.Font.FontSize = 14;

            var r = 4;
            ws.Cell(r, 1).Value = result.RowDimLabel;
            if (result.Pivoted)
            {
                for (var i = 0; i < result.ColumnKeys.Count; i++) ws.Cell(r, i + 2).Value = result.ColumnKeys[i];
                ws.Cell(r, result.ColumnKeys.Count + 2).Value = "Total";
            }
            else
            {
                ws.Cell(r, 2).Value = result.MeasureLabel;
            }
            ws.Range(r, 1, r, result.Pivoted ? result.ColumnKeys.Count + 2 : 2).Style.Font.Bold = true;
            r++;

            foreach (var row in result.Rows)
            {
                ws.Cell(r, 1).Value = row.Key;
                if (result.Pivoted)
                {
                    for (var i = 0; i < row.Cells.Count; i++)
                    {
                        ws.Cell(r, i + 2).Value = row.Cells[i];
                        ws.Cell(r, i + 2).Style.NumberFormat.Format = "#,##0.00";
                    }
                    ws.Cell(r, result.ColumnKeys.Count + 2).Value = row.Total;
                    ws.Cell(r, result.ColumnKeys.Count + 2).Style.NumberFormat.Format = "#,##0.00";
                }
                else
                {
                    ws.Cell(r, 2).Value = row.Total;
                    ws.Cell(r, 2).Style.NumberFormat.Format = "#,##0.00";
                }
                r++;
            }

            ws.Cell(r, 1).Value = result.IsNet ? "Net (Revenue - Expenses)" : "Grand Total";
            ws.Cell(r, 1).Style.Font.Bold = true;
            if (result.Pivoted)
            {
                for (var i = 0; i < result.ColumnTotals.Count; i++)
                {
                    ws.Cell(r, i + 2).Value = result.ColumnTotals[i];
                    ws.Cell(r, i + 2).Style.NumberFormat.Format = "#,##0.00";
                }
                ws.Cell(r, result.ColumnKeys.Count + 2).Value = result.GrandTotal;
                ws.Cell(r, result.ColumnKeys.Count + 2).Style.NumberFormat.Format = "#,##0.00";
            }
            else
            {
                ws.Cell(r, 2).Value = result.GrandTotal;
                ws.Cell(r, 2).Style.NumberFormat.Format = "#,##0.00";
            }
            ws.Range(r, 1, r, result.Pivoted ? result.ColumnKeys.Count + 2 : 2).Style.Font.Bold = true;
            ws.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            var bytes = stream.ToArray();
            var fileName = $"CustomReport_{rowDim}_{selectedYear}_{entityLabel}.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        private async Task<List<SelectListItem>> BuildCategoryOptions(string? selected)
        {
            var cats = await _db.Categories.AsNoTracking()
                .Where(c => c.CategoryCode != null)
                .Select(c => c.CategoryCode!)
                .Distinct().OrderBy(c => c).ToListAsync();
            var list = new List<SelectListItem> { new SelectListItem("All Categories", "", string.IsNullOrEmpty(selected)) };
            list.AddRange(cats.Select(c => new SelectListItem(c, c, c == selected)));
            return list;
        }

        private async Task<List<string>> BuildCategoryCodes()
        {
            return await _db.Categories.AsNoTracking()
                .Where(c => c.CategoryCode != null)
                .Select(c => c.CategoryCode!)
                .Distinct().OrderBy(c => c).ToListAsync();
        }

        // Applies the category include/exclude filter and the program-type filter in memory.
        private static List<LedgerEntry> ApplyBuilderFilters(
            List<LedgerEntry> rows, string? categoryMode, IReadOnlyCollection<string>? categories, string? programType)
        {
            if (categories != null && categories.Count > 0)
            {
                var set = new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase);
                var exclude = string.Equals(categoryMode, "Exclude", StringComparison.OrdinalIgnoreCase);
                rows = exclude
                    ? rows.Where(r => !set.Contains(r.CategoryCode)).ToList()
                    : rows.Where(r => set.Contains(r.CategoryCode)).ToList();
            }
            if (!string.IsNullOrWhiteSpace(programType))
            {
                rows = rows.Where(r => string.Equals(r.ProgramType, programType, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            return rows;
        }

        // Normalizes incoming category selection: prefers the multi-select list,
        // falls back to the legacy single 'category' value for backward compatibility.
        private static List<string> NormalizeCategories(List<string>? categories, string? legacyCategory)
        {
            var list = (categories ?? new List<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (list.Count == 0 && !string.IsNullOrWhiteSpace(legacyCategory))
                list.Add(legacyCategory.Trim());
            return list;
        }

        // Fully-loaded ("Total") cost basis: overlay the latest Posted allocation run.
        // Adds an allocated-in row to each target (Mandate) program and a contra
        // allocated-out row to each source (Support) program. The pair nets to zero
        // overall, preserving the invariant Sum(Total) == Sum(Direct).
        private async Task<List<LedgerEntry>> ApplyAllocations(List<LedgerEntry> rows, int year, int? entityId)
        {
            List<AllocationTransactions> txns;
            Dictionary<int, Programs> progs;
            try
            {
                var runQuery = _db.AllocationRuns.AsNoTracking()
                    .Where(r => r.BudgetYear == year && r.Status == "Posted");
                if (entityId.HasValue)
                    runQuery = runQuery.Where(r => r.EntityId == null || r.EntityId == entityId.Value);
                var run = await runQuery.OrderByDescending(r => r.RunAt).FirstOrDefaultAsync();
                if (run == null) return rows;

                var tq = _db.AllocationTransactions.AsNoTracking().Where(t => t.RunId == run.RunId);
                if (entityId.HasValue) tq = tq.Where(t => t.EntityId == entityId.Value);
                txns = await tq.ToListAsync();
                if (txns.Count == 0) return rows;

                progs = await _db.Programs.AsNoTracking().ToDictionaryAsync(p => p.ProgramId);
            }
            catch
            {
                // Allocation tables may not exist yet (migration not applied) -> Direct basis only.
                return rows;
            }

            LedgerEntry MakeEntry(int programId, string category, decimal amount, bool allocatedIn)
            {
                progs.TryGetValue(programId, out var p);
                var per = amount / 12m;
                return new LedgerEntry
                {
                    Year = year,
                    EntityId = p?.EntityId ?? entityId ?? 0,
                    CategoryCode = string.IsNullOrWhiteSpace(category) ? "OPEX" : category,
                    ProgramId = programId,
                    ProgramType = p?.ProgramType ?? "Mandate",
                    ProgramCode = p?.ProgramCode ?? "",
                    ProgramName = p?.ProgramName ?? "",
                    ActivityCode = allocatedIn ? "ALLOC-IN" : "ALLOC-OUT",
                    ActivityName = allocatedIn ? "Allocated In" : "Allocated Out",
                    GLType = "Allocated",
                    Amount = amount,
                    Forecast1Amount = amount,
                    Forecast2Amount = amount,
                    M01 = per, M02 = per, M03 = per, M04 = per, M05 = per, M06 = per,
                    M07 = per, M08 = per, M09 = per, M10 = per, M11 = per, M12 = per
                };
            }

            var extra = new List<LedgerEntry>(txns.Count * 2);
            foreach (var t in txns)
            {
                var cat = t.SourceCategoryCode ?? "OPEX";
                extra.Add(MakeEntry(t.TargetProgramId, cat, t.Amount, allocatedIn: true));
                extra.Add(MakeEntry(t.SourceProgramId, cat, -t.Amount, allocatedIn: false));
            }
            rows.AddRange(extra);
            return rows;
        }

        private static ReportBuilderResultVm ComputePivot(List<LedgerEntry> rows, string rowDim, string? colDim, string measure)
        {
            var rowSel = BuilderDimensions[rowDim].Selector;
            var measSel = BuilderMeasures[measure].Selector;
            var isMonth = colDim == MonthColKey;
            var pivoted = !string.IsNullOrWhiteSpace(colDim) && (BuilderDimensions.ContainsKey(colDim) || isMonth);

            // Totals are netted (Revenue - Expenses) for monetary measures, matching the
            // Income Statement convention (CategoryCode == "REVENUE" is income, all else expense).
            // Quantity is a non-monetary count, so it is summed normally.
            var applyNet = !string.Equals(measure, "quantity", StringComparison.OrdinalIgnoreCase);
            static bool IsRevenue(LedgerEntry e) => string.Equals(e.CategoryCode, "REVENUE", StringComparison.OrdinalIgnoreCase);
            decimal Signed(LedgerEntry e) => measSel(e) * (IsRevenue(e) ? 1m : -1m);

            var result = new ReportBuilderResultVm
            {
                HasResult = true,
                Pivoted = pivoted,
                IsNet = applyNet,
                RowDimLabel = BuilderDimensions[rowDim].Label,
                ColDimLabel = isMonth ? "Month" : (pivoted ? BuilderDimensions[colDim!].Label : ""),
                MeasureLabel = BuilderMeasures[measure].Label
            };

            if (!pivoted)
            {
                var grouped = rows
                    .GroupBy(rowSel)
                    .Select(g => new ReportBuilderRowVm { Key = g.Key, Total = g.Sum(measSel) })
                    .OrderByDescending(x => x.Total)
                    .ToList();
                result.Rows = grouped;
                result.GrandTotal = applyNet ? rows.Sum(Signed) : grouped.Sum(x => x.Total);
                return result;
            }

            // Special pivot: months are the 12 budget-distribution columns (M01..M12).
            if (isMonth)
            {
                result.ColumnKeys = MonthColumns.Select(m => m.Label).ToList();
                foreach (var rg in rows.GroupBy(rowSel))
                {
                    var cells = MonthColumns.Select(m => rg.Sum(m.Selector)).ToList();
                    result.Rows.Add(new ReportBuilderRowVm { Key = rg.Key, Cells = cells, Total = cells.Sum() });
                }
                result.Rows = result.Rows.OrderByDescending(x => x.Total).ToList();
                // Monthly columns are the budget distribution (monetary) -> net the totals.
                result.IsNet = true;
                result.ColumnTotals = Enumerable.Range(0, MonthColumns.Length)
                    .Select(i => rows.Sum(e => MonthColumns[i].Selector(e) * (IsRevenue(e) ? 1m : -1m))).ToList();
                result.GrandTotal = result.ColumnTotals.Sum();
                return result;
            }

            var colSel = BuilderDimensions[colDim!].Selector;
            var colKeys = rows.Select(colSel).Distinct().OrderBy(x => x).ToList();
            result.ColumnKeys = colKeys;

            var byRow = rows.GroupBy(rowSel);
            foreach (var rg in byRow)
            {
                var cellMap = rg.GroupBy(colSel).ToDictionary(g => g.Key, g => g.Sum(measSel));
                var cells = colKeys.Select(k => cellMap.TryGetValue(k, out var v) ? v : 0m).ToList();
                result.Rows.Add(new ReportBuilderRowVm { Key = rg.Key, Cells = cells, Total = cells.Sum() });
            }
            result.Rows = result.Rows.OrderByDescending(x => x.Total).ToList();
            if (applyNet)
            {
                var netByCol = rows.GroupBy(colSel).ToDictionary(g => g.Key, g => g.Sum(Signed));
                result.ColumnTotals = colKeys.Select(k => netByCol.TryGetValue(k, out var v) ? v : 0m).ToList();
                result.GrandTotal = rows.Sum(Signed);
            }
            else
            {
                result.ColumnTotals = colKeys.Select((k, i) => result.Rows.Sum(row => row.Cells[i])).ToList();
                result.GrandTotal = result.Rows.Sum(x => x.Total);
            }
            return result;
        }

        private static string BuildChartJson(ReportBuilderResultVm result, string chartType)
        {
            // Cap to top 25 rows for readable charts.
            var rows = result.Rows.Take(25).ToList();
            var labels = rows.Select(r => r.Key).ToList();
            var palette = new[] { "#4e79a7", "#f28e2b", "#e15759", "#76b7b2", "#59a14f", "#edc948", "#b07aa1", "#ff9da7", "#9c755f", "#bab0ac" };

            object data;
            if (result.Pivoted && result.ColumnKeys.Count > 0)
            {
                var datasets = result.ColumnKeys.Select((ck, i) => (object)new
                {
                    label = ck,
                    data = rows.Select(r => r.Cells[i]).ToList(),
                    backgroundColor = palette[i % palette.Length]
                }).ToList();
                data = new { labels, datasets };
            }
            else
            {
                data = new
                {
                    labels,
                    datasets = new[]
                    {
                        new
                        {
                            label = result.MeasureLabel,
                            data = rows.Select(r => r.Total).ToList(),
                            backgroundColor = (chartType == "pie") ? (object)labels.Select((l, i) => palette[i % palette.Length]).ToList() : palette[0]
                        }
                    }
                };
            }
            return JsonSerializer.Serialize(new { type = chartType, data });
        }

        private static string NormalizeReport(string? report)
        {
            var r = (report ?? "income").Trim().ToLowerInvariant();
            return r switch
            {
                "income" => "income",
                "gl" => "gl",
                "projects" => "projects",
                "activities" => "activities",
                "activitiesalloc" => "activitiesalloc",
                "hralloc" => "hralloc",
                "hrrate" => "hrrate",
                "entitysummary" => "entitysummary",
                "trend" => "trend",
                _ => "income"
            };
        }

        private int? GetEntityClaimId()
        {
            var entityClaim = User.Claims.FirstOrDefault(c => c.Type == "EntityId")?.Value;
            if (!int.TryParse(entityClaim, out var entityId) || entityId <= 0)
            {
                return null;
            }

            return entityId;
        }

        private static bool IsGlobalAdmin(bool isAdmin, bool isSysAdmin, int? scopedEntityId = null)
        {
            if (isSysAdmin) return true;
            if (!isAdmin) return false;
            if (scopedEntityId.HasValue) return false;
            return true;
        }

        private int? ResolveEntityScope(bool isAdminLike, bool isGlobalAdmin, int? requestedEntityId)
        {
            if (isAdminLike)
            {
                if (isGlobalAdmin)
                {
                    if (requestedEntityId.HasValue && requestedEntityId.Value > 0)
                    {
                        return requestedEntityId.Value;
                    }
                    return null;
                }

                var scoped = GetEntityClaimId();
                return scoped.HasValue && scoped.Value > 0 ? scoped.Value : -1;
            }

            var entityId = GetEntityClaimId();
            return entityId.HasValue && entityId.Value > 0 ? entityId.Value : -1;
        }

        private async Task<string> GetEntityLabel(int? entityId)
        {
            if (!entityId.HasValue)
            {
                return "AllEntities";
            }

            var code = await _db.Entities
                .AsNoTracking()
                .Where(e => e.EntityId == entityId.Value)
                .Select(e => e.EntityCode)
                .FirstOrDefaultAsync();

            if (string.IsNullOrWhiteSpace(code))
            {
                return $"Entity{entityId.Value}";
            }

            return new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray());
        }

        private enum HrLedgerMode
        {
            None = 0,
            ImportedOnly = 1,
            AllocatedOnly = 2
        }

        private static int CategorySortKey(string? categoryCode)
        {
            var code = categoryCode?.Trim().ToUpperInvariant();
            return code switch
            {
                "REVENUE" => 1,
                "HR" => 2,
                "CAPEX" => 3,
                "OPEX" => 4,
                _ => 99
            };
        }

        private async Task<List<SelectListItem>> BuildEntityOptions(bool isGlobalAdmin, int? effectiveEntityId)
        {
            var entitiesQuery = _db.Entities.AsNoTracking().OrderBy(e => e.EntityCode).AsQueryable();
            if (!isGlobalAdmin && effectiveEntityId.HasValue)
            {
                entitiesQuery = entitiesQuery.Where(e => e.EntityId == effectiveEntityId.Value);
            }
            var entities = await entitiesQuery
                .Select(e => new SelectListItem(e.EntityCode + " - " + e.EntityName, e.EntityId.ToString()))
                .ToListAsync();

            var options = new List<SelectListItem>();
            if (isGlobalAdmin) options.Add(new SelectListItem("All Entities", "", !effectiveEntityId.HasValue));
            options.AddRange(entities);
            foreach (var opt in options)
            {
                if (effectiveEntityId.HasValue && opt.Value == effectiveEntityId.Value.ToString()) opt.Selected = true;
            }
            return options;
        }

        private async Task<List<LedgerEntry>> GetLedgerEntries(int year, int? entityId, HrLedgerMode hrMode)
        {
            var budgetLinesQuery =
                from b in _db.BudgetLines.AsNoTracking()
                join cat in _db.Categories.AsNoTracking() on b.CategoryId equals cat.CategoryId
                join item in _db.Items.AsNoTracking() on b.ItemId equals item.ItemId
                join gl in _db.GLAccounts.AsNoTracking() on item.GLAccountId equals gl.GLAccountId
                join act in _db.Activities.AsNoTracking() on b.ActivityId equals act.ActivityId into actJoin
                from act in actJoin.DefaultIfEmpty()
                join prog in _db.Programs.AsNoTracking() on (b.ProgramId ?? act.ProgramId) equals prog.ProgramId into progJoin
                from prog in progJoin.DefaultIfEmpty()
                join proj in _db.Projects.AsNoTracking() on b.ProjectId equals proj.ProjectId into projJoin
                from proj in projJoin.DefaultIfEmpty()
                where b.BudgetYear == year
                    // HR is sourced exclusively from the HR tables (imported or allocated) below,
                    // so exclude any HR-categorised budget line to avoid double-counting HR cost.
                    && cat.CategoryCode != "HR"
                select new LedgerEntry
                {
                    BudgetLineId = b.BudgetLineId,
                    Year = year,
                    EntityId = b.EntityId,
                    DepartmentId = b.DepartmentId,
                    CategoryCode = cat.CategoryCode,
                    ItemId = item.ItemId,
                    ItemCode = item.ItemCode,
                    ItemName = item.ItemName,
                    ProgramId = prog != null ? prog.ProgramId : 0,
                    ProgramType = prog != null ? prog.ProgramType : "Mandate",
                    ProgramCode = prog != null ? prog.ProgramCode : "",
                    ProgramName = prog != null ? prog.ProgramName : "",
                    ActivityId = act != null ? act.ActivityId : 0,
                    ActivityCode = act != null ? act.ActivityCode : "",
                    ActivityName = act != null ? act.ActivityName : "",
                    ProjectId = b.ProjectId,
                    ProjectCode = proj != null ? proj.ProjectCode : "",
                    ProjectName = proj != null ? proj.ProjectName : "",
                    GLCode = gl.GLCode,
                    GLName = gl.GLName,
                    GLType = gl.GLType,
                    Quantity = b.Quantity,
                    UnitPrice = b.UnitPrice,
                    Amount = b.Amount,
                    Forecast1Amount = b.F1_Amount,
                    Forecast2Amount = b.F2_Amount,
                    M01 = b.M01, M02 = b.M02, M03 = b.M03, M04 = b.M04, M05 = b.M05, M06 = b.M06,
                    M07 = b.M07, M08 = b.M08, M09 = b.M09, M10 = b.M10, M11 = b.M11, M12 = b.M12
                };

            IQueryable<LedgerEntry> combined = budgetLinesQuery;

            var hrAllocatedQuery =
                from a in _db.HrEmployeeCostAllocations.AsNoTracking()
                join emp in _db.HrEmployeeCosts.AsNoTracking() on a.EmployeeCostId equals emp.EmployeeCostId
                join act in _db.Activities.AsNoTracking() on a.ActivityId equals act.ActivityId
                join prog in _db.Programs.AsNoTracking() on act.ProgramId equals prog.ProgramId
                join proj in _db.Projects.AsNoTracking() on a.ProjectId equals proj.ProjectId into projJoin
                from proj in projJoin.DefaultIfEmpty()
                join gl in _db.GLAccounts.AsNoTracking() on emp.GLCode equals gl.GLCode into glJoin
                from gl in glJoin.DefaultIfEmpty()
                where emp.BudgetYear == year
                select new LedgerEntry
                {
                    BudgetLineId = null,
                    Year = year,
                    EntityId = emp.EntityId ?? 0,
                    DepartmentId = act.DepartmentId,
                    CategoryCode = "HR",
                    ItemId = null,
                    ItemCode = "",
                    ItemName = "",
                    GLType = gl != null ? gl.GLType : "",
                    ProgramId = prog.ProgramId,
                    ProgramType = prog.ProgramType,
                    ProgramCode = prog.ProgramCode,
                    ProgramName = prog.ProgramName,
                    ActivityId = act.ActivityId,
                    ActivityCode = act.ActivityCode,
                    ActivityName = act.ActivityName,
                    ProjectId = a.ProjectId,
                    ProjectCode = proj != null ? proj.ProjectCode : "",
                    ProjectName = proj != null ? proj.ProjectName : "",
                    GLCode = emp.GLCode,
                    GLName = gl != null ? gl.GLName : "",
                    Quantity = 0m,
                    UnitPrice = 0m,
                    Amount = a.AllocatedAmount,
                    Forecast1Amount = a.AllocatedAmount,
                    Forecast2Amount = a.AllocatedAmount,
                    M01 = a.AllocatedAmount / 12m, M02 = a.AllocatedAmount / 12m, M03 = a.AllocatedAmount / 12m,
                    M04 = a.AllocatedAmount / 12m, M05 = a.AllocatedAmount / 12m, M06 = a.AllocatedAmount / 12m,
                    M07 = a.AllocatedAmount / 12m, M08 = a.AllocatedAmount / 12m, M09 = a.AllocatedAmount / 12m,
                    M10 = a.AllocatedAmount / 12m, M11 = a.AllocatedAmount / 12m, M12 = a.AllocatedAmount / 12m
                };

            var hrImportedQuery =
                from emp in _db.HrEmployeeCosts.AsNoTracking()
                join gl in _db.GLAccounts.AsNoTracking() on emp.GLCode equals gl.GLCode into glJoin
                from gl in glJoin.DefaultIfEmpty()
                where emp.BudgetYear == year
                group new { emp, gl } by new
                {
                    EntityId = emp.EntityId ?? 0,
                    DepartmentId = emp.DepartmentId ?? 0,
                    emp.GLCode,
                    GLName = gl != null ? gl.GLName : "",
                    GLType = gl != null ? gl.GLType : ""
                }
                into g
                select new LedgerEntry
                {
                    BudgetLineId = null,
                    Year = year,
                    EntityId = g.Key.EntityId,
                    DepartmentId = g.Key.DepartmentId,
                    CategoryCode = "HR",
                    ItemId = null,
                    ItemCode = "",
                    ItemName = "",
                    GLType = g.Key.GLType,
                    ProgramId = 0,
                    ProgramType = "Mandate",
                    ProgramCode = "HR",
                    ProgramName = "HR",
                    ActivityId = 0,
                    ActivityCode = "IMPORTED",
                    ActivityName = "Imported",
                    ProjectId = null,
                    ProjectCode = "",
                    ProjectName = "",
                    GLCode = g.Key.GLCode,
                    GLName = g.Key.GLName,
                    Quantity = 0m,
                    UnitPrice = 0m,
                    Amount = g.Sum(x => x.emp.AnnualCost),
                    Forecast1Amount = g.Sum(x => x.emp.AnnualCost),
                    Forecast2Amount = g.Sum(x => x.emp.AnnualCost),
                    M01 = g.Sum(x => x.emp.AnnualCost) / 12m, M02 = g.Sum(x => x.emp.AnnualCost) / 12m,
                    M03 = g.Sum(x => x.emp.AnnualCost) / 12m, M04 = g.Sum(x => x.emp.AnnualCost) / 12m,
                    M05 = g.Sum(x => x.emp.AnnualCost) / 12m, M06 = g.Sum(x => x.emp.AnnualCost) / 12m,
                    M07 = g.Sum(x => x.emp.AnnualCost) / 12m, M08 = g.Sum(x => x.emp.AnnualCost) / 12m,
                    M09 = g.Sum(x => x.emp.AnnualCost) / 12m, M10 = g.Sum(x => x.emp.AnnualCost) / 12m,
                    M11 = g.Sum(x => x.emp.AnnualCost) / 12m, M12 = g.Sum(x => x.emp.AnnualCost) / 12m
                };

            if (hrMode == HrLedgerMode.AllocatedOnly)
            {
                combined = combined.Concat(hrAllocatedQuery);
            }
            else if (hrMode == HrLedgerMode.ImportedOnly)
            {
                combined = combined.Concat(hrImportedQuery);
            }

            if (entityId.HasValue)
            {
                combined = combined.Where(x => x.EntityId == entityId.Value);
            }

            var rows = await combined.ToListAsync();

            var entityIds = rows.Select(x => x.EntityId).Where(id => id > 0).Distinct().ToList();
            if (entityIds.Count > 0)
            {
                var entities = await _db.Entities.AsNoTracking()
                    .Where(e => entityIds.Contains(e.EntityId))
                    .Select(e => new { e.EntityId, e.EntityCode, e.EntityName })
                    .ToListAsync();
                var map = entities.ToDictionary(e => e.EntityId);

                foreach (var r in rows)
                {
                    if (r.EntityId > 0 && map.TryGetValue(r.EntityId, out var e))
                    {
                        r.EntityCode = e.EntityCode ?? "";
                        r.EntityName = e.EntityName ?? "";
                    }
                }
            }

            // Mid-Year Actual (H1) is captured at entity x GL grain; allocate it down to
            // each line proportionally to that line's budget so it aggregates correctly at any grouping.
            if (entityIds.Count > 0)
            {
                var actuals = await _db.MidYearGlActualForecasts.AsNoTracking()
                    .Where(m => m.BudgetYear == year && entityIds.Contains(m.EntityId))
                    .GroupBy(m => new { m.EntityId, m.GLCode })
                    .Select(g => new { g.Key.EntityId, g.Key.GLCode, Actual = g.Sum(x => x.ActualH1Amount) })
                    .ToListAsync();

                if (actuals.Count > 0)
                {
                    var actualMap = actuals.ToDictionary(a => (a.EntityId, a.GLCode ?? ""), a => a.Actual);
                    foreach (var grp in rows.Where(r => !string.IsNullOrEmpty(r.GLCode))
                                            .GroupBy(r => (r.EntityId, r.GLCode)))
                    {
                        if (!actualMap.TryGetValue(grp.Key, out var actual) || actual == 0m) continue;
                        var sumBudget = grp.Sum(r => r.Amount);
                        var members = grp.ToList();
                        if (sumBudget != 0m)
                        {
                            foreach (var r in members) r.ActualH1Amount = actual * (r.Amount / sumBudget);
                        }
                        else
                        {
                            var share = actual / members.Count;
                            foreach (var r in members) r.ActualH1Amount = share;
                        }
                    }
                }
            }

            rows = rows
                .OrderBy(x => CategorySortKey(x.CategoryCode))
                .ThenBy(x => x.GLCode)
                .ThenBy(x => x.ProgramCode)
                .ThenBy(x => x.ActivityCode)
                .ToList();

            var scenario = await GetActiveScenario(year);
            if (scenario != null)
            {
                ApplyScenario(rows, scenario);
            }

            return rows;
        }

        private async Task<ActiveScenario?> GetActiveScenario(int year)
        {
            var scenarioId = HttpContext.Session.GetInt("ctxScenarioId");
            if (!scenarioId.HasValue || scenarioId.Value <= 0) return null;

            var scenario = await _db.WhatIfScenarios.AsNoTracking()
                .Include(s => s.WhatIfScenarioDefaults)
                .FirstOrDefaultAsync(s => s.ScenarioId == scenarioId.Value && s.BudgetYear == year && s.IsActive);
            if (scenario == null) return null;

            var defaults = scenario.WhatIfScenarioDefaults ?? new WhatIfScenarioDefaults
            {
                ScenarioId = scenario.ScenarioId,
                CostInflationRate = 0,
                RevenueGrowthRate = 0
            };

            var projectRates = await _db.WhatIfScenarioProjectRates.AsNoTracking()
                .Where(r => r.ScenarioId == scenario.ScenarioId)
                .ToDictionaryAsync(r => r.ProjectId, r => r);

            return new ActiveScenario
            {
                ScenarioId = scenario.ScenarioId,
                ScenarioName = scenario.ScenarioName,
                BudgetYear = scenario.BudgetYear,
                EntityId = scenario.EntityId,
                DepartmentId = scenario.DepartmentId,
                CostInflationRate = defaults.CostInflationRate,
                RevenueGrowthRate = defaults.RevenueGrowthRate,
                ProjectRates = projectRates
            };
        }

        private static void ApplyScenario(List<LedgerEntry> rows, ActiveScenario scenario)
        {
            foreach (var r in rows)
            {
                if (scenario.EntityId.HasValue && r.EntityId != scenario.EntityId.Value) continue;
                if (scenario.DepartmentId.HasValue && r.DepartmentId != scenario.DepartmentId.Value) continue;

                if (string.Equals(r.CategoryCode, "HR", StringComparison.OrdinalIgnoreCase)) continue;

                var costRate = scenario.CostInflationRate;
                var revRate = scenario.RevenueGrowthRate;

                if (r.ProjectId.HasValue && scenario.ProjectRates.TryGetValue(r.ProjectId.Value, out var pr))
                {
                    if (pr.CostInflationRate.HasValue) costRate = pr.CostInflationRate.Value;
                    if (pr.RevenueGrowthRate.HasValue) revRate = pr.RevenueGrowthRate.Value;
                }

                decimal rateToApply;
                if (string.Equals(r.GLType, "REVENUE", StringComparison.OrdinalIgnoreCase))
                {
                    rateToApply = revRate;
                }
                else if (!string.IsNullOrWhiteSpace(r.GLType))
                {
                    rateToApply = costRate;
                }
                else
                {
                    if (string.Equals(r.CategoryCode, "REVENUE", StringComparison.OrdinalIgnoreCase))
                    {
                        rateToApply = revRate;
                    }
                    else if (string.Equals(r.CategoryCode, "OPEX", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(r.CategoryCode, "CAPEX", StringComparison.OrdinalIgnoreCase))
                    {
                        rateToApply = costRate;
                    }
                    else
                    {
                        continue;
                    }
                }

                var multiplier = 1m + (rateToApply / 100m);
                if (r.BudgetLineId.HasValue)
                {
                    r.UnitPrice = Math.Round(r.UnitPrice * multiplier, 2, MidpointRounding.AwayFromZero);
                    r.Amount = Math.Round(r.Quantity * r.UnitPrice, 2, MidpointRounding.AwayFromZero);
                    r.Forecast1Amount = Math.Round(r.Forecast1Amount * multiplier, 2, MidpointRounding.AwayFromZero);
                    r.Forecast2Amount = Math.Round(r.Forecast2Amount * multiplier, 2, MidpointRounding.AwayFromZero);
                }
                else
                {
                    r.Amount = Math.Round(r.Amount * multiplier, 2, MidpointRounding.AwayFromZero);
                    r.Forecast1Amount = Math.Round(r.Forecast1Amount * multiplier, 2, MidpointRounding.AwayFromZero);
                    r.Forecast2Amount = Math.Round(r.Forecast2Amount * multiplier, 2, MidpointRounding.AwayFromZero);
                }
            }
        }

        private static void ApplyHeaderStyle(IXLRange range)
        {
            range.Style.Font.Bold = true;
            range.Style.Fill.BackgroundColor = XLColor.FromHtml("#f1f3f5");
            range.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        }

        private static void BuildIncomeWorksheet(XLWorkbook wb, IncomeStatementVm vm, int year, string entityLabel)
        {
            var ws = wb.Worksheets.Add("Income Statement");
            ws.Cell(1, 1).Value = "Income Statement (Program → Activity → GL)";
            ws.Cell(2, 1).Value = $"Year: {year}    Entity: {entityLabel}";

            var includeEntity = vm.Lines.Select(x => x.EntityId).Distinct().Count() > 1;
            var colCount = includeEntity ? 10 : 8;

            ws.Range(1, 1, 1, colCount).Merge().Style.Font.Bold = true;
            ws.Range(1, 1, 1, colCount).Style.Font.FontSize = 14;
            ws.Range(2, 1, 2, colCount).Merge().Style.Font.Bold = true;

            var row = 4;
            var c = 1;
            if (includeEntity)
            {
                ws.Cell(row, c++).Value = "Entity";
                ws.Cell(row, c++).Value = "Entity Name";
            }
            ws.Cell(row, c++).Value = "Program";
            ws.Cell(row, c++).Value = "Activity";
            ws.Cell(row, c++).Value = "GL";
            ws.Cell(row, c++).Value = "GL Name";
            ws.Cell(row, c++).Value = "Type";
            ws.Cell(row, c++).Value = "Amount";
            ws.Cell(row, c++).Value = "Forecast 1";
            ws.Cell(row, c++).Value = "Forecast 2";
            ApplyHeaderStyle(ws.Range(row, 1, row, colCount));

            row++;
            foreach (var r in vm.Lines.OrderBy(x => x.EntityCode).ThenBy(x => x.ProgramCode).ThenBy(x => x.ActivityCode).ThenBy(x => CategorySortKey(x.CategoryCode)).ThenBy(x => x.GLCode))
            {
                var dc = 1;
                if (includeEntity)
                {
                    ws.Cell(row, dc++).Value = r.EntityCode;
                    ws.Cell(row, dc++).Value = r.EntityName;
                }
                ws.Cell(row, dc++).Value = string.IsNullOrWhiteSpace(r.ProgramCode) ? "" : (r.ProgramCode + " - " + r.ProgramName);
                ws.Cell(row, dc++).Value = string.IsNullOrWhiteSpace(r.ActivityCode) ? "" : (r.ActivityCode + " - " + r.ActivityName);
                ws.Cell(row, dc++).Value = r.GLCode;
                ws.Cell(row, dc++).Value = r.GLName;
                ws.Cell(row, dc++).Value = r.CategoryCode;
                ws.Cell(row, dc++).Value = r.Amount;
                ws.Cell(row, dc++).Value = r.Forecast1Amount;
                ws.Cell(row, dc++).Value = r.Forecast2Amount;

                var amountColStart = includeEntity ? 8 : 6;
                ws.Range(row, amountColStart, row, amountColStart + 2).Style.NumberFormat.Format = "#,##0.00";
                row++;
            }

            row += 2;
            var labelCol = includeEntity ? 7 : 5;
            var totalsStart = includeEntity ? 8 : 6;
            var totalsEnd = includeEntity ? 10 : 8;

            ws.Cell(row, labelCol).Value = "Total Revenue";
            ws.Cell(row, totalsStart).Value = vm.TotalRevenue;
            ws.Cell(row, totalsStart + 1).Value = vm.Forecast1TotalRevenue;
            ws.Cell(row, totalsStart + 2).Value = vm.Forecast2TotalRevenue;
            ws.Range(row, totalsStart, row, totalsEnd).Style.NumberFormat.Format = "#,##0.00";
            row++;

            ws.Cell(row, labelCol).Value = "Total HR";
            ws.Cell(row, totalsStart).Value = vm.TotalHr;
            ws.Cell(row, totalsStart + 1).Value = vm.Forecast1TotalHr;
            ws.Cell(row, totalsStart + 2).Value = vm.Forecast2TotalHr;
            ws.Range(row, totalsStart, row, totalsEnd).Style.NumberFormat.Format = "#,##0.00";
            row++;

            ws.Cell(row, labelCol).Value = "Total CAPEX";
            ws.Cell(row, totalsStart).Value = vm.TotalCapex;
            ws.Cell(row, totalsStart + 1).Value = vm.Forecast1TotalCapex;
            ws.Cell(row, totalsStart + 2).Value = vm.Forecast2TotalCapex;
            ws.Range(row, totalsStart, row, totalsEnd).Style.NumberFormat.Format = "#,##0.00";
            row++;

            ws.Cell(row, labelCol).Value = "Total OPEX";
            ws.Cell(row, totalsStart).Value = vm.TotalOpex;
            ws.Cell(row, totalsStart + 1).Value = vm.Forecast1TotalOpex;
            ws.Cell(row, totalsStart + 2).Value = vm.Forecast2TotalOpex;
            ws.Range(row, totalsStart, row, totalsEnd).Style.NumberFormat.Format = "#,##0.00";
            row++;

            ws.Cell(row, labelCol).Value = "Total Expenditures";
            ws.Cell(row, totalsStart).Value = vm.TotalExpense;
            ws.Cell(row, totalsStart + 1).Value = vm.Forecast1TotalExpense;
            ws.Cell(row, totalsStart + 2).Value = vm.Forecast2TotalExpense;
            ws.Range(row, totalsStart, row, totalsEnd).Style.NumberFormat.Format = "#,##0.00";
            row++;

            ws.Cell(row, labelCol).Value = "Net";
            ws.Cell(row, totalsStart).Value = vm.SurplusDeficit;
            ws.Cell(row, totalsStart + 1).Value = vm.Forecast1SurplusDeficit;
            ws.Cell(row, totalsStart + 2).Value = vm.Forecast2SurplusDeficit;
            ws.Range(row, totalsStart, row, totalsEnd).Style.NumberFormat.Format = "#,##0.00";
            ws.Range(row - 5, labelCol, row, totalsEnd).Style.Font.Bold = true;

            ws.Columns(1, colCount).AdjustToContents();
        }

        private static void BuildGlWorksheet(XLWorkbook wb, List<GlSummaryRowVm> rows, int year, string entityLabel)
        {
            var ws = wb.Worksheets.Add("GL View");
            ws.Cell(1, 1).Value = "GL View";
            ws.Cell(2, 1).Value = $"Year: {year}    Entity: {entityLabel}";
            var includeEntity = rows.Select(x => x.EntityCode).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Count() > 1;
            var colCount = includeEntity ? 9 : 7;
            ws.Range(1, 1, 1, colCount).Merge().Style.Font.Bold = true;
            ws.Range(1, 1, 1, colCount).Style.Font.FontSize = 14;
            ws.Range(2, 1, 2, colCount).Merge().Style.Font.Bold = true;

            var row = 4;
            var c = 1;
            if (includeEntity)
            {
                ws.Cell(row, c++).Value = "Entity";
                ws.Cell(row, c++).Value = "Entity Name";
            }
            ws.Cell(row, c++).Value = "GL";
            ws.Cell(row, c++).Value = "GL Name";
            ws.Cell(row, c++).Value = "Revenue";
            ws.Cell(row, c++).Value = "HR";
            ws.Cell(row, c++).Value = "CAPEX";
            ws.Cell(row, c++).Value = "OPEX";
            ws.Cell(row, c++).Value = "Net";
            ApplyHeaderStyle(ws.Range(row, 1, row, colCount));

            row++;
            foreach (var r in rows)
            {
                var dc = 1;
                if (includeEntity)
                {
                    ws.Cell(row, dc++).Value = r.EntityCode;
                    ws.Cell(row, dc++).Value = r.EntityName;
                }
                ws.Cell(row, dc++).Value = r.GLCode;
                ws.Cell(row, dc++).Value = r.GLName;
                ws.Cell(row, dc++).Value = r.Revenue;
                ws.Cell(row, dc++).Value = r.Hr;
                ws.Cell(row, dc++).Value = r.Capex;
                ws.Cell(row, dc++).Value = r.Opex;
                ws.Cell(row, dc++).Value = r.Net;
                var amountsStart = includeEntity ? 5 : 3;
                ws.Range(row, amountsStart, row, amountsStart + 4).Style.NumberFormat.Format = "#,##0.00";
                row++;
            }

            if (rows.Count > 0)
            {
                var totalRevenue = rows.Sum(x => x.Revenue);
                var totalHr = rows.Sum(x => x.Hr);
                var totalCapex = rows.Sum(x => x.Capex);
                var totalOpex = rows.Sum(x => x.Opex);
                var totalNet = rows.Sum(x => x.Net);

                if (includeEntity)
                {
                    ws.Cell(row, 3).Value = "Total (All Entities)";
                    ws.Cell(row, 5).Value = totalRevenue;
                    ws.Cell(row, 6).Value = totalHr;
                    ws.Cell(row, 7).Value = totalCapex;
                    ws.Cell(row, 8).Value = totalOpex;
                    ws.Cell(row, 9).Value = totalNet;
                    ws.Range(row, 3, row, 9).Style.Font.Bold = true;
                    ws.Range(row, 5, row, 9).Style.NumberFormat.Format = "#,##0.00";
                }
                else
                {
                    ws.Cell(row, 2).Value = "Total";
                    ws.Cell(row, 3).Value = totalRevenue;
                    ws.Cell(row, 4).Value = totalHr;
                    ws.Cell(row, 5).Value = totalCapex;
                    ws.Cell(row, 6).Value = totalOpex;
                    ws.Cell(row, 7).Value = totalNet;
                    ws.Range(row, 2, row, 7).Style.Font.Bold = true;
                    ws.Range(row, 3, row, 7).Style.NumberFormat.Format = "#,##0.00";
                }
            }

            ws.Columns(1, colCount).AdjustToContents();
        }

        private static void BuildProjectsWorksheet(XLWorkbook wb, List<ProjectCostRowVm> rows, int year, string entityLabel)
        {
            var ws = wb.Worksheets.Add("Project Costs");
            ws.Cell(1, 1).Value = "Project Costs";
            ws.Cell(2, 1).Value = $"Year: {year}    Entity: {entityLabel}";
            var includeEntity = rows.Select(x => x.EntityCode).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Count() > 1;
            var colCount = includeEntity ? 9 : 7;
            ws.Range(1, 1, 1, colCount).Merge().Style.Font.Bold = true;
            ws.Range(1, 1, 1, colCount).Style.Font.FontSize = 14;
            ws.Range(2, 1, 2, colCount).Merge().Style.Font.Bold = true;

            var row = 4;
            var c = 1;
            if (includeEntity)
            {
                ws.Cell(row, c++).Value = "Entity";
                ws.Cell(row, c++).Value = "Entity Name";
            }
            ws.Cell(row, c++).Value = "Project";
            ws.Cell(row, c++).Value = "Revenue";
            ws.Cell(row, c++).Value = "HR";
            ws.Cell(row, c++).Value = "CAPEX";
            ws.Cell(row, c++).Value = "OPEX";
            ws.Cell(row, c++).Value = "Total Expense";
            ws.Cell(row, c++).Value = "Net";
            ApplyHeaderStyle(ws.Range(row, 1, row, colCount));

            row++;
            foreach (var r in rows)
            {
                var dc = 1;
                if (includeEntity)
                {
                    ws.Cell(row, dc++).Value = r.EntityCode;
                    ws.Cell(row, dc++).Value = r.EntityName;
                }
                ws.Cell(row, dc++).Value = r.ProjectCode + " - " + r.ProjectName;
                ws.Cell(row, dc++).Value = r.Revenue;
                ws.Cell(row, dc++).Value = r.Hr;
                ws.Cell(row, dc++).Value = r.Capex;
                ws.Cell(row, dc++).Value = r.Opex;
                ws.Cell(row, dc++).Value = r.TotalExpense;
                ws.Cell(row, dc++).Value = r.Net;
                var amountsStart = includeEntity ? 4 : 2;
                ws.Range(row, amountsStart, row, amountsStart + 5).Style.NumberFormat.Format = "#,##0.00";
                row++;
            }

            ws.Columns(1, colCount).AdjustToContents();
        }

        private static void BuildActivitiesWorksheet(XLWorkbook wb, List<ActivityCostRowVm> rows, int year, string entityLabel)
        {
            var ws = wb.Worksheets.Add("Activity Costs");
            ws.Cell(1, 1).Value = "Activity Costs";
            ws.Cell(2, 1).Value = $"Year: {year}    Entity: {entityLabel}";
            var includeEntity = rows.Select(x => x.EntityCode).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Count() > 1;
            var colCount = includeEntity ? 12 : 10;
            ws.Range(1, 1, 1, colCount).Merge().Style.Font.Bold = true;
            ws.Range(1, 1, 1, colCount).Style.Font.FontSize = 14;
            ws.Range(2, 1, 2, colCount).Merge().Style.Font.Bold = true;

            var row = 4;
            var c = 1;
            if (includeEntity)
            {
                ws.Cell(row, c++).Value = "Entity";
                ws.Cell(row, c++).Value = "Entity Name";
            }
            ws.Cell(row, c++).Value = "Program";
            ws.Cell(row, c++).Value = "Activity";
            ws.Cell(row, c++).Value = "Revenue";
            ws.Cell(row, c++).Value = "HR";
            ws.Cell(row, c++).Value = "CAPEX";
            ws.Cell(row, c++).Value = "OPEX";
            ws.Cell(row, c++).Value = "Total Expense";
            ws.Cell(row, c++).Value = "Net";
            ws.Cell(row, c++).Value = "Forecast 1 Total";
            ws.Cell(row, c++).Value = "Forecast 2 Total";
            ApplyHeaderStyle(ws.Range(row, 1, row, colCount));

            row++;
            var topGroups = includeEntity
                ? rows.GroupBy(r => new { r.EntityCode, r.EntityName, r.ProgramCode, r.ProgramName })
                    .OrderBy(g => g.Key.EntityCode).ThenBy(g => g.Key.ProgramCode)
                : rows.GroupBy(r => new { EntityCode = "", EntityName = "", r.ProgramCode, r.ProgramName })
                    .OrderBy(g => g.Key.ProgramCode);

            foreach (var group in topGroups)
            {
                var label = string.IsNullOrWhiteSpace(group.Key.ProgramCode) ? "No Program" : (group.Key.ProgramCode + " - " + group.Key.ProgramName);
                var dc = 1;
                if (includeEntity)
                {
                    ws.Cell(row, dc++).Value = group.Key.EntityCode;
                    ws.Cell(row, dc++).Value = group.Key.EntityName;
                }
                ws.Cell(row, dc++).Value = label;
                ws.Cell(row, dc + 1).Value = group.Sum(x => x.Revenue);
                ws.Cell(row, dc + 2).Value = group.Sum(x => x.Hr);
                ws.Cell(row, dc + 3).Value = group.Sum(x => x.Capex);
                ws.Cell(row, dc + 4).Value = group.Sum(x => x.Opex);
                ws.Cell(row, dc + 5).Value = group.Sum(x => x.TotalExpense);
                ws.Cell(row, dc + 6).Value = group.Sum(x => x.Net);
                ws.Cell(row, dc + 7).Value = group.Sum(x => x.Forecast1TotalExpense);
                ws.Cell(row, dc + 8).Value = group.Sum(x => x.Forecast2TotalExpense);

                ws.Range(row, 1, row, colCount).Style.Font.Bold = true;
                var groupAmountsStart = includeEntity ? 5 : 3;
                ws.Range(row, groupAmountsStart, row, groupAmountsStart + 7).Style.NumberFormat.Format = "#,##0.00";
                row++;

                foreach (var r in group.OrderBy(x => x.ActivityCode))
                {
                    var rdc = 1;
                    if (includeEntity)
                    {
                        rdc += 2;
                    }
                    ws.Cell(row, rdc + 1).Value = r.ActivityCode + " - " + r.ActivityName;
                    ws.Cell(row, rdc + 2).Value = r.Revenue;
                    ws.Cell(row, rdc + 3).Value = r.Hr;
                    ws.Cell(row, rdc + 4).Value = r.Capex;
                    ws.Cell(row, rdc + 5).Value = r.Opex;
                    ws.Cell(row, rdc + 6).Value = r.TotalExpense;
                    ws.Cell(row, rdc + 7).Value = r.Net;
                    ws.Cell(row, rdc + 8).Value = r.Forecast1TotalExpense;
                    ws.Cell(row, rdc + 9).Value = r.Forecast2TotalExpense;
                    var detailAmountsStart = includeEntity ? 5 : 3;
                    ws.Range(row, detailAmountsStart, row, detailAmountsStart + 7).Style.NumberFormat.Format = "#,##0.00";
                    row++;
                }
            }

            ws.Columns(1, colCount).AdjustToContents();
        }

        // Flat list of underlying cost transactions (OPEX/CAPEX/Revenue budget lines + HR allocations),
        // each tagged with its entity/program/activity/project, for the detail export worksheets.
        private async Task<List<CostTxnExportRow>> GetCostTransactionRows(int year, int? entityId)
        {
            var blBase = _db.BudgetLines.AsNoTracking().Where(b => b.BudgetYear == year);
            if (entityId.HasValue) blBase = blBase.Where(b => b.EntityId == entityId.Value);

            var budgetLines = await (
                from b in blBase
                join cat in _db.Categories.AsNoTracking() on b.CategoryId equals cat.CategoryId
                join item in _db.Items.AsNoTracking() on b.ItemId equals item.ItemId
                join gl in _db.GLAccounts.AsNoTracking() on item.GLAccountId equals gl.GLAccountId
                join act in _db.Activities.AsNoTracking() on b.ActivityId equals act.ActivityId into actJoin
                from act in actJoin.DefaultIfEmpty()
                join prog in _db.Programs.AsNoTracking() on (b.ProgramId ?? act.ProgramId) equals prog.ProgramId into progJoin
                from prog in progJoin.DefaultIfEmpty()
                join proj in _db.Projects.AsNoTracking() on b.ProjectId equals proj.ProjectId into projJoin
                from proj in projJoin.DefaultIfEmpty()
                join ent in _db.Entities.AsNoTracking() on b.EntityId equals ent.EntityId into entJoin
                from ent in entJoin.DefaultIfEmpty()
                where cat.CategoryCode != "HR"
                select new CostTxnExportRow
                {
                    EntityCode = ent != null ? ent.EntityCode : "",
                    EntityName = ent != null ? ent.EntityName : "",
                    ProgramCode = prog != null ? prog.ProgramCode : "",
                    ProgramName = prog != null ? prog.ProgramName : "",
                    ActivityCode = act != null ? act.ActivityCode : "",
                    ActivityName = act != null ? act.ActivityName : "",
                    ProjectCode = proj != null ? proj.ProjectCode : "",
                    ProjectName = proj != null ? proj.ProjectName : "",
                    Source = cat.CategoryCode,
                    Description = item.ItemCode + " - " + item.ItemName,
                    GLCode = gl.GLCode,
                    GLName = gl.GLName,
                    Quantity = b.Quantity,
                    UnitPrice = b.UnitPrice,
                    Amount = b.Amount,
                    Forecast1 = b.F1_Amount,
                    Forecast2 = b.F2_Amount
                }
            ).ToListAsync();

            var hrLines = await (
                from a in _db.HrEmployeeCostAllocations.AsNoTracking()
                join emp in _db.HrEmployeeCosts.AsNoTracking() on a.EmployeeCostId equals emp.EmployeeCostId
                join act in _db.Activities.AsNoTracking() on a.ActivityId equals act.ActivityId
                join prog in _db.Programs.AsNoTracking() on act.ProgramId equals prog.ProgramId
                join proj in _db.Projects.AsNoTracking() on a.ProjectId equals proj.ProjectId into projJoin
                from proj in projJoin.DefaultIfEmpty()
                join gl in _db.GLAccounts.AsNoTracking() on emp.GLCode equals gl.GLCode into glJoin
                from gl in glJoin.DefaultIfEmpty()
                where emp.BudgetYear == year && (entityId == null || emp.EntityId == entityId)
                select new CostTxnExportRow
                {
                    EntityCode = "",
                    EntityName = emp.EntityName ?? "",
                    ProgramCode = prog.ProgramCode,
                    ProgramName = prog.ProgramName,
                    ActivityCode = act.ActivityCode,
                    ActivityName = act.ActivityName,
                    ProjectCode = proj != null ? proj.ProjectCode : "",
                    ProjectName = proj != null ? proj.ProjectName : "",
                    Source = "HR",
                    Description = emp.EmployeeId + " - " + emp.EmployeeName,
                    GLCode = emp.GLCode,
                    GLName = gl != null ? gl.GLName : "",
                    Quantity = 0m,
                    UnitPrice = 0m,
                    Amount = a.AllocatedAmount,
                    Forecast1 = a.AllocatedAmount,
                    Forecast2 = a.AllocatedAmount
                }
            ).ToListAsync();

            var all = new List<CostTxnExportRow>(budgetLines.Count + hrLines.Count);
            all.AddRange(budgetLines);
            all.AddRange(hrLines);
            return all;
        }

        // Reallocation postings behind the reported figures. Only the run the reports actually use
        // is listed (latest Posted per entity, falling back to a global run); scenario and superseded
        // runs keep their transactions but must not be added on top of the official ones.
        private async Task<List<ReallocExportRow>> GetReallocationExportRows(int year, int? entityId)
        {
            var posted = await _db.AllocationRuns.AsNoTracking()
                .Where(r => r.BudgetYear == year && r.Status == "Posted"
                    && (entityId == null || r.EntityId == null || r.EntityId == entityId))
                .OrderByDescending(r => r.RunAt)
                .ToListAsync();
            if (posted.Count == 0) return new List<ReallocExportRow>();

            var postedIds = posted.Select(r => r.RunId).ToList();

            var txEntityIds = await _db.AllocationTransactions.AsNoTracking()
                .Where(t => t.BudgetYear == year && postedIds.Contains(t.RunId)
                    && (entityId == null || t.EntityId == entityId))
                .Select(t => t.EntityId).Distinct().ToListAsync();

            // One run per entity: the entity's own latest posted run, else the latest global run.
            var allowed = new HashSet<(int runId, int entityId)>();
            foreach (var eid in txEntityIds)
            {
                var run = posted.FirstOrDefault(r => r.EntityId == eid)
                          ?? posted.FirstOrDefault(r => r.EntityId == null);
                if (run != null) allowed.Add((run.RunId, eid));
            }
            if (allowed.Count == 0) return new List<ReallocExportRow>();

            var raw = await (
                from t in _db.AllocationTransactions.AsNoTracking()
                join sp in _db.Programs.AsNoTracking() on t.SourceProgramId equals sp.ProgramId into spj
                from sp in spj.DefaultIfEmpty()
                join tp in _db.Programs.AsNoTracking() on t.TargetProgramId equals tp.ProgramId into tpj
                from tp in tpj.DefaultIfEmpty()
                join sa in _db.Activities.AsNoTracking() on t.SourceActivityId equals sa.ActivityId into saj
                from sa in saj.DefaultIfEmpty()
                join ta in _db.Activities.AsNoTracking() on t.TargetActivityId equals ta.ActivityId into taj
                from ta in taj.DefaultIfEmpty()
                join ent in _db.Entities.AsNoTracking() on t.EntityId equals ent.EntityId into entj
                from ent in entj.DefaultIfEmpty()
                where t.BudgetYear == year && postedIds.Contains(t.RunId)
                    && (entityId == null || t.EntityId == entityId)
                select new
                {
                    t.RunId,
                    t.EntityId,
                    Row = new ReallocExportRow
                    {
                        EntityLabel = ent != null ? (ent.EntityCode + " - " + ent.EntityName) : "",
                        SourceProgram = sp != null ? sp.ProgramCode + " - " + sp.ProgramName : "",
                        SourceActivity = sa != null ? sa.ActivityCode + " - " + sa.ActivityName : "",
                        TargetProgram = tp != null ? tp.ProgramCode + " - " + tp.ProgramName : "",
                        TargetActivity = ta != null ? ta.ActivityCode + " - " + ta.ActivityName : "",
                        Category = t.SourceCategoryCode ?? "",
                        AllocationPct = t.AllocationPct,
                        Amount = t.Amount
                    }
                }
            ).ToListAsync();

            return raw
                .Where(x => allowed.Contains((x.RunId, x.EntityId)))
                .Select(x => x.Row)
                .ToList();
        }

        private static void BuildActivityTransactionsWorksheet(XLWorkbook wb, List<CostTxnExportRow> txns, List<ReallocExportRow> realloc, int year, string entityLabel)
        {
            var rows = txns.Where(x => !string.IsNullOrWhiteSpace(x.ActivityCode))
                .OrderBy(x => x.EntityCode).ThenBy(x => x.ProgramCode).ThenBy(x => x.ActivityCode).ThenBy(x => x.Source)
                .ToList();

            var ws = wb.Worksheets.Add("Activity Transactions");
            ws.Cell(1, 1).Value = "Activity Cost Transactions";
            ws.Cell(2, 1).Value = $"Year: {year}    Entity: {entityLabel}";
            ws.Range(1, 1, 1, 11).Merge().Style.Font.Bold = true;
            ws.Range(1, 1, 1, 11).Style.Font.FontSize = 14;
            ws.Range(2, 1, 2, 11).Merge().Style.Font.Bold = true;

            var row = 4;
            string[] headers = { "Entity", "Program", "Activity", "Source", "Description", "GL", "Qty", "Unit Price", "Amount", "Forecast 1", "Forecast 2" };
            for (var i = 0; i < headers.Length; i++) ws.Cell(row, i + 1).Value = headers[i];
            ApplyHeaderStyle(ws.Range(row, 1, row, headers.Length));

            row++;
            foreach (var r in rows)
            {
                ws.Cell(row, 1).Value = string.IsNullOrWhiteSpace(r.EntityCode) ? r.EntityName : (r.EntityCode + " - " + r.EntityName);
                ws.Cell(row, 2).Value = string.IsNullOrWhiteSpace(r.ProgramCode) ? "" : (r.ProgramCode + " - " + r.ProgramName);
                ws.Cell(row, 3).Value = r.ActivityCode + " - " + r.ActivityName;
                ws.Cell(row, 4).Value = r.Source;
                ws.Cell(row, 5).Value = r.Description;
                ws.Cell(row, 6).Value = string.IsNullOrWhiteSpace(r.GLName) ? r.GLCode : (r.GLCode + " - " + r.GLName);
                ws.Cell(row, 7).Value = r.Quantity;
                ws.Cell(row, 8).Value = r.UnitPrice;
                ws.Cell(row, 9).Value = r.Amount;
                ws.Cell(row, 10).Value = r.Forecast1;
                ws.Cell(row, 11).Value = r.Forecast2;
                ws.Range(row, 7, row, 11).Style.NumberFormat.Format = "#,##0.00";
                row++;
            }
            if (rows.Count == 0)
            {
                ws.Cell(row, 1).Value = "No activity transactions for the selected filters.";
                row++;
            }

            var activityReallocs = realloc
                .Where(x => !string.IsNullOrWhiteSpace(x.SourceActivity) || !string.IsNullOrWhiteSpace(x.TargetActivity))
                .ToList();
            if (activityReallocs.Count > 0)
            {
                row += 2;
                ws.Cell(row, 1).Value = "Reallocation Postings (step-down) — not included in Activity Costs totals";
                ws.Range(row, 1, row, 8).Merge().Style.Font.Bold = true;
                row++;
                string[] rHeaders = { "Entity", "Source Program", "Source Activity", "Target Program", "Target Activity", "Category", "%", "Amount" };
                for (var i = 0; i < rHeaders.Length; i++) ws.Cell(row, i + 1).Value = rHeaders[i];
                ApplyHeaderStyle(ws.Range(row, 1, row, rHeaders.Length));
                row++;
                foreach (var t in activityReallocs)
                {
                    ws.Cell(row, 1).Value = t.EntityLabel;
                    ws.Cell(row, 2).Value = t.SourceProgram;
                    ws.Cell(row, 3).Value = t.SourceActivity;
                    ws.Cell(row, 4).Value = t.TargetProgram;
                    ws.Cell(row, 5).Value = t.TargetActivity;
                    ws.Cell(row, 6).Value = t.Category;
                    ws.Cell(row, 7).Value = t.AllocationPct;
                    ws.Cell(row, 8).Value = t.Amount;
                    ws.Cell(row, 7).Style.NumberFormat.Format = "0.####";
                    ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0.00";
                    row++;
                }
            }

            ws.Columns(1, 11).AdjustToContents();
        }

        private static void BuildProjectTransactionsWorksheet(XLWorkbook wb, List<CostTxnExportRow> txns, int year, string entityLabel)
        {
            var rows = txns.Where(x => !string.IsNullOrWhiteSpace(x.ProjectCode))
                .OrderBy(x => x.EntityCode).ThenBy(x => x.ProjectCode).ThenBy(x => x.Source)
                .ToList();

            var ws = wb.Worksheets.Add("Project Transactions");
            ws.Cell(1, 1).Value = "Project Cost Transactions";
            ws.Cell(2, 1).Value = $"Year: {year}    Entity: {entityLabel}";
            ws.Range(1, 1, 1, 11).Merge().Style.Font.Bold = true;
            ws.Range(1, 1, 1, 11).Style.Font.FontSize = 14;
            ws.Range(2, 1, 2, 11).Merge().Style.Font.Bold = true;

            var row = 4;
            string[] headers = { "Entity", "Project", "Program", "Source", "Description", "GL", "Qty", "Unit Price", "Amount", "Forecast 1", "Forecast 2" };
            for (var i = 0; i < headers.Length; i++) ws.Cell(row, i + 1).Value = headers[i];
            ApplyHeaderStyle(ws.Range(row, 1, row, headers.Length));

            row++;
            foreach (var r in rows)
            {
                ws.Cell(row, 1).Value = string.IsNullOrWhiteSpace(r.EntityCode) ? r.EntityName : (r.EntityCode + " - " + r.EntityName);
                ws.Cell(row, 2).Value = r.ProjectCode + " - " + r.ProjectName;
                ws.Cell(row, 3).Value = string.IsNullOrWhiteSpace(r.ProgramCode) ? "" : (r.ProgramCode + " - " + r.ProgramName);
                ws.Cell(row, 4).Value = r.Source;
                ws.Cell(row, 5).Value = r.Description;
                ws.Cell(row, 6).Value = string.IsNullOrWhiteSpace(r.GLName) ? r.GLCode : (r.GLCode + " - " + r.GLName);
                ws.Cell(row, 7).Value = r.Quantity;
                ws.Cell(row, 8).Value = r.UnitPrice;
                ws.Cell(row, 9).Value = r.Amount;
                ws.Cell(row, 10).Value = r.Forecast1;
                ws.Cell(row, 11).Value = r.Forecast2;
                ws.Range(row, 7, row, 11).Style.NumberFormat.Format = "#,##0.00";
                row++;
            }
            if (rows.Count == 0)
            {
                ws.Cell(row, 1).Value = "No project transactions for the selected filters.";
            }

            ws.Columns(1, 11).AdjustToContents();
        }

        // Imported HR employee costs (used by the GL view / GL transactions export, which is ImportedOnly HR).
        private async Task<List<CostTxnExportRow>> GetImportedHrRows(int year, int? entityId)
        {
            return await (
                from emp in _db.HrEmployeeCosts.AsNoTracking()
                join gl in _db.GLAccounts.AsNoTracking() on emp.GLCode equals gl.GLCode into glJoin
                from gl in glJoin.DefaultIfEmpty()
                where emp.BudgetYear == year && (entityId == null || emp.EntityId == entityId)
                select new CostTxnExportRow
                {
                    EntityCode = "",
                    EntityName = emp.EntityName ?? "",
                    ProgramCode = "",
                    ProgramName = "",
                    ActivityCode = "",
                    ActivityName = "",
                    ProjectCode = "",
                    ProjectName = "",
                    Source = "HR",
                    Description = emp.EmployeeId + " - " + emp.EmployeeName,
                    GLCode = emp.GLCode,
                    GLName = gl != null ? gl.GLName : "",
                    Quantity = 0m,
                    UnitPrice = 0m,
                    Amount = emp.AnnualCost,
                    Forecast1 = emp.AnnualCost,
                    Forecast2 = emp.AnnualCost
                }
            ).ToListAsync();
        }

        private static void BuildGlTransactionsWorksheet(XLWorkbook wb, List<CostTxnExportRow> budgetTxns, List<CostTxnExportRow> importedHr, int year, string entityLabel)
        {
            var rows = budgetTxns.Where(x => x.Source != "HR")
                .Concat(importedHr)
                .OrderBy(x => x.GLCode).ThenBy(x => x.Source).ThenBy(x => x.ProgramCode).ThenBy(x => x.ActivityCode)
                .ToList();

            var ws = wb.Worksheets.Add("GL Transactions");
            ws.Cell(1, 1).Value = "GL Transactions";
            ws.Cell(2, 1).Value = $"Year: {year}    Entity: {entityLabel}";
            ws.Range(1, 1, 1, 11).Merge().Style.Font.Bold = true;
            ws.Range(1, 1, 1, 11).Style.Font.FontSize = 14;
            ws.Range(2, 1, 2, 11).Merge().Style.Font.Bold = true;

            var row = 4;
            string[] headers = { "GL", "GL Name", "Source", "Program", "Activity", "Item / Employee", "Entity", "Qty", "Unit Price", "Amount" };
            for (var i = 0; i < headers.Length; i++) ws.Cell(row, i + 1).Value = headers[i];
            ApplyHeaderStyle(ws.Range(row, 1, row, headers.Length));

            row++;
            foreach (var r in rows)
            {
                ws.Cell(row, 1).Value = r.GLCode;
                ws.Cell(row, 2).Value = r.GLName;
                ws.Cell(row, 3).Value = r.Source;
                ws.Cell(row, 4).Value = string.IsNullOrWhiteSpace(r.ProgramCode) ? "" : (r.ProgramCode + " - " + r.ProgramName);
                ws.Cell(row, 5).Value = string.IsNullOrWhiteSpace(r.ActivityCode) ? "" : (r.ActivityCode + " - " + r.ActivityName);
                ws.Cell(row, 6).Value = r.Description;
                ws.Cell(row, 7).Value = string.IsNullOrWhiteSpace(r.EntityCode) ? r.EntityName : (r.EntityCode + " - " + r.EntityName);
                ws.Cell(row, 8).Value = r.Quantity;
                ws.Cell(row, 9).Value = r.UnitPrice;
                ws.Cell(row, 10).Value = r.Amount;
                ws.Range(row, 8, row, 10).Style.NumberFormat.Format = "#,##0.00";
                row++;
            }
            if (rows.Count == 0)
            {
                ws.Cell(row, 1).Value = "No GL transactions for the selected filters.";
            }

            ws.Columns(1, 10).AdjustToContents();
        }

        private static void BuildHrAllocationsWorksheet(XLWorkbook wb, List<HrAllocationRowVm> rows, int year, string entityLabel)
        {
            var ws = wb.Worksheets.Add("HR Allocations");
            ws.Cell(1, 1).Value = "HR Allocations (Employee → Program → Activity → GL)";
            ws.Cell(2, 1).Value = $"Year: {year}    Entity: {entityLabel}";
            ws.Range(1, 1, 1, 8).Merge().Style.Font.Bold = true;
            ws.Range(1, 1, 1, 8).Style.Font.FontSize = 14;
            ws.Range(2, 1, 2, 8).Merge().Style.Font.Bold = true;

            var row = 4;
            ws.Cell(row, 1).Value = "Employee ID";
            ws.Cell(row, 2).Value = "Employee Name";
            ws.Cell(row, 3).Value = "Entity";
            ws.Cell(row, 4).Value = "Cost Center";
            ws.Cell(row, 5).Value = "Program";
            ws.Cell(row, 6).Value = "Activity";
            ws.Cell(row, 7).Value = "GL";
            ws.Cell(row, 8).Value = "Amount";
            ApplyHeaderStyle(ws.Range(row, 1, row, 8));

            row++;
            foreach (var r in rows)
            {
                ws.Cell(row, 1).Value = r.EmployeeId;
                ws.Cell(row, 2).Value = r.EmployeeName;
                ws.Cell(row, 3).Value = r.EntityName;
                ws.Cell(row, 4).Value = r.DepartmentName;
                ws.Cell(row, 5).Value = string.IsNullOrWhiteSpace(r.ProgramCode) ? "" : (r.ProgramCode + " - " + r.ProgramName);
                ws.Cell(row, 6).Value = r.ActivityCode + " - " + r.ActivityName;
                ws.Cell(row, 7).Value = string.IsNullOrWhiteSpace(r.GLName) ? r.GLCode : (r.GLCode + " - " + r.GLName);
                ws.Cell(row, 8).Value = r.Amount;
                ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0.00";
                row++;
            }

            ws.Columns(1, 8).AdjustToContents();
        }

        private static void BuildHrHourlyRateWorksheet(XLWorkbook wb, HrHourlyRateVm vm, int year, string entityLabel)
        {
            const int LastCol = 14;
            var ws = wb.Worksheets.Add("Employee Cost per Hour");
            ws.Cell(1, 1).Value = "Employee Cost per Hour (standard / fully loaded)";
            ws.Cell(2, 1).Value = $"Year: {year}    Entity: {entityLabel}";
            ws.Range(1, 1, 1, LastCol).Merge().Style.Font.Bold = true;
            ws.Range(1, 1, 1, LastCol).Style.Font.FontSize = 14;
            ws.Range(2, 1, 2, LastCol).Merge().Style.Font.Bold = true;

            // The basis has to travel with the file: an hourly rate with no stated
            // divisor is the kind of number that gets quoted out of context.
            ws.Cell(3, 1).Value =
                "Standard rate = annual cost / productive hours (contracted hours less paid public holidays and annual leave). " +
                "Paid leave sits inside annual cost, so it is absorbed into the rate - this is the rate to use for costing. " +
                "Nominal rate divides by contracted hours and is shown for reference only.";
            ws.Range(3, 1, 3, LastCol).Merge();
            ws.Cell(3, 1).Style.Font.Italic = true;
            ws.Cell(3, 1).Style.Font.FontColor = XLColor.Gray;

            var row = 5;
            ws.Cell(row, 1).Value = "Blended rate / hour";
            ws.Cell(row, 2).Value = vm.BlendedRatePerHour ?? 0m;
            ws.Cell(row, 2).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 3).Value = "Employees";
            ws.Cell(row, 4).Value = vm.EmployeeCount - vm.VacancyCount;
            ws.Cell(row, 5).Value = "Vacant posts (excluded)";
            ws.Cell(row, 6).Value = vm.VacancyCount;
            ws.Cell(row, 7).Value = "No calendar";
            ws.Cell(row, 8).Value = vm.MissingCalendarCount;
            ws.Range(row, 1, row, 8).Style.Font.Bold = true;

            row += 2;
            ws.Cell(row, 1).Value = "Employee ID";
            ws.Cell(row, 2).Value = "Employee Name";
            ws.Cell(row, 3).Value = "Occupation";
            ws.Cell(row, 4).Value = "Entity";
            ws.Cell(row, 5).Value = "Cost Center";
            ws.Cell(row, 6).Value = "Annual Cost";
            ws.Cell(row, 7).Value = "Gross Paid Hours";
            ws.Cell(row, 8).Value = "Holiday Hours";
            ws.Cell(row, 9).Value = "Leave Hours";
            ws.Cell(row, 10).Value = "Productive Hours";
            ws.Cell(row, 11).Value = "Effective Hours";
            ws.Cell(row, 12).Value = "Standard Rate / Hour";
            ws.Cell(row, 13).Value = "Nominal Rate / Hour";
            ws.Cell(row, 14).Value = "Note";
            ApplyHeaderStyle(ws.Range(row, 1, row, LastCol));

            row++;
            foreach (var r in vm.Rows)
            {
                ws.Cell(row, 1).Value = r.EmployeeId;
                ws.Cell(row, 2).Value = r.EmployeeName;
                ws.Cell(row, 3).Value = r.Occupation ?? "";
                ws.Cell(row, 4).Value = r.EntityName;
                ws.Cell(row, 5).Value = r.DepartmentName;
                ws.Cell(row, 6).Value = r.AnnualCost;
                ws.Cell(row, 7).Value = r.GrossPaidHours ?? 0m;
                ws.Cell(row, 8).Value = r.HolidayHours ?? 0m;
                ws.Cell(row, 9).Value = r.LeaveHours ?? 0m;
                ws.Cell(row, 10).Value = r.ProductiveHours ?? 0m;
                ws.Cell(row, 11).Value = r.EffectiveHours ?? 0m;

                if (r.StandardRatePerHour.HasValue)
                {
                    ws.Cell(row, 12).Value = r.StandardRatePerHour.Value;
                }

                if (r.NominalRatePerHour.HasValue)
                {
                    ws.Cell(row, 13).Value = r.NominalRatePerHour.Value;
                }

                ws.Cell(row, 14).Value =
                    r.IsVacancy == true ? "Vacant post - part-year cost, rate not meaningful"
                    : r.IsRateAvailable != true ? "No work calendar for this year"
                    : r.OverrideHours.HasValue ? "Hours overridden for this employee"
                    : "";

                ws.Range(row, 6, row, 11).Style.NumberFormat.Format = "#,##0.00";
                ws.Range(row, 12, row, 13).Style.NumberFormat.Format = "#,##0.0000";

                if (r.IsVacancy == true || r.IsRateAvailable != true)
                {
                    ws.Range(row, 1, row, LastCol).Style.Font.FontColor = XLColor.Gray;
                }

                row++;
            }

            ws.Columns(1, LastCol).AdjustToContents();
        }

        private static void BuildEntitySummaryWorksheet(XLWorkbook wb, List<EntityBudgetSummaryRowVm> rows, int year, string entityLabel)
        {
            var ws = wb.Worksheets.Add("Entity Summary");
            ws.Cell(1, 1).Value = "Entity Budget Summary";
            ws.Cell(2, 1).Value = $"Year: {year}    Entity Filter: {entityLabel}";
            ws.Range(1, 1, 1, 14).Merge().Style.Font.Bold = true;
            ws.Range(1, 1, 1, 14).Style.Font.FontSize = 14;
            ws.Range(2, 1, 2, 14).Merge().Style.Font.Bold = true;

            var row = 4;
            ws.Cell(row, 1).Value = "Entity";
            ws.Cell(row, 2).Value = "Revenue";
            ws.Cell(row, 3).Value = "HR Cost";
            ws.Cell(row, 4).Value = "Head Count";
            ws.Cell(row, 5).Value = "CAPEX";
            ws.Cell(row, 6).Value = "OPEX";
            ws.Cell(row, 7).Value = "Total Expense";
            ws.Cell(row, 8).Value = "Net";
            ws.Cell(row, 9).Value = "Forecast 1 Revenue";
            ws.Cell(row, 10).Value = "Forecast 1 Total Expense";
            ws.Cell(row, 11).Value = "Forecast 1 Net";
            ws.Cell(row, 12).Value = "Forecast 2 Revenue";
            ws.Cell(row, 13).Value = "Forecast 2 Total Expense";
            ws.Cell(row, 14).Value = "Forecast 2 Net";
            ApplyHeaderStyle(ws.Range(row, 1, row, 14));

            row++;
            foreach (var r in rows)
            {
                ws.Cell(row, 1).Value = r.EntityCode + " - " + r.EntityName;
                ws.Cell(row, 2).Value = r.Revenue;
                ws.Cell(row, 3).Value = r.HrCost;
                ws.Cell(row, 4).Value = r.HeadCount;
                ws.Cell(row, 5).Value = r.Capex;
                ws.Cell(row, 6).Value = r.Opex;
                ws.Cell(row, 7).Value = r.TotalExpense;
                ws.Cell(row, 8).Value = r.Net;
                ws.Cell(row, 9).Value = r.Forecast1Revenue;
                ws.Cell(row, 10).Value = r.Forecast1TotalExpense;
                ws.Cell(row, 11).Value = r.Forecast1Net;
                ws.Cell(row, 12).Value = r.Forecast2Revenue;
                ws.Cell(row, 13).Value = r.Forecast2TotalExpense;
                ws.Cell(row, 14).Value = r.Forecast2Net;
                ws.Range(row, 2, row, 3).Style.NumberFormat.Format = "#,##0.00";
                ws.Range(row, 5, row, 14).Style.NumberFormat.Format = "#,##0.00";
                row++;
            }

            ws.Columns(1, 14).AdjustToContents();
        }

        private static void BuildTrendWorksheet(XLWorkbook wb, List<TrendRowVm> rows, int year, string entityLabel)
        {
            var ws = wb.Worksheets.Add("Trend Summary");
            ws.Cell(1, 1).Value = "Trend Summary (Actuals + Budget + Forecasts)";
            ws.Cell(2, 1).Value = $"Year: {year}    Entity Filter: {entityLabel}";
            ws.Range(1, 1, 1, 5).Merge().Style.Font.Bold = true;
            ws.Range(1, 1, 1, 5).Style.Font.FontSize = 14;
            ws.Range(2, 1, 2, 5).Merge().Style.Font.Bold = true;

            var row = 4;
            ws.Cell(row, 1).Value = "Line";
            ws.Cell(row, 2).Value = $"{year - 1} Actual";
            ws.Cell(row, 3).Value = $"{year} Budget";
            ws.Cell(row, 4).Value = $"{year + 1} Forecast";
            ws.Cell(row, 5).Value = $"{year + 2} Forecast";
            ApplyHeaderStyle(ws.Range(row, 1, row, 5));

            row++;
            foreach (var r in rows)
            {
                ws.Cell(row, 1).Value = r.Line;
                ws.Cell(row, 2).Value = r.Actual;
                ws.Cell(row, 3).Value = r.Budget;
                ws.Cell(row, 4).Value = r.Forecast1;
                ws.Cell(row, 5).Value = r.Forecast2;
                ws.Range(row, 2, row, 5).Style.NumberFormat.Format = "#,##0.00";
                row++;
            }

            ws.Columns(1, 5).AdjustToContents();
        }

        private async Task<IncomeStatementVm> BuildIncomeStatement(int year, int? entityId)
        {
            var rows = await GetLedgerEntries(year, entityId, HrLedgerMode.ImportedOnly);

            var revenue = rows.Where(x => x.CategoryCode == "REVENUE").Sum(x => x.Amount);
            var capex = rows.Where(x => x.CategoryCode == "CAPEX").Sum(x => x.Amount);
            var opex = rows.Where(x => x.CategoryCode == "OPEX").Sum(x => x.Amount);
            var hr = rows.Where(x => x.CategoryCode == "HR").Sum(x => x.Amount);
            var totalExpense = capex + opex + hr;

            var forecast1Revenue = rows.Where(x => x.CategoryCode == "REVENUE").Sum(x => x.Forecast1Amount);
            var forecast1Capex = rows.Where(x => x.CategoryCode == "CAPEX").Sum(x => x.Forecast1Amount);
            var forecast1Opex = rows.Where(x => x.CategoryCode == "OPEX").Sum(x => x.Forecast1Amount);
            var forecast1Hr = rows.Where(x => x.CategoryCode == "HR").Sum(x => x.Forecast1Amount);
            var forecast1TotalExpense = forecast1Capex + forecast1Opex + forecast1Hr;

            var forecast2Revenue = rows.Where(x => x.CategoryCode == "REVENUE").Sum(x => x.Forecast2Amount);
            var forecast2Capex = rows.Where(x => x.CategoryCode == "CAPEX").Sum(x => x.Forecast2Amount);
            var forecast2Opex = rows.Where(x => x.CategoryCode == "OPEX").Sum(x => x.Forecast2Amount);
            var forecast2Hr = rows.Where(x => x.CategoryCode == "HR").Sum(x => x.Forecast2Amount);
            var forecast2TotalExpense = forecast2Capex + forecast2Opex + forecast2Hr;

            return new IncomeStatementVm
            {
                Lines = rows
                    .OrderBy(x => x.ProgramCode)
                    .ThenBy(x => x.ActivityCode)
                    .ThenBy(x => CategorySortKey(x.CategoryCode))
                    .ThenBy(x => x.GLCode)
                    .ToList(),
                TotalRevenue = revenue,
                TotalCapex = capex,
                TotalOpex = opex,
                TotalHr = hr,
                TotalExpense = totalExpense,
                SurplusDeficit = revenue - totalExpense,
                Forecast1TotalRevenue = forecast1Revenue,
                Forecast1TotalCapex = forecast1Capex,
                Forecast1TotalOpex = forecast1Opex,
                Forecast1TotalHr = forecast1Hr,
                Forecast1TotalExpense = forecast1TotalExpense,
                Forecast1SurplusDeficit = forecast1Revenue - forecast1TotalExpense,
                Forecast2TotalRevenue = forecast2Revenue,
                Forecast2TotalCapex = forecast2Capex,
                Forecast2TotalOpex = forecast2Opex,
                Forecast2TotalHr = forecast2Hr,
                Forecast2TotalExpense = forecast2TotalExpense,
                Forecast2SurplusDeficit = forecast2Revenue - forecast2TotalExpense
            };
        }

        private async Task<List<GlSummaryRowVm>> BuildGlSummary(int year, int? entityId)
        {
            var rows = await GetLedgerEntries(year, entityId, HrLedgerMode.ImportedOnly);

            var includeEntity = !entityId.HasValue;
            return rows
                .GroupBy(x => includeEntity ? new { x.EntityId, x.EntityCode, x.EntityName, x.GLCode, x.GLName } : new { EntityId = 0, EntityCode = "", EntityName = "", x.GLCode, x.GLName })
                .Select(g =>
                {
                    var revenue = g.Where(x => x.CategoryCode == "REVENUE").Sum(x => x.Amount);
                    var capex = g.Where(x => x.CategoryCode == "CAPEX").Sum(x => x.Amount);
                    var opex = g.Where(x => x.CategoryCode == "OPEX").Sum(x => x.Amount);
                    var hr = g.Where(x => x.CategoryCode == "HR").Sum(x => x.Amount);
                    return new GlSummaryRowVm
                    {
                        EntityId = g.Key.EntityId,
                        EntityCode = g.Key.EntityCode,
                        EntityName = g.Key.EntityName,
                        GLCode = g.Key.GLCode,
                        GLName = g.Key.GLName,
                        Revenue = revenue,
                        Capex = capex,
                        Opex = opex,
                        Hr = hr,
                        Net = revenue - (capex + opex + hr)
                    };
                })
                .OrderBy(x => x.EntityCode)
                .ThenBy(x => x.GLCode)
                .ToList();
        }

        private async Task<List<ProjectCostRowVm>> BuildProjectCosts(int year, int? entityId)
        {
            var rows = await GetLedgerEntries(year, entityId, HrLedgerMode.AllocatedOnly);

            var includeEntity = !entityId.HasValue;
            return rows
                .Where(x => !string.IsNullOrWhiteSpace(x.ProjectCode))
                .GroupBy(x => includeEntity ? new { x.EntityId, x.EntityCode, x.EntityName, x.ProjectCode, x.ProjectName } : new { EntityId = 0, EntityCode = "", EntityName = "", x.ProjectCode, x.ProjectName })
                .Select(g =>
                {
                    var revenue = g.Where(x => x.CategoryCode == "REVENUE").Sum(x => x.Amount);
                    var capex = g.Where(x => x.CategoryCode == "CAPEX").Sum(x => x.Amount);
                    var opex = g.Where(x => x.CategoryCode == "OPEX").Sum(x => x.Amount);
                    var hr = g.Where(x => x.CategoryCode == "HR").Sum(x => x.Amount);
                    var expense = capex + opex + hr;
                    return new ProjectCostRowVm
                    {
                        ProjectId = g.Select(x => x.ProjectId ?? 0).FirstOrDefault(id => id > 0),
                        EntityId = g.Select(x => x.EntityId).FirstOrDefault(id => id > 0),
                        EntityCode = g.Key.EntityCode,
                        EntityName = g.Key.EntityName,
                        ProjectCode = g.Key.ProjectCode,
                        ProjectName = g.Key.ProjectName,
                        Revenue = revenue,
                        Capex = capex,
                        Opex = opex,
                        Hr = hr,
                        TotalExpense = expense,
                        Net = revenue - expense
                    };
                })
                .OrderBy(x => x.EntityCode)
                .ThenBy(x => x.ProjectCode)
                .ToList();
        }

        private async Task<List<ActivityCostRowVm>> BuildActivityCosts(int year, int? entityId)
        {
            var rows = await GetLedgerEntries(year, entityId, HrLedgerMode.AllocatedOnly);
            return GroupActivityCosts(rows, entityId);
        }

        // Activity Costs AFTER the step-down cost allocation: the same activity ledger,
        // plus the latest Posted allocation run distributed onto each programme's
        // activities pro-rata by their direct cost in the same category. Mirrors the
        // core.vw_CostByActivity_AfterAllocation SQL view and refreshes with each run.
        private async Task<List<ActivityCostRowVm>> BuildActivityCostsAfterAllocation(int year, int? entityId)
        {
            var rows = await GetLedgerEntries(year, entityId, HrLedgerMode.AllocatedOnly);
            var (adjustments, netByActivity) = await BuildActivityAllocationAdjustments(rows, year, entityId);
            rows.AddRange(adjustments);

            var result = GroupActivityCosts(rows, entityId);
            foreach (var r in result)
            {
                if (r.ActivityId > 0 && netByActivity.TryGetValue(r.ActivityId, out var na))
                    r.Allocated = na;
                else if (r.ActivityId <= 0)
                    r.Allocated = r.TotalExpense; // synthetic programme-level reallocation row
            }
            return result;
        }

        private static List<ActivityCostRowVm> GroupActivityCosts(List<LedgerEntry> rows, int? entityId)
        {
            var includeEntity = !entityId.HasValue;
            return rows
                .Where(x => !string.IsNullOrWhiteSpace(x.ActivityCode))
                .GroupBy(x => includeEntity
                    ? new { x.EntityId, x.EntityCode, x.EntityName, x.ProgramCode, x.ProgramName, x.ActivityCode, x.ActivityName }
                    : new { EntityId = 0, EntityCode = "", EntityName = "", x.ProgramCode, x.ProgramName, x.ActivityCode, x.ActivityName })
                .Select(g =>
                {
                    var revenue = g.Where(x => x.CategoryCode == "REVENUE").Sum(x => x.Amount);
                    var capex = g.Where(x => x.CategoryCode == "CAPEX").Sum(x => x.Amount);
                    var opex = g.Where(x => x.CategoryCode == "OPEX").Sum(x => x.Amount);
                    var hr = g.Where(x => x.CategoryCode == "HR").Sum(x => x.Amount);
                    var expense = capex + opex + hr;

                    var forecast1Revenue = g.Where(x => x.CategoryCode == "REVENUE").Sum(x => x.Forecast1Amount);
                    var forecast1Capex = g.Where(x => x.CategoryCode == "CAPEX").Sum(x => x.Forecast1Amount);
                    var forecast1Opex = g.Where(x => x.CategoryCode == "OPEX").Sum(x => x.Forecast1Amount);
                    var forecast1Hr = g.Where(x => x.CategoryCode == "HR").Sum(x => x.Forecast1Amount);
                    var forecast1Expense = forecast1Capex + forecast1Opex + forecast1Hr;

                    var forecast2Revenue = g.Where(x => x.CategoryCode == "REVENUE").Sum(x => x.Forecast2Amount);
                    var forecast2Capex = g.Where(x => x.CategoryCode == "CAPEX").Sum(x => x.Forecast2Amount);
                    var forecast2Opex = g.Where(x => x.CategoryCode == "OPEX").Sum(x => x.Forecast2Amount);
                    var forecast2Hr = g.Where(x => x.CategoryCode == "HR").Sum(x => x.Forecast2Amount);
                    var forecast2Expense = forecast2Capex + forecast2Opex + forecast2Hr;

                    return new ActivityCostRowVm
                    {
                        ActivityId = g.Select(x => x.ActivityId).FirstOrDefault(),
                        EntityId = g.Select(x => x.EntityId).FirstOrDefault(id => id > 0),
                        EntityCode = g.Key.EntityCode,
                        EntityName = g.Key.EntityName,
                        ProgramCode = g.Key.ProgramCode,
                        ProgramName = g.Key.ProgramName,
                        ActivityCode = g.Key.ActivityCode,
                        ActivityName = g.Key.ActivityName,
                        Revenue = revenue,
                        Capex = capex,
                        Opex = opex,
                        Hr = hr,
                        TotalExpense = expense,
                        Net = revenue - expense,
                        Forecast1TotalExpense = forecast1Expense,
                        Forecast2TotalExpense = forecast2Expense,
                        Forecast1Net = forecast1Revenue - forecast1Expense,
                        Forecast2Net = forecast2Revenue - forecast2Expense
                    };
                })
                .OrderBy(x => x.EntityCode)
                .ThenBy(x => x.ProgramCode)
                .ThenBy(x => x.ActivityCode)
                .ToList();
        }

        // Builds the step-down allocation as extra activity-grain ledger rows. Reads the
        // latest Posted run per entity (falls back to a global null-entity run) and spreads
        // each (programme, category) net onto that programme's activities pro-rata by their
        // direct cost in the same category. Returns the extra rows plus the net per activity.
        private async Task<(List<LedgerEntry> adjustments, Dictionary<int, decimal> netByActivity)>
            BuildActivityAllocationAdjustments(List<LedgerEntry> baseRows, int year, int? entityId)
        {
            var adjustments = new List<LedgerEntry>();
            var netByActivity = new Dictionary<int, decimal>();

            var entityIds = entityId.HasValue
                ? new List<int> { entityId.Value }
                : baseRows.Select(x => x.EntityId).Where(id => id > 0).Distinct().ToList();
            if (entityIds.Count == 0) return (adjustments, netByActivity);

            List<AllocationTransactions> txns;
            Dictionary<int, Programs> progs;
            try
            {
                var posted = await _db.AllocationRuns.AsNoTracking()
                    .Where(r => r.BudgetYear == year && r.Status == "Posted"
                        && (r.EntityId == null || entityIds.Contains(r.EntityId.Value)))
                    .OrderByDescending(r => r.RunAt).ToListAsync();
                if (posted.Count == 0) return (adjustments, netByActivity);

                var runByEntity = new Dictionary<int, int>();
                var runIds = new HashSet<int>();
                foreach (var eid in entityIds)
                {
                    var run = posted.FirstOrDefault(r => r.EntityId == eid)
                              ?? posted.FirstOrDefault(r => r.EntityId == null);
                    if (run != null) { runByEntity[eid] = run.RunId; runIds.Add(run.RunId); }
                }
                if (runIds.Count == 0) return (adjustments, netByActivity);

                var candidate = await _db.AllocationTransactions.AsNoTracking()
                    .Where(t => runIds.Contains(t.RunId)).ToListAsync();
                txns = candidate.Where(t => runByEntity.TryGetValue(t.EntityId, out var rid) && rid == t.RunId).ToList();
                if (txns.Count == 0) return (adjustments, netByActivity);

                var progIds = txns.Select(t => t.SourceProgramId)
                    .Concat(txns.Select(t => t.TargetProgramId)).Distinct().ToList();
                progs = await _db.Programs.AsNoTracking()
                    .Where(p => progIds.Contains(p.ProgramId)).ToDictionaryAsync(p => p.ProgramId);
            }
            catch
            {
                return (adjustments, netByActivity); // allocation tables may not exist yet
            }

            // Activity meta, entity labels, and pro-rata weights from the BEFORE-allocation ledger.
            var actMeta = baseRows.Where(x => x.ActivityId > 0)
                .GroupBy(x => x.ActivityId).ToDictionary(g => g.Key, g => g.First());
            var entityMap = baseRows.Where(x => x.EntityId > 0)
                .GroupBy(x => x.EntityId)
                .ToDictionary(g => g.Key, g => (Code: g.First().EntityCode, Name: g.First().EntityName));

            var weightsByPC = baseRows.Where(x => x.ActivityId > 0)
                .GroupBy(x => (x.EntityId, x.ProgramId, Cat: (x.CategoryCode ?? "").ToUpperInvariant()))
                .ToDictionary(g => g.Key, g => g.GroupBy(x => x.ActivityId)
                    .Select(a => (ActivityId: a.Key, Weight: a.Sum(z => z.Amount)))
                    .Where(a => a.Weight > 0).ToList());
            var weightsByP = baseRows.Where(x => x.ActivityId > 0)
                .GroupBy(x => (x.EntityId, x.ProgramId))
                .ToDictionary(g => g.Key, g => g.GroupBy(x => x.ActivityId)
                    .Select(a => (ActivityId: a.Key, Weight: a.Sum(z => z.Amount)))
                    .Where(a => a.Weight > 0).ToList());

            LedgerEntry MakeAdj(int entId, Programs p, int actId, string actCode, string actName, string cat, decimal amount)
            {
                var per = amount / 12m;
                entityMap.TryGetValue(entId, out var ent);
                return new LedgerEntry
                {
                    Year = year,
                    EntityId = entId,
                    EntityCode = ent.Code ?? "",
                    EntityName = ent.Name ?? "",
                    CategoryCode = cat,
                    ProgramId = p?.ProgramId ?? 0,
                    ProgramType = p?.ProgramType ?? "Mandate",
                    ProgramCode = p?.ProgramCode ?? "",
                    ProgramName = p?.ProgramName ?? "",
                    ActivityId = actId,
                    ActivityCode = actCode,
                    ActivityName = actName,
                    GLType = "Allocated",
                    Amount = amount,
                    Forecast1Amount = amount,
                    Forecast2Amount = amount,
                    M01 = per, M02 = per, M03 = per, M04 = per, M05 = per, M06 = per,
                    M07 = per, M08 = per, M09 = per, M10 = per, M11 = per, M12 = per
                };
            }

            void Distribute(int entId, int programId, string cat, decimal amount)
            {
                if (amount == 0m) return;
                progs.TryGetValue(programId, out var p);

                List<(int ActivityId, decimal Weight)> weights = null;
                if (weightsByPC.TryGetValue((entId, programId, cat), out var w1) && w1.Count > 0) weights = w1;
                else if (weightsByP.TryGetValue((entId, programId), out var w2) && w2.Count > 0) weights = w2;

                var totalW = weights?.Sum(x => x.Weight) ?? 0m;
                if (weights == null || weights.Count == 0 || totalW <= 0m)
                {
                    // No activity to attach to -> keep it at programme level so totals still reconcile.
                    adjustments.Add(MakeAdj(entId, p, 0, "(Allocated)", "Reallocation", cat, amount));
                    return;
                }

                decimal running = 0m;
                for (var i = 0; i < weights.Count; i++)
                {
                    var (aid, w) = weights[i];
                    var share = (i == weights.Count - 1) ? (amount - running) : Math.Round(amount * (w / totalW), 2);
                    running += share;
                    if (share == 0m) continue;
                    actMeta.TryGetValue(aid, out var meta);
                    adjustments.Add(MakeAdj(entId, p, aid, meta?.ActivityCode ?? "", meta?.ActivityName ?? "", cat, share));
                    netByActivity[aid] = netByActivity.GetValueOrDefault(aid) + share;
                }
            }

            foreach (var t in txns)
            {
                var cat = (t.SourceCategoryCode ?? "OPEX").ToUpperInvariant();
                Distribute(t.EntityId, t.TargetProgramId, cat, t.Amount);   // allocated-in (+)
                Distribute(t.EntityId, t.SourceProgramId, cat, -t.Amount);  // allocated-out (-)
            }

            return (adjustments, netByActivity);
        }

        // Drill-down for a single activity: the underlying OPEX/CAPEX/Revenue budget lines,
        // HR allocations, and any reallocation (step-down) postings touching the activity.
        [HttpGet]
        public async Task<IActionResult> ActivityCostDetail(int activityId, int year, int? entityId = null)
        {
            var isAdmin = User.IsInRole("ADMIN");
            var isSysAdmin = User.IsInRole("SYSADMIN");
            var isAdminLike = isAdmin || isSysAdmin;
            var scopedEntityId = GetEntityClaimId();
            var isGlobalAdmin = IsGlobalAdmin(isAdmin, isSysAdmin, scopedEntityId);
            var effectiveEntityId = ResolveEntityScope(isAdminLike, isGlobalAdmin, entityId);

            var act = await (
                from a in _db.Activities.AsNoTracking()
                join p in _db.Programs.AsNoTracking() on a.ProgramId equals p.ProgramId
                where a.ActivityId == activityId
                select new { a.ActivityId, a.ActivityCode, a.ActivityName, p.EntityId, p.ProgramCode, p.ProgramName }
            ).FirstOrDefaultAsync();
            if (act == null) return NotFound();

            // Enforce entity scope: entity admins (and global admins filtering one entity) can only see their entity's activity.
            if (effectiveEntityId.HasValue)
            {
                if (effectiveEntityId.Value <= 0) return Forbid();
                if (act.EntityId != effectiveEntityId.Value) return Forbid();
            }

            var vm = new ActivityCostDetailVm
            {
                ActivityId = act.ActivityId,
                ActivityLabel = act.ActivityCode + " - " + act.ActivityName,
                ProgramLabel = act.ProgramCode + " - " + act.ProgramName,
                Year = year
            };

            var lines = await (
                from b in _db.BudgetLines.AsNoTracking()
                join cat in _db.Categories.AsNoTracking() on b.CategoryId equals cat.CategoryId
                join item in _db.Items.AsNoTracking() on b.ItemId equals item.ItemId
                join gl in _db.GLAccounts.AsNoTracking() on item.GLAccountId equals gl.GLAccountId
                where b.BudgetYear == year && b.ActivityId == activityId && cat.CategoryCode != "HR"
                select new ActivityCostLineVm
                {
                    CategoryCode = cat.CategoryCode,
                    ItemCode = item.ItemCode,
                    ItemName = item.ItemName,
                    GLCode = gl.GLCode,
                    GLName = gl.GLName,
                    Quantity = b.Quantity,
                    UnitPrice = b.UnitPrice,
                    Amount = b.Amount,
                    Forecast1 = b.F1_Amount,
                    Forecast2 = b.F2_Amount
                }
            ).ToListAsync();
            vm.OpexLines = lines.Where(x => x.CategoryCode == "OPEX").OrderBy(x => x.ItemCode).ToList();
            vm.CapexLines = lines.Where(x => x.CategoryCode == "CAPEX").OrderBy(x => x.ItemCode).ToList();
            vm.RevenueLines = lines.Where(x => x.CategoryCode == "REVENUE").OrderBy(x => x.ItemCode).ToList();

            vm.HrLines = await (
                from a in _db.HrEmployeeCostAllocations.AsNoTracking()
                join emp in _db.HrEmployeeCosts.AsNoTracking() on a.EmployeeCostId equals emp.EmployeeCostId
                join gl in _db.GLAccounts.AsNoTracking() on emp.GLCode equals gl.GLCode into glJoin
                from gl in glJoin.DefaultIfEmpty()
                where emp.BudgetYear == year && a.ActivityId == activityId
                select new ActivityHrLineVm
                {
                    EmployeeId = emp.EmployeeId,
                    EmployeeName = emp.EmployeeName,
                    GLCode = emp.GLCode,
                    GLName = gl != null ? gl.GLName : "",
                    Amount = a.AllocatedAmount
                }
            ).ToListAsync();

            var reallocRaw = await (
                from t in _db.AllocationTransactions.AsNoTracking()
                join sp in _db.Programs.AsNoTracking() on t.SourceProgramId equals sp.ProgramId into spj
                from sp in spj.DefaultIfEmpty()
                join tp in _db.Programs.AsNoTracking() on t.TargetProgramId equals tp.ProgramId into tpj
                from tp in tpj.DefaultIfEmpty()
                join sa in _db.Activities.AsNoTracking() on t.SourceActivityId equals sa.ActivityId into saj
                from sa in saj.DefaultIfEmpty()
                join ta in _db.Activities.AsNoTracking() on t.TargetActivityId equals ta.ActivityId into taj
                from ta in taj.DefaultIfEmpty()
                where t.BudgetYear == year && (t.SourceActivityId == activityId || t.TargetActivityId == activityId)
                select new
                {
                    t.SourceActivityId,
                    t.TargetActivityId,
                    SourceProgramCode = sp != null ? sp.ProgramCode : "",
                    SourceActivityCode = sa != null ? sa.ActivityCode : "",
                    TargetProgramCode = tp != null ? tp.ProgramCode : "",
                    TargetActivityCode = ta != null ? ta.ActivityCode : "",
                    t.SourceCategoryCode,
                    t.AllocationPct,
                    t.Amount
                }
            ).ToListAsync();

            vm.ReallocLines = reallocRaw
                .Select(t => new ActivityReallocLineVm
                {
                    Direction = t.TargetActivityId == activityId ? "In" : "Out",
                    SourceProgram = t.SourceProgramCode,
                    SourceActivity = t.SourceActivityCode,
                    TargetProgram = t.TargetProgramCode,
                    TargetActivity = t.TargetActivityCode,
                    Category = t.SourceCategoryCode ?? "",
                    AllocationPct = t.AllocationPct,
                    Amount = t.Amount
                })
                .OrderBy(x => x.Direction)
                .ToList();

            return PartialView("_ActivityCostDetail", vm);
        }

        // Drill-down for a single project: the underlying OPEX/CAPEX/Revenue budget lines and HR
        // allocations tagged to the project. (Reallocation postings are program/activity-based, not project.)
        [HttpGet]
        public async Task<IActionResult> ProjectCostDetail(int projectId, int year, int? entityId = null)
        {
            var isAdmin = User.IsInRole("ADMIN");
            var isSysAdmin = User.IsInRole("SYSADMIN");
            var isAdminLike = isAdmin || isSysAdmin;
            var scopedEntityId = GetEntityClaimId();
            var isGlobalAdmin = IsGlobalAdmin(isAdmin, isSysAdmin, scopedEntityId);
            var effectiveEntityId = ResolveEntityScope(isAdminLike, isGlobalAdmin, entityId);
            if (effectiveEntityId.HasValue && effectiveEntityId.Value <= 0) return Forbid();

            var proj = await _db.Projects.AsNoTracking()
                .Where(p => p.ProjectId == projectId)
                .Select(p => new { p.ProjectId, p.ProjectCode, p.ProjectName })
                .FirstOrDefaultAsync();
            if (proj == null) return NotFound();

            var vm = new ProjectCostDetailVm
            {
                ProjectId = proj.ProjectId,
                ProjectLabel = proj.ProjectCode + " - " + proj.ProjectName,
                Year = year
            };

            var lineQuery =
                from b in _db.BudgetLines.AsNoTracking()
                join cat in _db.Categories.AsNoTracking() on b.CategoryId equals cat.CategoryId
                join item in _db.Items.AsNoTracking() on b.ItemId equals item.ItemId
                join gl in _db.GLAccounts.AsNoTracking() on item.GLAccountId equals gl.GLAccountId
                where b.BudgetYear == year && b.ProjectId == projectId && cat.CategoryCode != "HR"
                select new { b.EntityId, cat.CategoryCode, item.ItemCode, item.ItemName, gl.GLCode, gl.GLName, b.Quantity, b.UnitPrice, b.Amount, F1 = b.F1_Amount, F2 = b.F2_Amount };
            if (effectiveEntityId.HasValue) lineQuery = lineQuery.Where(x => x.EntityId == effectiveEntityId.Value);

            var lines = (await lineQuery.ToListAsync())
                .Select(x => new ActivityCostLineVm
                {
                    CategoryCode = x.CategoryCode,
                    ItemCode = x.ItemCode,
                    ItemName = x.ItemName,
                    GLCode = x.GLCode,
                    GLName = x.GLName,
                    Quantity = x.Quantity,
                    UnitPrice = x.UnitPrice,
                    Amount = x.Amount,
                    Forecast1 = x.F1,
                    Forecast2 = x.F2
                })
                .ToList();
            vm.OpexLines = lines.Where(x => x.CategoryCode == "OPEX").OrderBy(x => x.ItemCode).ToList();
            vm.CapexLines = lines.Where(x => x.CategoryCode == "CAPEX").OrderBy(x => x.ItemCode).ToList();
            vm.RevenueLines = lines.Where(x => x.CategoryCode == "REVENUE").OrderBy(x => x.ItemCode).ToList();

            var hrQuery =
                from a in _db.HrEmployeeCostAllocations.AsNoTracking()
                join emp in _db.HrEmployeeCosts.AsNoTracking() on a.EmployeeCostId equals emp.EmployeeCostId
                join gl in _db.GLAccounts.AsNoTracking() on emp.GLCode equals gl.GLCode into glJoin
                from gl in glJoin.DefaultIfEmpty()
                where emp.BudgetYear == year && a.ProjectId == projectId
                select new { emp.EntityId, emp.EmployeeId, emp.EmployeeName, emp.GLCode, GLName = gl != null ? gl.GLName : "", a.AllocatedAmount };
            if (effectiveEntityId.HasValue) hrQuery = hrQuery.Where(x => x.EntityId == effectiveEntityId.Value);

            vm.HrLines = (await hrQuery.ToListAsync())
                .Select(x => new ActivityHrLineVm
                {
                    EmployeeId = x.EmployeeId,
                    EmployeeName = x.EmployeeName,
                    GLCode = x.GLCode,
                    GLName = x.GLName,
                    Amount = x.AllocatedAmount
                })
                .ToList();

            return PartialView("_ProjectCostDetail", vm);
        }

        // Drill-down for a single GL account: the underlying Revenue/CAPEX/OPEX budget lines and the
        // imported HR employee costs posted to that GL. Mirrors how BuildGlSummary aggregates (ImportedOnly HR).
        [HttpGet]
        public async Task<IActionResult> GlDetail(string glCode, int year, int? entityId = null)
        {
            var isAdmin = User.IsInRole("ADMIN");
            var isSysAdmin = User.IsInRole("SYSADMIN");
            var isAdminLike = isAdmin || isSysAdmin;
            var scopedEntityId = GetEntityClaimId();
            var isGlobalAdmin = IsGlobalAdmin(isAdmin, isSysAdmin, scopedEntityId);
            var effectiveEntityId = ResolveEntityScope(isAdminLike, isGlobalAdmin, entityId);
            if (effectiveEntityId.HasValue && effectiveEntityId.Value <= 0) return Forbid();

            glCode = (glCode ?? "").Trim();
            var gl = await _db.GLAccounts.AsNoTracking()
                .Where(g => g.GLCode == glCode)
                .Select(g => new { g.GLCode, g.GLName })
                .FirstOrDefaultAsync();
            if (gl == null) return NotFound();

            var vm = new GlDetailVm
            {
                GlLabel = gl.GLCode + " - " + gl.GLName,
                Year = year
            };

            var blBase = _db.BudgetLines.AsNoTracking().Where(b => b.BudgetYear == year);
            if (effectiveEntityId.HasValue) blBase = blBase.Where(b => b.EntityId == effectiveEntityId.Value);

            var lines = (await (
                from b in blBase
                join cat in _db.Categories.AsNoTracking() on b.CategoryId equals cat.CategoryId
                join item in _db.Items.AsNoTracking() on b.ItemId equals item.ItemId
                join glx in _db.GLAccounts.AsNoTracking() on item.GLAccountId equals glx.GLAccountId
                join act in _db.Activities.AsNoTracking() on b.ActivityId equals act.ActivityId into actJoin
                from act in actJoin.DefaultIfEmpty()
                join prog in _db.Programs.AsNoTracking() on (b.ProgramId ?? act.ProgramId) equals prog.ProgramId into progJoin
                from prog in progJoin.DefaultIfEmpty()
                where glx.GLCode == glCode && cat.CategoryCode != "HR"
                select new
                {
                    cat.CategoryCode,
                    ProgramCode = prog != null ? prog.ProgramCode : "",
                    ProgramName = prog != null ? prog.ProgramName : "",
                    ActivityCode = act != null ? act.ActivityCode : "",
                    ActivityName = act != null ? act.ActivityName : "",
                    item.ItemCode,
                    item.ItemName,
                    b.Quantity,
                    b.UnitPrice,
                    b.Amount,
                    F1 = b.F1_Amount,
                    F2 = b.F2_Amount
                }).ToListAsync())
                .Select(x => new GlLineVm
                {
                    CategoryCode = x.CategoryCode,
                    ProgramLabel = string.IsNullOrWhiteSpace(x.ProgramCode) ? "" : (x.ProgramCode + " - " + x.ProgramName),
                    ActivityLabel = string.IsNullOrWhiteSpace(x.ActivityCode) ? "" : (x.ActivityCode + " - " + x.ActivityName),
                    ItemLabel = x.ItemCode + " - " + x.ItemName,
                    Quantity = x.Quantity,
                    UnitPrice = x.UnitPrice,
                    Amount = x.Amount,
                    Forecast1 = x.F1,
                    Forecast2 = x.F2
                })
                .ToList();
            vm.RevenueLines = lines.Where(x => x.CategoryCode == "REVENUE").OrderBy(x => x.ProgramLabel).ThenBy(x => x.ActivityLabel).ToList();
            vm.CapexLines = lines.Where(x => x.CategoryCode == "CAPEX").OrderBy(x => x.ProgramLabel).ThenBy(x => x.ActivityLabel).ToList();
            vm.OpexLines = lines.Where(x => x.CategoryCode == "OPEX").OrderBy(x => x.ProgramLabel).ThenBy(x => x.ActivityLabel).ToList();

            var hrBase = _db.HrEmployeeCosts.AsNoTracking().Where(e => e.BudgetYear == year && e.GLCode == glCode);
            if (effectiveEntityId.HasValue) hrBase = hrBase.Where(e => e.EntityId == effectiveEntityId.Value);
            vm.HrLines = (await hrBase
                .Select(e => new { e.EmployeeId, e.EmployeeName, e.EntityName, e.DepartmentName, e.AnnualCost })
                .ToListAsync())
                .Select(e => new GlHrLineVm
                {
                    EmployeeId = e.EmployeeId,
                    EmployeeName = e.EmployeeName,
                    EntityName = e.EntityName ?? "",
                    DepartmentName = e.DepartmentName ?? "",
                    Amount = e.AnnualCost
                })
                .OrderBy(x => x.EmployeeName)
                .ToList();

            return PartialView("_GlDetail", vm);
        }

        // Employee cost per hour, read from core.vw_HrEmployeeHourlyRates.
        // Purely derived and read-only: this touches no table the budget entry,
        // HR import or allocation paths write to.
        private async Task<HrHourlyRateVm> BuildHrHourlyRates(int year, int? entityId)
        {
            var query = _db.vw_HrEmployeeHourlyRates
                .AsNoTracking()
                .Where(x => x.BudgetYear == year);

            if (entityId.HasValue)
            {
                query = query.Where(x => x.EntityId == entityId.Value);
            }

            var rows = await query
                .OrderBy(x => x.EntityName)
                .ThenBy(x => x.DepartmentName)
                .ThenByDescending(x => x.AnnualCost)
                .Take(5000)
                .ToListAsync();

            var vm = new HrHourlyRateVm
            {
                Rows = rows,
                EmployeeCount = rows.Count,
                VacancyCount = rows.Count(r => r.IsVacancy == true),
                MissingCalendarCount = rows.Count(r => r.IsRateAvailable != true)
            };

            // Blended figures exclude vacant posts: their cost is budgeted for a
            // part year, so including them would drag the organisational rate down
            // against hours nobody is going to work.
            var rated = rows
                .Where(r => r.IsVacancy != true && r.IsRateAvailable == true && r.EffectiveHours > 0m)
                .ToList();

            vm.TotalAnnualCost = rated.Sum(r => r.AnnualCost);
            vm.TotalEffectiveHours = rated.Sum(r => r.EffectiveHours ?? 0m);

            if (vm.TotalEffectiveHours > 0m)
            {
                vm.BlendedRatePerHour = Math.Round(vm.TotalAnnualCost / vm.TotalEffectiveHours, 2);
            }

            var grossHours = rated.Sum(r => r.GrossPaidHours ?? 0m);
            if (grossHours > 0m)
            {
                vm.BlendedNominalRatePerHour = Math.Round(vm.TotalAnnualCost / grossHours, 2);
            }

            return vm;
        }

        private async Task<List<HrAllocationRowVm>> BuildHrAllocations(int year, int? entityId)
        {
            if (entityId.HasValue && entityId.Value <= 0)
            {
                return new List<HrAllocationRowVm>();
            }

            var query =
                from a in _db.HrEmployeeCostAllocations.AsNoTracking()
                join emp in _db.HrEmployeeCosts.AsNoTracking() on a.EmployeeCostId equals emp.EmployeeCostId
                join act in _db.Activities.AsNoTracking() on a.ActivityId equals act.ActivityId
                join prog in _db.Programs.AsNoTracking() on act.ProgramId equals prog.ProgramId
                join gl in _db.GLAccounts.AsNoTracking() on emp.GLCode equals gl.GLCode into glJoin
                from gl in glJoin.DefaultIfEmpty()
                where emp.BudgetYear == year
                select new
                {
                    emp.EmployeeId,
                    emp.EmployeeName,
                    emp.EntityName,
                    emp.DepartmentName,
                    emp.EntityId,
                    ProgramCode = prog.ProgramCode,
                    ProgramName = prog.ProgramName,
                    ActivityCode = act.ActivityCode,
                    ActivityName = act.ActivityName,
                    emp.GLCode,
                    GLName = gl != null ? gl.GLName : "",
                    Amount = a.AllocatedAmount
                };

            if (entityId.HasValue)
            {
                query = query.Where(x => x.EntityId == entityId.Value);
            }

            return await query
                .GroupBy(x => new
                {
                    x.EmployeeId,
                    x.EmployeeName,
                    x.EntityName,
                    x.DepartmentName,
                    x.ProgramCode,
                    x.ProgramName,
                    x.ActivityCode,
                    x.ActivityName,
                    x.GLCode,
                    x.GLName
                })
                .Select(g => new HrAllocationRowVm
                {
                    EmployeeId = g.Key.EmployeeId,
                    EmployeeName = g.Key.EmployeeName,
                    EntityName = g.Key.EntityName,
                    DepartmentName = g.Key.DepartmentName,
                    ProgramCode = g.Key.ProgramCode,
                    ProgramName = g.Key.ProgramName,
                    ActivityCode = g.Key.ActivityCode,
                    ActivityName = g.Key.ActivityName,
                    GLCode = g.Key.GLCode,
                    GLName = g.Key.GLName,
                    Amount = g.Sum(x => x.Amount)
                })
                .OrderBy(x => x.EntityName)
                .ThenBy(x => x.DepartmentName)
                .ThenBy(x => x.EmployeeName)
                .ThenBy(x => x.ProgramCode)
                .ThenBy(x => x.ActivityCode)
                .ThenBy(x => x.GLCode)
                .ToListAsync();
        }

        private async Task<List<EntityBudgetSummaryRowVm>> BuildEntityBudgetSummary(int year, int? entityId)
        {
            if (entityId.HasValue && entityId.Value <= 0)
            {
                return new List<EntityBudgetSummaryRowVm>();
            }

            var entitiesQuery = _db.Entities.AsNoTracking();
            if (entityId.HasValue)
            {
                entitiesQuery = entitiesQuery.Where(e => e.EntityId == entityId.Value);
            }

            var entities = await entitiesQuery
                .OrderBy(e => e.EntityCode)
                .Select(e => new { e.EntityId, e.EntityCode, e.EntityName })
                .ToListAsync();

            var ledger = await GetLedgerEntries(year, entityId, HrLedgerMode.None);
            var byEntity = ledger
                .GroupBy(x => x.EntityId)
                .ToDictionary(
                    g => g.Key,
                    g => new
                    {
                        Revenue = g.Where(x => x.CategoryCode == "REVENUE").Sum(x => x.Amount),
                        Capex = g.Where(x => x.CategoryCode == "CAPEX").Sum(x => x.Amount),
                        Opex = g.Where(x => x.CategoryCode == "OPEX").Sum(x => x.Amount),
                        Forecast1Revenue = g.Where(x => x.CategoryCode == "REVENUE").Sum(x => x.Forecast1Amount),
                        Forecast1Capex = g.Where(x => x.CategoryCode == "CAPEX").Sum(x => x.Forecast1Amount),
                        Forecast1Opex = g.Where(x => x.CategoryCode == "OPEX").Sum(x => x.Forecast1Amount),
                        Forecast2Revenue = g.Where(x => x.CategoryCode == "REVENUE").Sum(x => x.Forecast2Amount),
                        Forecast2Capex = g.Where(x => x.CategoryCode == "CAPEX").Sum(x => x.Forecast2Amount),
                        Forecast2Opex = g.Where(x => x.CategoryCode == "OPEX").Sum(x => x.Forecast2Amount)
                    });

            var headcountQuery = _db.HrEmployeeCosts.AsNoTracking().Where(x => x.BudgetYear == year);
            if (entityId.HasValue)
            {
                headcountQuery = headcountQuery.Where(x => x.EntityId == entityId.Value);
            }

            var headcounts = await headcountQuery
                .GroupBy(x => new { EntityId = x.EntityId ?? 0 })
                .Select(g => new { g.Key.EntityId, HeadCount = g.Select(x => x.EmployeeId).Distinct().Count(), HrCost = g.Sum(x => x.AnnualCost) })
                .ToDictionaryAsync(x => x.EntityId, x => new { x.HeadCount, x.HrCost });

            var rows = new List<EntityBudgetSummaryRowVm>();
            foreach (var e in entities)
            {
                byEntity.TryGetValue(e.EntityId, out var sums);
                headcounts.TryGetValue(e.EntityId, out var hc);

                var revenue = sums?.Revenue ?? 0m;
                var capex = sums?.Capex ?? 0m;
                var opex = sums?.Opex ?? 0m;
                var forecast1RevenueEntity = sums?.Forecast1Revenue ?? 0m;
                var forecast1CapexEntity = sums?.Forecast1Capex ?? 0m;
                var forecast1OpexEntity = sums?.Forecast1Opex ?? 0m;
                var forecast2RevenueEntity = sums?.Forecast2Revenue ?? 0m;
                var forecast2CapexEntity = sums?.Forecast2Capex ?? 0m;
                var forecast2OpexEntity = sums?.Forecast2Opex ?? 0m;
                var hrCost = hc?.HrCost ?? 0m;
                var headCount = hc?.HeadCount ?? 0;
                var totalExpense = capex + opex + hrCost;
                var forecast1TotalExpense = forecast1CapexEntity + forecast1OpexEntity + hrCost;
                var forecast2TotalExpense = forecast2CapexEntity + forecast2OpexEntity + hrCost;

                rows.Add(new EntityBudgetSummaryRowVm
                {
                    EntityId = e.EntityId,
                    EntityCode = e.EntityCode,
                    EntityName = e.EntityName,
                    Revenue = revenue,
                    HrCost = hrCost,
                    HeadCount = headCount,
                    Capex = capex,
                    Opex = opex,
                    TotalExpense = totalExpense,
                    Net = revenue - totalExpense,
                    Forecast1Revenue = forecast1RevenueEntity,
                    Forecast1TotalExpense = forecast1TotalExpense,
                    Forecast1Net = forecast1RevenueEntity - forecast1TotalExpense,
                    Forecast2Revenue = forecast2RevenueEntity,
                    Forecast2TotalExpense = forecast2TotalExpense,
                    Forecast2Net = forecast2RevenueEntity - forecast2TotalExpense
                });
            }

            return rows;
        }

        private class GlTypeAmountVm
        {
            public string GLType { get; set; } = "";
            public decimal Amount { get; set; }
        }

        private async Task<List<TrendRowVm>> BuildTrendSummary(int year, int? entityId)
        {
            if (entityId.HasValue && entityId.Value <= 0)
            {
                return new List<TrendRowVm>();
            }

            var actualYear = year - 1;
            var ledger = await GetLedgerEntries(year, entityId, HrLedgerMode.ImportedOnly);

            var budgetRevenue = ledger.Where(x => x.CategoryCode == "REVENUE").Sum(x => x.Amount);
            var budgetHr = ledger.Where(x => x.CategoryCode == "HR").Sum(x => x.Amount);
            var budgetCapex = ledger.Where(x => x.CategoryCode == "CAPEX").Sum(x => x.Amount);
            var budgetOpex = ledger.Where(x => x.CategoryCode == "OPEX").Sum(x => x.Amount);
            var budgetExpense = budgetHr + budgetCapex + budgetOpex;
            var budgetNet = budgetRevenue - budgetExpense;

            var forecast1Revenue = ledger.Where(x => x.CategoryCode == "REVENUE").Sum(x => x.Forecast1Amount);
            var forecast1Hr = ledger.Where(x => x.CategoryCode == "HR").Sum(x => x.Forecast1Amount);
            var forecast1Capex = ledger.Where(x => x.CategoryCode == "CAPEX").Sum(x => x.Forecast1Amount);
            var forecast1Opex = ledger.Where(x => x.CategoryCode == "OPEX").Sum(x => x.Forecast1Amount);
            var forecast1Expense = forecast1Hr + forecast1Capex + forecast1Opex;
            var forecast1Net = forecast1Revenue - forecast1Expense;

            var forecast2Revenue = ledger.Where(x => x.CategoryCode == "REVENUE").Sum(x => x.Forecast2Amount);
            var forecast2Hr = ledger.Where(x => x.CategoryCode == "HR").Sum(x => x.Forecast2Amount);
            var forecast2Capex = ledger.Where(x => x.CategoryCode == "CAPEX").Sum(x => x.Forecast2Amount);
            var forecast2Opex = ledger.Where(x => x.CategoryCode == "OPEX").Sum(x => x.Forecast2Amount);
            var forecast2Expense = forecast2Hr + forecast2Capex + forecast2Opex;
            var forecast2Net = forecast2Revenue - forecast2Expense;

            var hasMidYear = await _db.MidYearGlActualForecasts.AsNoTracking()
                .Where(x => x.BudgetYear == actualYear && (!entityId.HasValue || x.EntityId == entityId.Value))
                .AnyAsync();

            var actualByGlType = new List<GlTypeAmountVm>();
            if (hasMidYear)
            {
                actualByGlType = await _db.MidYearGlActualForecasts.AsNoTracking()
                    .Where(x => x.BudgetYear == actualYear && (!entityId.HasValue || x.EntityId == entityId.Value))
                    .GroupBy(x => x.GLType)
                    .Select(g => new GlTypeAmountVm { GLType = g.Key, Amount = g.Sum(x => x.ActualH1Amount + (x.ForecastH2Amount ?? 0m)) })
                    .ToListAsync();
            }
            else
            {
                IQueryable<HistoricalGlActuals> actualsQuery = _db.HistoricalGlActuals.AsNoTracking()
                    .Where(x => x.BudgetYear == actualYear);

                if (entityId.HasValue)
                {
                    actualsQuery = actualsQuery.Where(x => x.EntityId == entityId.Value);
                }

                actualByGlType = await (
                        from a in actualsQuery
                        join gl in _db.GLAccounts.AsNoTracking() on a.GLCode equals gl.GLCode into glJoin
                        from gl in glJoin.DefaultIfEmpty()
                        group a by (a.GLType != null && a.GLType != "" ? a.GLType : (gl != null ? gl.GLType : "")) into g
                        select new GlTypeAmountVm { GLType = g.Key, Amount = g.Sum(x => x.Amount) }
                    )
                    .ToListAsync();
            }

            var actualRevenue = 0m;
            var actualHr = 0m;
            var actualCapex = 0m;
            var actualOpex = 0m;

            foreach (var x in actualByGlType)
            {
                var cat = NormalizeCategoryFromGlType(x.GLType);
                if (cat == "REVENUE") actualRevenue += x.Amount;
                else if (cat == "HR") actualHr += x.Amount;
                else if (cat == "CAPEX") actualCapex += x.Amount;
                else if (cat == "OPEX") actualOpex += x.Amount;
            }

            var actualExpense = actualHr + actualCapex + actualOpex;
            var actualNet = actualRevenue - actualExpense;

            return new List<TrendRowVm>
            {
                new TrendRowVm { Line = "Revenue", Actual = actualRevenue, Budget = budgetRevenue, Forecast1 = forecast1Revenue, Forecast2 = forecast2Revenue },
                new TrendRowVm { Line = "HR", Actual = actualHr, Budget = budgetHr, Forecast1 = forecast1Hr, Forecast2 = forecast2Hr },
                new TrendRowVm { Line = "CAPEX", Actual = actualCapex, Budget = budgetCapex, Forecast1 = forecast1Capex, Forecast2 = forecast2Capex },
                new TrendRowVm { Line = "OPEX", Actual = actualOpex, Budget = budgetOpex, Forecast1 = forecast1Opex, Forecast2 = forecast2Opex },
                new TrendRowVm { Line = "Total Expense", Actual = actualExpense, Budget = budgetExpense, Forecast1 = forecast1Expense, Forecast2 = forecast2Expense },
                new TrendRowVm { Line = "Net", Actual = actualNet, Budget = budgetNet, Forecast1 = forecast1Net, Forecast2 = forecast2Net }
            };
        }

        private static string NormalizeCategoryFromGlType(string? glType)
        {
            var t = (glType ?? "").Trim().ToUpperInvariant();
            return t switch
            {
                "REVENUE" => "REVENUE",
                "REV" => "REVENUE",
                "CAPEX" => "CAPEX",
                "OPEX" => "OPEX",
                "HR" => "HR",
                _ => "OTHER"
            };
        }
    }

    public class ReportsIndexVm
    {
        public string Report { get; set; } = "income";
        public int Year { get; set; }
        public bool IsAdmin { get; set; }
        public int? EntityId { get; set; }
        public List<SelectListItem> YearOptions { get; set; } = new();
        public List<SelectListItem> EntityOptions { get; set; } = new();

        public IncomeStatementVm? Income { get; set; }
        public List<GlSummaryRowVm>? GlSummary { get; set; }
        public List<ProjectCostRowVm>? ProjectCosts { get; set; }
        public List<ActivityCostRowVm>? ActivityCosts { get; set; }
        // True when ActivityCosts reflect the step-down cost allocation (after-allocation tab).
        public bool AfterAllocation { get; set; }
        public List<HrAllocationRowVm>? HrAllocations { get; set; }
        public HrHourlyRateVm? HrHourlyRates { get; set; }
        public List<EntityBudgetSummaryRowVm>? EntitySummary { get; set; }
        public List<TrendRowVm>? TrendSummary { get; set; }
    }

    // View-model for the executive PBB-vs-Traditional slide deck (Reports/Presentation).
    public class PbbPresentationVm
    {
        public int Year { get; set; }
        public string EntityLabel { get; set; } = "";
        public bool IsAllEntities { get; set; }
        public int? EntityId { get; set; }
        public bool IsAdmin { get; set; }
        public List<SelectListItem> YearOptions { get; set; } = new();
        public List<SelectListItem> EntityOptions { get; set; } = new();

        // Traditional (input) lens.
        public decimal TotalRevenue { get; set; }
        public decimal TotalHr { get; set; }
        public decimal TotalOpex { get; set; }
        public decimal TotalCapex { get; set; }
        public decimal TotalExpense { get; set; }
        public decimal SurplusDeficit { get; set; }

        // PBB (output) lens.
        public List<PbbProgrammeRowVm> Programmes { get; set; } = new();
        public int ProgrammeCount { get; set; }
        public int MandateProgrammeCount { get; set; }
        public int SupportProgrammeCount { get; set; }
        public int ActivityCount { get; set; }
        public int KpiCount { get; set; }
        public int KpiWithTargetCount { get; set; }
        public int KpiCostLinkedCount { get; set; }
    }

    public class PbbProgrammeRowVm
    {
        public string ProgramCode { get; set; } = "";
        public string ProgramName { get; set; } = "";
        public int ActivityCount { get; set; }
        public decimal Revenue { get; set; }
        public decimal Expense { get; set; }
    }

    public class IncomeStatementVm
    {
        public List<LedgerEntry> Lines { get; set; } = new();
        public decimal TotalRevenue { get; set; }
        public decimal TotalCapex { get; set; }
        public decimal TotalOpex { get; set; }
        public decimal TotalHr { get; set; }
        public decimal TotalExpense { get; set; }
        public decimal SurplusDeficit { get; set; }
        public decimal Forecast1TotalRevenue { get; set; }
        public decimal Forecast1TotalCapex { get; set; }
        public decimal Forecast1TotalOpex { get; set; }
        public decimal Forecast1TotalHr { get; set; }
        public decimal Forecast1TotalExpense { get; set; }
        public decimal Forecast1SurplusDeficit { get; set; }
        public decimal Forecast2TotalRevenue { get; set; }
        public decimal Forecast2TotalCapex { get; set; }
        public decimal Forecast2TotalOpex { get; set; }
        public decimal Forecast2TotalHr { get; set; }
        public decimal Forecast2TotalExpense { get; set; }
        public decimal Forecast2SurplusDeficit { get; set; }
    }

    public class LedgerEntry
    {
        public long? BudgetLineId { get; set; }
        public int Year { get; set; }
        public int EntityId { get; set; }
        public string EntityCode { get; set; } = "";
        public string EntityName { get; set; } = "";
        public int DepartmentId { get; set; }
        public string CategoryCode { get; set; } = "";
        public int? ItemId { get; set; }
        public string ItemCode { get; set; } = "";
        public string ItemName { get; set; } = "";
        public int ProgramId { get; set; }
        public string ProgramType { get; set; } = "Mandate";
        public string ProgramCode { get; set; } = "";
        public string ProgramName { get; set; } = "";
        public int ActivityId { get; set; }
        public string ActivityCode { get; set; } = "";
        public string ActivityName { get; set; } = "";
        public int? ProjectId { get; set; }
        public string ProjectCode { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string GLCode { get; set; } = "";
        public string GLName { get; set; } = "";
        public string GLType { get; set; } = "";
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Amount { get; set; }
        public decimal Forecast1Amount { get; set; }
        public decimal Forecast2Amount { get; set; }
        public decimal M01 { get; set; }
        public decimal M02 { get; set; }
        public decimal M03 { get; set; }
        public decimal M04 { get; set; }
        public decimal M05 { get; set; }
        public decimal M06 { get; set; }
        public decimal M07 { get; set; }
        public decimal M08 { get; set; }
        public decimal M09 { get; set; }
        public decimal M10 { get; set; }
        public decimal M11 { get; set; }
        public decimal M12 { get; set; }
        public decimal ActualH1Amount { get; set; }
        public decimal BudgetH1 => M01 + M02 + M03 + M04 + M05 + M06;
        public decimal VarianceH1 => BudgetH1 - ActualH1Amount;
    }

    public class ReportBuilderVm
    {
        public int Year { get; set; }
        public bool IsAdmin { get; set; }
        public int? EntityId { get; set; }
        public string RowDim { get; set; } = "entity";
        public string ColDim { get; set; } = "";
        public string Measure { get; set; } = "amount";
        public string Category { get; set; } = "";
        public string CategoryMode { get; set; } = "Include";
        public List<string> SelectedCategories { get; set; } = new();
        public string ProgramType { get; set; } = "";
        public string CostBasis { get; set; } = "Direct";
        public bool IncludeHr { get; set; }
        public string ChartType { get; set; } = "table";
        public List<SelectListItem> YearOptions { get; set; } = new();
        public List<SelectListItem> EntityOptions { get; set; } = new();
        public List<SelectListItem> CategoryOptions { get; set; } = new();
        public List<string> CategoryCodes { get; set; } = new();
        public List<SelectListItem> RowDimOptions { get; set; } = new();
        public List<SelectListItem> ColDimOptions { get; set; } = new();
        public List<SelectListItem> MeasureOptions { get; set; } = new();
        public ReportBuilderResultVm? Result { get; set; }
        public string ChartJson { get; set; } = "";
        public int? SavedId { get; set; }
        public List<SavedReports> SavedReports { get; set; } = new();
        public bool SavedReportsUnavailable { get; set; }
    }

    public class ReportBuilderResultVm
    {
        public bool HasResult { get; set; }
        public bool Pivoted { get; set; }
        public bool IsNet { get; set; }
        public string RowDimLabel { get; set; } = "";
        public string ColDimLabel { get; set; } = "";
        public string MeasureLabel { get; set; } = "";
        public List<string> ColumnKeys { get; set; } = new();
        public List<ReportBuilderRowVm> Rows { get; set; } = new();
        public List<decimal> ColumnTotals { get; set; } = new();
        public decimal GrandTotal { get; set; }
    }

    public class ReportBuilderRowVm
    {
        public string Key { get; set; } = "";
        public List<decimal> Cells { get; set; } = new();
        public decimal Total { get; set; }
    }

    public class ActiveScenario
    {
        public int ScenarioId { get; set; }
        public string ScenarioName { get; set; } = "";
        public int BudgetYear { get; set; }
        public int? EntityId { get; set; }
        public int? DepartmentId { get; set; }
        public decimal CostInflationRate { get; set; }
        public decimal RevenueGrowthRate { get; set; }
        public Dictionary<int, WhatIfScenarioProjectRates> ProjectRates { get; set; } = new();
    }

    public class GlSummaryRowVm
    {
        public int EntityId { get; set; }
        public string EntityCode { get; set; } = "";
        public string EntityName { get; set; } = "";
        public string GLCode { get; set; } = "";
        public string GLName { get; set; } = "";
        public decimal Revenue { get; set; }
        public decimal Capex { get; set; }
        public decimal Opex { get; set; }
        public decimal Hr { get; set; }
        public decimal Net { get; set; }
    }

    public class ProjectCostRowVm
    {
        public int ProjectId { get; set; }
        public int EntityId { get; set; }
        public string EntityCode { get; set; } = "";
        public string EntityName { get; set; } = "";
        public string ProjectCode { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public decimal Revenue { get; set; }
        public decimal Capex { get; set; }
        public decimal Opex { get; set; }
        public decimal Hr { get; set; }
        public decimal TotalExpense { get; set; }
        public decimal Net { get; set; }
    }

    public class ActivityCostRowVm
    {
        public int ActivityId { get; set; }
        public int EntityId { get; set; }
        public string EntityCode { get; set; } = "";
        public string EntityName { get; set; } = "";
        public string ProgramCode { get; set; } = "";
        public string ProgramName { get; set; } = "";
        public string ActivityCode { get; set; } = "";
        public string ActivityName { get; set; } = "";
        public decimal Revenue { get; set; }
        public decimal Capex { get; set; }
        public decimal Opex { get; set; }
        public decimal Hr { get; set; }
        // Net step-down allocation folded into this activity (after-allocation report only; 0 otherwise).
        public decimal Allocated { get; set; }
        public decimal TotalExpense { get; set; }
        public decimal Net { get; set; }
        public decimal Forecast1TotalExpense { get; set; }
        public decimal Forecast2TotalExpense { get; set; }
        public decimal Forecast1Net { get; set; }
        public decimal Forecast2Net { get; set; }
    }

    public class ActivityCostDetailVm
    {
        public int ActivityId { get; set; }
        public int Year { get; set; }
        public string ActivityLabel { get; set; } = "";
        public string ProgramLabel { get; set; } = "";
        public List<ActivityCostLineVm> OpexLines { get; set; } = new();
        public List<ActivityCostLineVm> CapexLines { get; set; } = new();
        public List<ActivityCostLineVm> RevenueLines { get; set; } = new();
        public List<ActivityHrLineVm> HrLines { get; set; } = new();
        public List<ActivityReallocLineVm> ReallocLines { get; set; } = new();
    }

    public class CostTxnExportRow
    {
        public string EntityCode { get; set; } = "";
        public string EntityName { get; set; } = "";
        public string ProgramCode { get; set; } = "";
        public string ProgramName { get; set; } = "";
        public string ActivityCode { get; set; } = "";
        public string ActivityName { get; set; } = "";
        public string ProjectCode { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string Source { get; set; } = "";
        public string Description { get; set; } = "";
        public string GLCode { get; set; } = "";
        public string GLName { get; set; } = "";
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Amount { get; set; }
        public decimal Forecast1 { get; set; }
        public decimal Forecast2 { get; set; }
    }

    public class ReallocExportRow
    {
        public string EntityLabel { get; set; } = "";
        public string SourceProgram { get; set; } = "";
        public string SourceActivity { get; set; } = "";
        public string TargetProgram { get; set; } = "";
        public string TargetActivity { get; set; } = "";
        public string Category { get; set; } = "";
        public decimal AllocationPct { get; set; }
        public decimal Amount { get; set; }
    }

    public class ProjectCostDetailVm
    {
        public int ProjectId { get; set; }
        public int Year { get; set; }
        public string ProjectLabel { get; set; } = "";
        public List<ActivityCostLineVm> OpexLines { get; set; } = new();
        public List<ActivityCostLineVm> CapexLines { get; set; } = new();
        public List<ActivityCostLineVm> RevenueLines { get; set; } = new();
        public List<ActivityHrLineVm> HrLines { get; set; } = new();
    }

    public class GlDetailVm
    {
        public int Year { get; set; }
        public string GlLabel { get; set; } = "";
        public List<GlLineVm> RevenueLines { get; set; } = new();
        public List<GlLineVm> CapexLines { get; set; } = new();
        public List<GlLineVm> OpexLines { get; set; } = new();
        public List<GlHrLineVm> HrLines { get; set; } = new();
    }

    public class GlLineVm
    {
        public string CategoryCode { get; set; } = "";
        public string ProgramLabel { get; set; } = "";
        public string ActivityLabel { get; set; } = "";
        public string ItemLabel { get; set; } = "";
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Amount { get; set; }
        public decimal Forecast1 { get; set; }
        public decimal Forecast2 { get; set; }
    }

    public class GlHrLineVm
    {
        public string EmployeeId { get; set; } = "";
        public string EmployeeName { get; set; } = "";
        public string EntityName { get; set; } = "";
        public string DepartmentName { get; set; } = "";
        public decimal Amount { get; set; }
    }

    public class ActivityCostLineVm
    {
        public string CategoryCode { get; set; } = "";
        public string ItemCode { get; set; } = "";
        public string ItemName { get; set; } = "";
        public string GLCode { get; set; } = "";
        public string GLName { get; set; } = "";
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Amount { get; set; }
        public decimal Forecast1 { get; set; }
        public decimal Forecast2 { get; set; }
    }

    public class ActivityHrLineVm
    {
        public string EmployeeId { get; set; } = "";
        public string EmployeeName { get; set; } = "";
        public string GLCode { get; set; } = "";
        public string GLName { get; set; } = "";
        public decimal Amount { get; set; }
    }

    public class ActivityReallocLineVm
    {
        public string Direction { get; set; } = "";
        public string SourceProgram { get; set; } = "";
        public string SourceActivity { get; set; } = "";
        public string TargetProgram { get; set; } = "";
        public string TargetActivity { get; set; } = "";
        public string Category { get; set; } = "";
        public decimal AllocationPct { get; set; }
        public decimal Amount { get; set; }
    }

    // Employee cost per hour. Rows come straight from core.vw_HrEmployeeHourlyRates,
    // which derives everything below AnnualCost from the work calendar.
    public class HrHourlyRateVm
    {
        public List<vw_HrEmployeeHourlyRates> Rows { get; set; } = new();

        public int EmployeeCount { get; set; }
        public int VacancyCount { get; set; }

        // Employees whose budget year has no work calendar, so no rate could be
        // produced. Surfaced rather than hidden - a silent zero here would be
        // read as "this person is free".
        public int MissingCalendarCount { get; set; }

        public decimal TotalAnnualCost { get; set; }
        public decimal TotalEffectiveHours { get; set; }

        // Total cost over total hours - NOT the average of the per-employee rates.
        // Averaging ratios would weight a cleaner the same as a director and
        // understate what an organisational hour actually costs.
        public decimal? BlendedRatePerHour { get; set; }

        public decimal? BlendedNominalRatePerHour { get; set; }
    }

    public class HrAllocationRowVm
    {
        public string EmployeeId { get; set; } = "";
        public string EmployeeName { get; set; } = "";
        public string EntityName { get; set; } = "";
        public string DepartmentName { get; set; } = "";
        public string ProgramCode { get; set; } = "";
        public string ProgramName { get; set; } = "";
        public string ActivityCode { get; set; } = "";
        public string ActivityName { get; set; } = "";
        public string GLCode { get; set; } = "";
        public string GLName { get; set; } = "";
        public decimal Amount { get; set; }
    }

    public class EntityBudgetSummaryRowVm
    {
        public int EntityId { get; set; }
        public string EntityCode { get; set; } = "";
        public string EntityName { get; set; } = "";
        public decimal Revenue { get; set; }
        public decimal HrCost { get; set; }
        public int HeadCount { get; set; }
        public decimal Capex { get; set; }
        public decimal Opex { get; set; }
        public decimal TotalExpense { get; set; }
        public decimal Net { get; set; }
        public decimal Forecast1Revenue { get; set; }
        public decimal Forecast1TotalExpense { get; set; }
        public decimal Forecast1Net { get; set; }
        public decimal Forecast2Revenue { get; set; }
        public decimal Forecast2TotalExpense { get; set; }
        public decimal Forecast2Net { get; set; }
    }

    public class TrendRowVm
    {
        public string Line { get; set; } = "";
        public decimal Actual { get; set; }
        public decimal Budget { get; set; }
        public decimal Forecast1 { get; set; }
        public decimal Forecast2 { get; set; }
    }
}
