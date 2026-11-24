using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using GovBudget.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Controllers
{
    // The management workbook: one Excel file that shows the same budget three ways -
    // traditional (by GL account), performance-based (by programme and activity, after the
    // step-down cost allocation) and the HR allocation behind it. Every sheet is built from the
    // ledgers the on-screen reports use, so the figures agree with them and with each other.
    public partial class ReportsController
    {
        private const string PbbMoney = "#,##0.00;[Red](#,##0.00);-";
        private const string PbbPercent = "0.0%;[Red]-0.0%;-";
        private const string PbbNumber = "#,##0.##;[Red]-#,##0.##;-";

        private enum PbbKind { Text, Money, Percent, Number }
        private enum PbbRowStyle { Normal, Subtotal, Total, Muted }

        private sealed record PbbRow(object?[] Cells, PbbRowStyle Style = PbbRowStyle.Normal);

        [HttpGet]
        public async Task<IActionResult> PbbWorkbook(int? year = null, int? entityId = null)
        {
            var selectedYear = year ?? HttpContext.Session.GetInt("ctxYear") ?? DateTime.Now.Year;

            var isAdmin = User.IsInRole("ADMIN");
            var isSysAdmin = User.IsInRole("SYSADMIN");
            var scopedEntityId = GetEntityClaimId();
            var isGlobalAdmin = IsGlobalAdmin(isAdmin, isSysAdmin, scopedEntityId);
            var effectiveEntityId = ResolveEntityScope(isAdmin || isSysAdmin, isGlobalAdmin, entityId);
            var allEntities = !effectiveEntityId.HasValue;

            var fileLabel = await GetEntityLabel(effectiveEntityId);
            var entityTitle = allEntities
                ? "All entities"
                : await _db.Entities.AsNoTracking()
                      .Where(e => e.EntityId == effectiveEntityId!.Value)
                      .Select(e => e.EntityCode + " - " + e.EntityName)
                      .FirstOrDefaultAsync() ?? fileLabel;

            // ---- the two ledgers: HR as imported (GL lens) and HR as allocated (programme lens) ----
            var traditional = await GetLedgerEntries(selectedYear, effectiveEntityId, HrLedgerMode.ImportedOnly);
            var pbb = await GetLedgerEntries(selectedYear, effectiveEntityId, HrLedgerMode.AllocatedOnly);
            var (adjustments, _) = await BuildActivityAllocationAdjustments(pbb, selectedYear, effectiveEntityId);
            var realloc = await GetReallocationExportRows(selectedYear, effectiveEntityId);
            var scenario = await GetActiveScenario(selectedYear);

            static bool IsExpense(LedgerEntry r) => r.CategoryCode == "HR" || r.CategoryCode == "OPEX" || r.CategoryCode == "CAPEX";
            static decimal Cat(IEnumerable<LedgerEntry> rows, string cat) => rows.Where(r => r.CategoryCode == cat).Sum(r => r.Amount);

            var tradRevenue = Cat(traditional, "REVENUE");
            var tradHr = Cat(traditional, "HR");
            var tradOpex = Cat(traditional, "OPEX");
            var tradCapex = Cat(traditional, "CAPEX");
            var tradExpense = tradHr + tradOpex + tradCapex;

            // HR that was imported but not yet allocated to any activity. It belongs to no
            // programme, so it is carried as its own line to keep both views equal in total.
            var unallocatedHr = tradHr - Cat(pbb, "HR");
            var hasUnallocatedHr = Math.Abs(unallocatedHr) > 0.005m;

            // ---- supporting data ----
            var scopeEntityIds = pbb.Select(r => r.EntityId).Concat(traditional.Select(r => r.EntityId))
                .Where(id => id > 0).Distinct().ToList();

            var programsQ = _db.Programs.AsNoTracking().AsQueryable();
            if (!allEntities) programsQ = programsQ.Where(p => p.EntityId == effectiveEntityId!.Value);
            var programIds = await programsQ.Select(p => p.ProgramId).ToListAsync();

            var activityProgram = await _db.Activities.AsNoTracking()
                .Where(a => programIds.Contains(a.ProgramId))
                .Select(a => new { a.ActivityId, a.ProgramId })
                .ToListAsync();
            var activityIds = activityProgram.Select(a => a.ActivityId).ToList();

            var outputs = (await _db.ActivityOutputs.AsNoTracking()
                    .Where(o => o.BudgetYear == selectedYear && o.IsPrimary && activityIds.Contains(o.ActivityId))
                    .ToListAsync())
                .GroupBy(o => o.ActivityId).ToDictionary(g => g.Key, g => g.First());

            var kpisQ = _db.Kpis.AsNoTracking().Where(k => k.BudgetYear == selectedYear);
            if (!allEntities) kpisQ = kpisQ.Where(k => k.EntityId == effectiveEntityId!.Value);
            var kpis = await kpisQ.Select(k => new { k.KpiId, k.ProgramId, k.ActivityId }).ToListAsync();
            var kpisByActivity = kpis.Where(k => k.ActivityId != null)
                .GroupBy(k => k.ActivityId!.Value).ToDictionary(g => g.Key, g => g.Count());
            var activityToProgram = activityProgram.ToDictionary(a => a.ActivityId, a => a.ProgramId);
            var kpisByProgram = kpis
                .Select(k => k.ProgramId ?? (k.ActivityId != null && activityToProgram.TryGetValue(k.ActivityId.Value, out var pid) ? pid : 0))
                .Where(pid => pid > 0)
                .GroupBy(pid => pid).ToDictionary(g => g.Key, g => g.Count());

            var hrQ = _db.HrEmployeeCosts.AsNoTracking()
                .Include(x => x.HrEmployeeCostAllocations).ThenInclude(a => a.Activity).ThenInclude(a => a.Program)
                .Include(x => x.HrEmployeeCostAllocations).ThenInclude(a => a.Project)
                .Where(x => x.BudgetYear == selectedYear);
            if (!allEntities) hrQ = hrQ.Where(x => x.EntityId == effectiveEntityId!.Value);
            var employees = await hrQ.OrderBy(x => x.EntityName).ThenBy(x => x.DepartmentName).ThenBy(x => x.EmployeeName).ToListAsync();

            // ---- programme and activity cost, direct and after allocation ----
            var expenseRows = pbb.Where(IsExpense).ToList();
            var revenueRows = pbb.Where(r => r.CategoryCode == "REVENUE").ToList();

            var programmes = expenseRows.Concat(revenueRows).Concat(adjustments)
                .GroupBy(r => (r.EntityId, r.ProgramId))
                .Select(g =>
                {
                    var first = g.First();
                    var direct = g.Where(r => r.GLType != "Allocated").ToList();
                    var hr = Cat(direct, "HR");
                    var opex = Cat(direct, "OPEX");
                    var capex = Cat(direct, "CAPEX");
                    var allocated = g.Where(r => r.GLType == "Allocated").Sum(r => r.Amount);
                    return new PbbProgramme
                    {
                        EntityCode = first.EntityCode,
                        ProgramId = g.Key.ProgramId,
                        Code = g.Key.ProgramId > 0 ? first.ProgramCode : "",
                        Name = g.Key.ProgramId > 0 ? first.ProgramName : "(Not assigned to a programme)",
                        Type = g.Key.ProgramId > 0 ? (string.IsNullOrWhiteSpace(first.ProgramType) ? "Mandate" : first.ProgramType) : "",
                        Activities = g.Where(r => r.ActivityId > 0).Select(r => r.ActivityId).Distinct().Count(),
                        Kpis = kpisByProgram.GetValueOrDefault(g.Key.ProgramId),
                        Revenue = Cat(direct, "REVENUE"),
                        Hr = hr,
                        Opex = opex,
                        Capex = capex,
                        Allocated = allocated
                    };
                })
                .OrderBy(p => p.ProgramId == 0).ThenBy(p => p.EntityCode).ThenBy(p => p.Code)
                .ToList();

            var activities = expenseRows.Concat(revenueRows).Concat(adjustments)
                .GroupBy(r => (r.EntityId, r.ProgramId, r.ActivityId))
                .Select(g =>
                {
                    var first = g.FirstOrDefault(r => r.GLType != "Allocated") ?? g.First();
                    var direct = g.Where(r => r.GLType != "Allocated").ToList();
                    outputs.TryGetValue(g.Key.ActivityId, out var o);
                    return new PbbActivity
                    {
                        EntityCode = first.EntityCode,
                        ProgramId = g.Key.ProgramId,
                        ProgramCode = g.Key.ProgramId > 0 ? first.ProgramCode : "",
                        ProgramName = g.Key.ProgramId > 0 ? first.ProgramName : "(Not assigned to a programme)",
                        ActivityId = g.Key.ActivityId,
                        Code = g.Key.ActivityId > 0 ? first.ActivityCode : "",
                        Name = g.Key.ActivityId > 0 ? first.ActivityName : "(Programme level - no activity)",
                        Revenue = Cat(direct, "REVENUE"),
                        Hr = Cat(direct, "HR"),
                        Opex = Cat(direct, "OPEX"),
                        Capex = Cat(direct, "CAPEX"),
                        Allocated = g.Where(r => r.GLType == "Allocated").Sum(r => r.Amount),
                        OutputMeasure = o?.OutputMeasure ?? "",
                        OutputVolume = o?.OutputVolume ?? 0m,
                        Kpis = kpisByActivity.GetValueOrDefault(g.Key.ActivityId)
                    };
                })
                .OrderBy(a => a.ProgramId == 0).ThenBy(a => a.EntityCode).ThenBy(a => a.ProgramCode)
                .ThenBy(a => a.ActivityId == 0).ThenBy(a => a.Code)
                .ToList();

            var pbbTotal = programmes.Sum(p => p.Total) + (hasUnallocatedHr ? unallocatedHr : 0m);

            using var wb = new XLWorkbook();
            wb.Style.Font.FontName = "Calibri";
            wb.Style.Font.FontSize = 10;

            // =====================================================================
            // 1) Summary
            // =====================================================================
            {
                var ws = wb.Worksheets.Add("Summary");
                ws.ShowGridLines = false;
                ws.SetTabColor(XLColor.FromHtml(BrandColors.AccentHex));
                var row = PbbTitle(ws, "Performance-Based Budget Report", entityTitle, selectedYear, 7,
                    scenario != null ? $"What-if scenario applied to OPEX, CAPEX and revenue: {scenario.ScenarioName}" : null);

                row = PbbSection(ws, row, "Traditional view - what the money is spent on", 7);
                row = PbbTable(ws, row,
                    new[] { "Category", "Budget", "% of expenditure" },
                    new[] { PbbKind.Text, PbbKind.Money, PbbKind.Percent },
                    new List<PbbRow>
                    {
                        new(new object?[] { "HR (salaries and staff cost)", tradHr, Share(tradHr, tradExpense) }),
                        new(new object?[] { "OPEX (operating expenditure)", tradOpex, Share(tradOpex, tradExpense) }),
                        new(new object?[] { "CAPEX (capital expenditure)", tradCapex, Share(tradCapex, tradExpense) }),
                        new(new object?[] { "Total expenditure", tradExpense, Share(tradExpense, tradExpense) }, PbbRowStyle.Total),
                        new(new object?[] { "Revenue", tradRevenue, null }),
                        new(new object?[] { "Surplus / (deficit)", tradRevenue - tradExpense, null }, PbbRowStyle.Subtotal)
                    }, filter: false);

                row = PbbSection(ws, row + 1, "Performance-based view - what the money delivers (after cost allocation)", 7);
                // Programmes that carry no cost (e.g. revenue with no programme) stay on the detail sheets only.
                var costed = programmes.Where(p => p.Direct != 0m || p.Allocated != 0m).ToList();
                var progRows = costed.Select(p => new PbbRow(new object?[]
                {
                    PbbLabel(allEntities ? p.EntityCode : "", p.Code, p.Name), p.Type, p.Activities,
                    p.Direct, p.Allocated, p.Total, Share(p.Total, pbbTotal)
                })).ToList();
                if (hasUnallocatedHr)
                    progRows.Add(new PbbRow(new object?[] { "HR not yet allocated to an activity", "", null, unallocatedHr, 0m, unallocatedHr, Share(unallocatedHr, pbbTotal) }, PbbRowStyle.Muted));
                progRows.Add(new PbbRow(new object?[]
                {
                    "Total expenditure", "", programmes.Sum(p => p.Activities),
                    programmes.Sum(p => p.Direct) + (hasUnallocatedHr ? unallocatedHr : 0m),
                    programmes.Sum(p => p.Allocated), pbbTotal, Share(pbbTotal, pbbTotal)
                }, PbbRowStyle.Total));
                var progStart = row + 1;
                row = PbbTable(ws, row,
                    new[] { "Programme", "Type", "Activities", "Direct cost", "Allocated in / (out)", "Total cost", "% of total" },
                    new[] { PbbKind.Text, PbbKind.Text, PbbKind.Number, PbbKind.Money, PbbKind.Money, PbbKind.Money, PbbKind.Percent },
                    progRows, filter: false);
                if (costed.Count > 0)
                    ws.Range(progStart, 7, progStart + costed.Count - 1, 7).AddConditionalFormat()
                        .DataBar(XLColor.FromHtml(BrandColors.AccentHex), false).LowestValue().HighestValue();

                row = PbbSection(ws, row + 1, "Reconciliation - both views describe the same budget", 7);
                var diff = tradExpense - pbbTotal;
                row = PbbTable(ws, row,
                    new[] { "Check", "Amount" },
                    new[] { PbbKind.Text, PbbKind.Money },
                    new List<PbbRow>
                    {
                        new(new object?[] { "Total expenditure by GL account (traditional)", tradExpense }),
                        new(new object?[] { "Total expenditure by programme (performance-based)", pbbTotal }),
                        new(new object?[] { Math.Abs(diff) < 0.01m ? "Difference - the two views agree" : "Difference - to be investigated", diff }, PbbRowStyle.Subtotal)
                    }, filter: false);

                var allocatedHr = employees.Sum(e => e.HrEmployeeCostAllocations.Sum(a => a.AllocatedAmount));
                var importedHr = employees.Sum(e => e.AnnualCost);
                row = PbbSection(ws, row + 1, "Key facts", 7);
                row = PbbTable(ws, row,
                    new[] { "Item", "Value" },
                    new[] { PbbKind.Text, PbbKind.Text },
                    new List<PbbRow>
                    {
                        new(new object?[] { "Programmes with budget", programmes.Count(p => p.ProgramId > 0).ToString() }),
                        new(new object?[] { "Activities with budget", activities.Count(a => a.ActivityId > 0).ToString() }),
                        new(new object?[] { "KPIs defined for the year", kpis.Count.ToString() }),
                        new(new object?[] { "Activities with a recorded output", activities.Count(a => a.OutputVolume > 0).ToString() }),
                        new(new object?[] { "Employees in the HR cost file", employees.Count.ToString() }),
                        new(new object?[] { "HR cost allocated to activities", importedHr > 0 ? (allocatedHr / importedHr).ToString("0.0%") : "-" }),
                        new(new object?[] { "Cost allocation between programmes", realloc.Count > 0 ? "Posted - totals include allocated cost" : "No posted run - totals are direct cost only" })
                    }, filter: false);

                row = PbbSection(ws, row + 1, "In this workbook", 7);
                var guide = new (string Sheet, string What)[]
                {
                    ("Traditional (GL)", "Budget by GL account within Revenue, HR, OPEX and CAPEX."),
                    ("Programmes", "Cost of each programme: direct, allocated and total, with revenue."),
                    ("Activities", "Cost of each activity after allocation, with output and cost per output."),
                    ("Programme x GL", "Crosswalk: each GL account split across the programmes (direct cost)."),
                    ("Projects", "Each project's revenue and cost, and whether it comes from HR, OPEX or CAPEX."),
                    ("HR Allocation", "Each employee's annual cost and how it is allocated to activities."),
                    ("Cost Allocation", "The posted transfers of cost from support to mandate programmes.")
                };
                foreach (var g in guide)
                {
                    ws.Cell(row, 1).Value = g.Sheet;
                    ws.Cell(row, 1).Style.Font.Bold = true;
                    ws.Cell(row, 2).Value = g.What;
                    ws.Range(row, 2, row, 7).Merge();
                    row++;
                }

                ws.Column(1).Width = 52;
                ws.Column(2).Width = 18;
                ws.Column(3).Width = 16;
                ws.Columns(4, 7).Width = 20;
                PbbPrint(ws, landscape: false);
            }

            // =====================================================================
            // 2) Traditional (GL)
            // =====================================================================
            {
                var ws = wb.Worksheets.Add("Traditional (GL)");
                var row = PbbTitle(ws, "Traditional view - budget by GL account", entityTitle, selectedYear, 7, null);
                var rows = new List<PbbRow>();
                foreach (var cat in new[] { "REVENUE", "HR", "OPEX", "CAPEX" })
                {
                    var catRows = traditional.Where(r => r.CategoryCode == cat).ToList();
                    if (catRows.Count == 0) continue;
                    var catTotal = catRows.Sum(r => r.Amount);
                    foreach (var g in catRows.GroupBy(r => (r.GLCode, r.GLName)).OrderBy(g => g.Key.GLCode))
                    {
                        var amt = g.Sum(r => r.Amount);
                        rows.Add(new PbbRow(new object?[]
                        {
                            cat, g.Key.GLCode, g.Key.GLName, amt, g.Sum(r => r.Forecast1Amount), g.Sum(r => r.Forecast2Amount),
                            cat == "REVENUE" ? Share(amt, catTotal) : Share(amt, tradExpense)
                        }));
                    }
                    rows.Add(new PbbRow(new object?[]
                    {
                        "Total " + cat, "", "", catTotal, catRows.Sum(r => r.Forecast1Amount), catRows.Sum(r => r.Forecast2Amount),
                        cat == "REVENUE" ? Share(catTotal, catTotal) : Share(catTotal, tradExpense)
                    }, PbbRowStyle.Subtotal));
                }
                var exp = traditional.Where(IsExpense).ToList();
                rows.Add(new PbbRow(new object?[] { "Total expenditure", "", "", tradExpense, exp.Sum(r => r.Forecast1Amount), exp.Sum(r => r.Forecast2Amount), Share(tradExpense, tradExpense) }, PbbRowStyle.Total));
                rows.Add(new PbbRow(new object?[] { "Surplus / (deficit)", "", "", tradRevenue - tradExpense, null, null, null }, PbbRowStyle.Total));

                PbbTable(ws, row,
                    new[] { "Category", "GL code", "GL account", "Budget", "Forecast 1", "Forecast 2", "% of category total" },
                    new[] { PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Money, PbbKind.Money, PbbKind.Money, PbbKind.Percent },
                    rows);
                ws.Cell(row - 1, 1).Value = "Share is of total expenditure for HR, OPEX and CAPEX accounts, and of total revenue for revenue accounts.";
                ws.Cell(row - 1, 1).Style.Font.Italic = true;
                ws.Cell(row - 1, 1).Style.Font.FontColor = XLColor.Gray;
                PbbWidths(ws, 16, 16, 46, 18, 18, 18, 18);
                PbbPrint(ws);
            }

            // =====================================================================
            // 3) Programmes
            // =====================================================================
            {
                var ws = wb.Worksheets.Add("Programmes");
                var row = PbbTitle(ws, "Performance-based view - cost by programme", entityTitle, selectedYear, 13, null);
                var rows = programmes.Select(p => new PbbRow(new object?[]
                {
                    p.EntityCode, p.Code, p.Name, p.Type, p.Activities, p.Kpis,
                    p.Hr, p.Opex, p.Capex, p.Direct, p.Allocated, p.Total, Share(p.Total, pbbTotal), p.Revenue, p.Revenue - p.Total
                })).ToList();
                if (hasUnallocatedHr)
                    rows.Add(new PbbRow(new object?[] { "", "", "HR not yet allocated to an activity", "", null, null, unallocatedHr, 0m, 0m, unallocatedHr, 0m, unallocatedHr, Share(unallocatedHr, pbbTotal), 0m, -unallocatedHr }, PbbRowStyle.Muted));
                rows.Add(new PbbRow(new object?[]
                {
                    "Total", "", "", "", programmes.Sum(p => p.Activities), programmes.Sum(p => p.Kpis),
                    programmes.Sum(p => p.Hr) + (hasUnallocatedHr ? unallocatedHr : 0m), programmes.Sum(p => p.Opex), programmes.Sum(p => p.Capex),
                    programmes.Sum(p => p.Direct) + (hasUnallocatedHr ? unallocatedHr : 0m), programmes.Sum(p => p.Allocated), pbbTotal, Share(pbbTotal, pbbTotal),
                    programmes.Sum(p => p.Revenue), programmes.Sum(p => p.Revenue) - pbbTotal
                }, PbbRowStyle.Total));

                var first = row + 1;
                PbbTable(ws, row,
                    new[] { "Entity", "Programme code", "Programme", "Type", "Activities", "KPIs", "HR", "OPEX", "CAPEX", "Direct cost", "Allocated in / (out)", "Total cost", "% of total", "Revenue", "Net (revenue - cost)" },
                    new[] { PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Number, PbbKind.Number, PbbKind.Money, PbbKind.Money, PbbKind.Money, PbbKind.Money, PbbKind.Money, PbbKind.Money, PbbKind.Percent, PbbKind.Money, PbbKind.Money },
                    rows);
                if (programmes.Count > 0)
                    ws.Range(first, 13, first + programmes.Count - 1, 13).AddConditionalFormat()
                        .DataBar(XLColor.FromHtml(BrandColors.AccentHex), false).LowestValue().HighestValue();
                if (!allEntities) ws.Column(1).Hide();
                PbbWidths(ws, 10, 16, 44, 12, 11, 8, 17, 17, 17, 17, 19, 18, 12, 17, 19);
                PbbPrint(ws);
            }

            // =====================================================================
            // 4) Activities
            // =====================================================================
            {
                var ws = wb.Worksheets.Add("Activities");
                var row = PbbTitle(ws, "Performance-based view - cost by activity", entityTitle, selectedYear, 15, null);
                var rows = new List<PbbRow>();
                foreach (var g in activities.GroupBy(a => (a.EntityCode, a.ProgramId, a.ProgramCode, a.ProgramName)))
                {
                    foreach (var a in g)
                    {
                        rows.Add(new PbbRow(new object?[]
                        {
                            a.EntityCode, PbbLabel("", a.ProgramCode, a.ProgramName), a.Code, a.Name,
                            a.Hr, a.Opex, a.Capex, a.Direct, a.Allocated, a.Total, Share(a.Total, pbbTotal), a.Revenue,
                            a.OutputMeasure, a.OutputVolume > 0 ? a.OutputVolume : (decimal?)null,
                            a.OutputVolume > 0 && a.Total != 0m ? Math.Round(a.Total / a.OutputVolume, 2) : (decimal?)null, a.Kpis
                        }));
                    }
                    rows.Add(new PbbRow(new object?[]
                    {
                        g.Key.EntityCode, "Total " + PbbLabel("", g.Key.ProgramCode, g.Key.ProgramName), "", "",
                        g.Sum(a => a.Hr), g.Sum(a => a.Opex), g.Sum(a => a.Capex), g.Sum(a => a.Direct), g.Sum(a => a.Allocated),
                        g.Sum(a => a.Total), Share(g.Sum(a => a.Total), pbbTotal), g.Sum(a => a.Revenue), "", null, null, g.Sum(a => a.Kpis)
                    }, PbbRowStyle.Subtotal));
                }
                if (hasUnallocatedHr)
                    rows.Add(new PbbRow(new object?[] { "", "HR not yet allocated to an activity", "", "", unallocatedHr, 0m, 0m, unallocatedHr, 0m, unallocatedHr, Share(unallocatedHr, pbbTotal), 0m, "", null, null, null }, PbbRowStyle.Muted));
                rows.Add(new PbbRow(new object?[]
                {
                    "Total", "", "", "",
                    activities.Sum(a => a.Hr) + (hasUnallocatedHr ? unallocatedHr : 0m), activities.Sum(a => a.Opex), activities.Sum(a => a.Capex),
                    activities.Sum(a => a.Direct) + (hasUnallocatedHr ? unallocatedHr : 0m), activities.Sum(a => a.Allocated),
                    activities.Sum(a => a.Total) + (hasUnallocatedHr ? unallocatedHr : 0m), Share(pbbTotal, pbbTotal), activities.Sum(a => a.Revenue), "", null, null, activities.Sum(a => a.Kpis)
                }, PbbRowStyle.Total));

                PbbTable(ws, row,
                    new[] { "Entity", "Programme", "Activity code", "Activity", "HR", "OPEX", "CAPEX", "Direct cost", "Allocated in / (out)", "Total cost", "% of total", "Revenue", "Output measure", "Output volume", "Cost per output", "KPIs" },
                    new[] { PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Money, PbbKind.Money, PbbKind.Money, PbbKind.Money, PbbKind.Money, PbbKind.Money, PbbKind.Percent, PbbKind.Money, PbbKind.Text, PbbKind.Number, PbbKind.Money, PbbKind.Number },
                    rows);
                if (!allEntities) ws.Column(1).Hide();
                PbbWidths(ws, 10, 38, 16, 44, 16, 16, 16, 17, 19, 18, 11, 16, 30, 15, 16, 8);
                PbbPrint(ws);
            }

            // =====================================================================
            // 5) Programme x GL crosswalk (direct cost, before allocation between programmes)
            // =====================================================================
            {
                var ws = wb.Worksheets.Add("Programme x GL");
                var cols = programmes.Where(p => p.Direct != 0m).Select(p => (Key: (p.EntityCode, p.ProgramId), Header: p.ProgramId > 0 ? PbbLabel(allEntities ? p.EntityCode : "", p.Code, "") : "No programme")).ToList();
                var width = 3 + cols.Count + (hasUnallocatedHr ? 1 : 0) + 1;
                var row = PbbTitle(ws, "Crosswalk - each GL account split across the programmes (direct cost)", entityTitle, selectedYear, Math.Max(width, 6), null);

                var headers = new List<string> { "Category", "GL code", "GL account" };
                headers.AddRange(cols.Select(c => c.Header));
                if (hasUnallocatedHr) headers.Add("HR not allocated");
                headers.Add("Total");
                var kinds = headers.Select((_, i) => i < 3 ? PbbKind.Text : PbbKind.Money).ToArray();

                var tradByGl = traditional.Where(r => r.CategoryCode == "HR")
                    .GroupBy(r => r.GLCode).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
                var rows = new List<PbbRow>();
                var grand = new decimal[cols.Count + (hasUnallocatedHr ? 1 : 0) + 1];
                foreach (var cat in new[] { "HR", "OPEX", "CAPEX" })
                {
                    var catRows = expenseRows.Where(r => r.CategoryCode == cat).ToList();
                    // HR accounts that are imported but have no allocation at all still need a row.
                    var glKeys = catRows.Select(r => (r.GLCode, r.GLName)).Distinct().ToList();
                    if (cat == "HR")
                        foreach (var g in traditional.Where(r => r.CategoryCode == "HR").Select(r => (r.GLCode, r.GLName)).Distinct())
                            if (!glKeys.Any(k => k.GLCode == g.GLCode)) glKeys.Add(g);
                    if (glKeys.Count == 0) continue;

                    var sub = new decimal[grand.Length];
                    foreach (var gl in glKeys.OrderBy(k => k.GLCode))
                    {
                        var cells = new List<object?> { cat, gl.GLCode, gl.GLName };
                        var line = new decimal[grand.Length];
                        for (var i = 0; i < cols.Count; i++)
                            line[i] = catRows.Where(r => r.GLCode == gl.GLCode && (r.EntityCode, r.ProgramId) == cols[i].Key).Sum(r => r.Amount);
                        if (hasUnallocatedHr)
                            line[cols.Count] = cat == "HR" ? tradByGl.GetValueOrDefault(gl.GLCode) - catRows.Where(r => r.GLCode == gl.GLCode).Sum(r => r.Amount) : 0m;
                        line[^1] = line.Take(line.Length - 1).Sum();
                        for (var i = 0; i < line.Length; i++) { cells.Add(line[i]); sub[i] += line[i]; grand[i] += line[i]; }
                        rows.Add(new PbbRow(cells.ToArray()));
                    }
                    var subCells = new List<object?> { "Total " + cat, "", "" };
                    subCells.AddRange(sub.Cast<object?>());
                    rows.Add(new PbbRow(subCells.ToArray(), PbbRowStyle.Subtotal));
                }
                var totalCells = new List<object?> { "Total direct cost", "", "" };
                totalCells.AddRange(grand.Cast<object?>());
                rows.Add(new PbbRow(totalCells.ToArray(), PbbRowStyle.Total));

                PbbTable(ws, row, headers.ToArray(), kinds, rows);
                ws.Column(1).Width = 14; ws.Column(2).Width = 16; ws.Column(3).Width = 42;
                for (var c = 4; c <= headers.Count; c++) ws.Column(c).Width = 17;
                ws.SheetView.FreezeColumns(3);
                PbbPrint(ws);
            }

            // =====================================================================
            // Projects - a second cut of the same budget. A budget line or an HR allocation may
            // carry a project; this sheet shows that part per project and where it came from.
            // =====================================================================
            {
                var ws = wb.Worksheets.Add("Projects");
                var row = PbbTitle(ws, "Projects - revenue and cost by project, with the source of each amount", entityTitle, selectedYear, 9, null);

                // OPEX, CAPEX and revenue: budget lines tagged to a project (same ledger as the other sheets).
                var projectLines = pbb.Where(r => r.ProjectId.HasValue && r.CategoryCode != "HR").ToList();
                // HR: employee cost allocations that name a project.
                var projectHr = employees
                    .SelectMany(e => e.HrEmployeeCostAllocations.Where(a => a.ProjectId != null).Select(a => (Emp: e, Alloc: a)))
                    .ToList();

                var projectMeta = projectLines
                    .Select(r => (Id: r.ProjectId!.Value, Code: r.ProjectCode, Name: r.ProjectName))
                    .Concat(projectHr.Select(x => (Id: x.Alloc.ProjectId!.Value, Code: x.Alloc.Project?.ProjectCode ?? "", Name: x.Alloc.Project?.ProjectName ?? "")))
                    .GroupBy(x => x.Id)
                    .Select(g => g.First())
                    .OrderBy(x => x.Code)
                    .ToList();

                if (projectMeta.Count == 0)
                {
                    ws.Cell(row, 1).Value = "No budget line or HR allocation is tagged to a project for this year.";
                    ws.Cell(row, 1).Style.Font.Italic = true;
                    ws.Column(1).Width = 90;
                }
                else
                {
                    ws.Cell(row - 1, 1).Value = "Project amounts are already included in the totals on the other sheets. This sheet is a separate view by project and adds nothing to them.";
                    ws.Cell(row - 1, 1).Style.Font.Italic = true;
                    ws.Cell(row - 1, 1).Style.Font.FontColor = XLColor.Gray;

                    decimal LineSum(int id, string cat) => projectLines.Where(r => r.ProjectId == id && r.CategoryCode == cat).Sum(r => r.Amount);
                    decimal HrSum(int id) => projectHr.Where(x => x.Alloc.ProjectId == id).Sum(x => x.Alloc.AllocatedAmount);

                    // ---- 1) one line per project ----
                    row = PbbSection(ws, row, "Summary by project", 9);
                    var summary = projectMeta.Select(m =>
                    {
                        var hr = HrSum(m.Id); var opex = LineSum(m.Id, "OPEX"); var capex = LineSum(m.Id, "CAPEX"); var rev = LineSum(m.Id, "REVENUE");
                        return (m.Code, m.Name, Rev: rev, Hr: hr, Opex: opex, Capex: capex, Cost: hr + opex + capex);
                    }).ToList();
                    var allCost = summary.Sum(x => x.Cost);
                    var summaryRows = summary.Select(x => new PbbRow(new object?[]
                    {
                        x.Code, x.Name, x.Hr, x.Opex, x.Capex, x.Cost, Share(x.Cost, tradExpense), x.Rev, x.Rev - x.Cost
                    })).ToList();
                    summaryRows.Add(new PbbRow(new object?[]
                    {
                        "All projects", "", summary.Sum(x => x.Hr), summary.Sum(x => x.Opex), summary.Sum(x => x.Capex), allCost,
                        Share(allCost, tradExpense), summary.Sum(x => x.Rev), summary.Sum(x => x.Rev) - allCost
                    }, PbbRowStyle.Total));
                    summaryRows.Add(new PbbRow(new object?[]
                    {
                        "Share of the budget total", "", Share(summary.Sum(x => x.Hr), tradHr), Share(summary.Sum(x => x.Opex), tradOpex),
                        Share(summary.Sum(x => x.Capex), tradCapex), Share(allCost, tradExpense), null, Share(summary.Sum(x => x.Rev), tradRevenue), null
                    }, PbbRowStyle.Muted));
                    var shareRow = row + summaryRows.Count;
                    row = PbbTable(ws, row,
                        new[] { "Project code", "Project", "HR", "OPEX", "CAPEX", "Total cost", "% of total expenditure", "Revenue", "Net (revenue - cost)" },
                        new[] { PbbKind.Text, PbbKind.Text, PbbKind.Money, PbbKind.Money, PbbKind.Money, PbbKind.Money, PbbKind.Percent, PbbKind.Money, PbbKind.Money },
                        summaryRows, filter: false);
                    // The share line holds percentages under the money columns.
                    ws.Range(shareRow, 3, shareRow, 8).Style.NumberFormat.Format = PbbPercent;

                    // ---- 2) where each amount comes from ----
                    row = PbbSection(ws, row + 1, "Source of each project amount", 9);
                    var detail = new List<PbbRow>();
                    foreach (var m in projectMeta)
                    {
                        var label = PbbLabel("", m.Code, m.Name);
                        var projectTotal = 0m;
                        foreach (var src in new[] { "REVENUE", "HR", "OPEX", "CAPEX" })
                        {
                            var srcTotal = 0m;
                            if (src == "HR")
                            {
                                foreach (var x in projectHr.Where(x => x.Alloc.ProjectId == m.Id).OrderBy(x => x.Emp.EmployeeName))
                                {
                                    detail.Add(new PbbRow(new object?[]
                                    {
                                        label, "HR", x.Emp.GLCode, "",
                                        PbbLabel("", x.Alloc.Activity?.Program?.ProgramCode ?? "", x.Alloc.Activity?.Program?.ProgramName ?? ""),
                                        PbbLabel("", x.Alloc.Activity?.ActivityCode ?? "", x.Alloc.Activity?.ActivityName ?? ""),
                                        PbbLabel("", x.Emp.EmployeeId, x.Emp.EmployeeName), x.Emp.DepartmentName, x.Alloc.AllocatedAmount
                                    }));
                                    srcTotal += x.Alloc.AllocatedAmount;
                                }
                            }
                            else
                            {
                                foreach (var r in projectLines.Where(r => r.ProjectId == m.Id && r.CategoryCode == src)
                                             .OrderBy(r => r.GLCode).ThenBy(r => r.ActivityCode).ThenBy(r => r.ItemCode))
                                {
                                    detail.Add(new PbbRow(new object?[]
                                    {
                                        label, src == "REVENUE" ? "Revenue" : src, r.GLCode, r.GLName,
                                        PbbLabel("", r.ProgramCode, r.ProgramName), PbbLabel("", r.ActivityCode, r.ActivityName),
                                        PbbLabel("", r.ItemCode, r.ItemName), "", r.Amount
                                    }));
                                    srcTotal += r.Amount;
                                }
                            }
                            if (srcTotal != 0m)
                            {
                                detail.Add(new PbbRow(new object?[] { label, "Total " + (src == "REVENUE" ? "Revenue" : src), "", "", "", "", "", "", srcTotal }, PbbRowStyle.Subtotal));
                                if (src != "REVENUE") projectTotal += srcTotal;
                            }
                        }
                        detail.Add(new PbbRow(new object?[] { label, "Total cost (HR + OPEX + CAPEX)", "", "", "", "", "", "", projectTotal }, PbbRowStyle.Total));
                    }
                    PbbTable(ws, row,
                        new[] { "Project", "Source", "GL code", "GL account", "Programme", "Activity", "Item / employee", "Cost center (HR)", "Amount" },
                        new[] { PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Money },
                        detail);
                    PbbWidths(ws, 34, 30, 17, 30, 34, 38, 36, 26, 20);
                }
                PbbPrint(ws);
            }

            // =====================================================================
            // 6) HR Allocation
            // =====================================================================
            {
                var ws = wb.Worksheets.Add("HR Allocation");
                var row = PbbTitle(ws, "HR cost and its allocation to activities", entityTitle, selectedYear, 12, null);
                var rows = new List<PbbRow>();
                foreach (var e in employees)
                {
                    var allocated = 0m;
                    foreach (var a in e.HrEmployeeCostAllocations.OrderBy(a => a.Activity?.ActivityCode))
                    {
                        allocated += a.AllocatedAmount;
                        rows.Add(new PbbRow(new object?[]
                        {
                            e.EntityName, e.DepartmentName, e.EmployeeId, e.EmployeeName, e.Occupation ?? "", e.GLCode, e.AnnualCost,
                            PbbLabel("", a.Activity?.Program?.ProgramCode ?? "", a.Activity?.Program?.ProgramName ?? ""),
                            PbbLabel("", a.Activity?.ActivityCode ?? "", a.Activity?.ActivityName ?? ""),
                            a.Project != null ? PbbLabel("", a.Project.ProjectCode, a.Project.ProjectName) : "",
                            a.AllocatedAmount, Share(a.AllocatedAmount, e.AnnualCost)
                        }));
                    }
                    var remaining = e.AnnualCost - allocated;
                    if (Math.Abs(remaining) > 0.01m)
                        rows.Add(new PbbRow(new object?[]
                        {
                            e.EntityName, e.DepartmentName, e.EmployeeId, e.EmployeeName, e.Occupation ?? "", e.GLCode, e.AnnualCost,
                            remaining > 0 ? "(Not allocated)" : "(Over-allocated)", "", "", remaining, Share(remaining, e.AnnualCost)
                        }, PbbRowStyle.Muted));
                }
                rows.Add(new PbbRow(new object?[]
                {
                    "Total", "", employees.Count + " employee(s)", "", "", "", employees.Sum(e => e.AnnualCost), "", "", "",
                    employees.Sum(e => e.HrEmployeeCostAllocations.Sum(a => a.AllocatedAmount)),
                    Share(employees.Sum(e => e.HrEmployeeCostAllocations.Sum(a => a.AllocatedAmount)), employees.Sum(e => e.AnnualCost))
                }, PbbRowStyle.Total));

                PbbTable(ws, row,
                    new[] { "Entity", "Cost center", "Employee ID", "Employee", "Occupation", "GL code", "Annual cost", "Programme", "Activity", "Project", "Allocated amount", "% of annual cost" },
                    new[] { PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Money, PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Money, PbbKind.Percent },
                    rows);
                ws.Cell(row - 1, 1).Value = "Annual cost repeats on each allocation line of an employee; the total counts every employee once. The 'Allocated amount' total is what reaches the programmes.";
                ws.Cell(row - 1, 1).Style.Font.Italic = true;
                ws.Cell(row - 1, 1).Style.Font.FontColor = XLColor.Gray;
                PbbWidths(ws, 26, 28, 14, 30, 24, 14, 17, 36, 40, 26, 18, 14);
                PbbPrint(ws);
            }

            // =====================================================================
            // 7) Cost Allocation (posted step-down transfers)
            // =====================================================================
            {
                var ws = wb.Worksheets.Add("Cost Allocation");
                var row = PbbTitle(ws, "Cost allocation - transfers from support to mandate programmes", entityTitle, selectedYear, 8, null);
                if (realloc.Count == 0)
                {
                    ws.Cell(row, 1).Value = "No cost allocation run is posted for this year, so programme and activity totals in this workbook are direct cost only.";
                    ws.Cell(row, 1).Style.Font.Italic = true;
                    ws.Column(1).Width = 110;
                }
                else
                {
                    var rows = realloc.Select(r => new PbbRow(new object?[]
                    {
                        r.EntityLabel, r.SourceProgram, r.SourceActivity, r.TargetProgram, r.TargetActivity, r.Category, r.AllocationPct, r.Amount
                    })).ToList();
                    rows.Add(new PbbRow(new object?[] { "Total transferred", "", "", "", "", "", null, realloc.Sum(r => r.Amount) }, PbbRowStyle.Total));
                    PbbTable(ws, row,
                        new[] { "Entity", "From programme", "From activity", "To programme", "To activity", "Category", "Share", "Amount" },
                        new[] { PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Text, PbbKind.Percent, PbbKind.Money },
                        rows);
                    PbbWidths(ws, 26, 36, 36, 36, 36, 12, 10, 18);
                }
                PbbPrint(ws);
            }

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return File(stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"PBB_Report_{selectedYear}_{fileLabel}.xlsx");
        }

        // ---------------------------------------------------------------------
        // Workbook helpers
        // ---------------------------------------------------------------------

        private static decimal? Share(decimal part, decimal whole) => whole == 0m ? (decimal?)null : part / whole;

        private static string PbbLabel(string entityCode, string code, string name)
        {
            var parts = new[] { entityCode, code }.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            var left = string.Join(" / ", parts);
            if (string.IsNullOrWhiteSpace(name)) return left;
            return left.Length == 0 ? name : left + " - " + name;
        }

        // Title band across the sheet; returns the first free row below it.
        private static int PbbTitle(IXLWorksheet ws, string title, string entityTitle, int year, int colCount, string? note)
        {
            var band = ws.Range(1, 1, 1, colCount);
            band.Merge();
            band.Value = title;
            band.Style.Font.Bold = true;
            band.Style.Font.FontSize = 16;
            band.Style.Font.FontColor = XLColor.White;
            band.Style.Fill.BackgroundColor = XLColor.FromHtml(BrandColors.HeaderHex);
            band.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            band.Style.Alignment.Indent = 1;
            ws.Row(1).Height = 30;

            var sub = ws.Range(2, 1, 2, colCount);
            sub.Merge();
            sub.Value = $"{entityTitle}   |   Budget year {year}   |   Generated {DateTime.Now:dd MMM yyyy HH:mm}";
            sub.Style.Font.FontColor = XLColor.White;
            sub.Style.Fill.BackgroundColor = XLColor.FromHtml(BrandColors.HeaderAltHex);
            sub.Style.Alignment.Indent = 1;

            var accent = ws.Range(3, 1, 3, colCount);
            accent.Style.Fill.BackgroundColor = XLColor.FromHtml(BrandColors.AccentHex);
            ws.Row(3).Height = 4;

            var next = 5;
            if (!string.IsNullOrWhiteSpace(note))
            {
                ws.Cell(4, 1).Value = note;
                ws.Cell(4, 1).Style.Font.Italic = true;
                ws.Cell(4, 1).Style.Font.FontColor = XLColor.FromHtml("#9A6B00");
                next = 6;
            }
            return next;
        }

        private static int PbbSection(IXLWorksheet ws, int row, string text, int colCount)
        {
            var r = ws.Range(row, 1, row, colCount);
            r.Merge();
            r.Value = text;
            r.Style.Font.Bold = true;
            r.Style.Font.FontSize = 12;
            r.Style.Font.FontColor = XLColor.FromHtml(BrandColors.HeaderHex);
            r.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
            r.Style.Border.BottomBorderColor = XLColor.FromHtml(BrandColors.AccentHex);
            return row + 1;
        }

        // Writes a header row and the data rows; returns the first free row below the table.
        private static int PbbTable(IXLWorksheet ws, int row, string[] headers, PbbKind[] kinds, List<PbbRow> rows, bool filter = true)
        {
            var headerRow = row;
            for (var c = 0; c < headers.Length; c++) ws.Cell(row, c + 1).Value = headers[c];
            var head = ws.Range(row, 1, row, headers.Length);
            head.Style.Font.Bold = true;
            head.Style.Font.FontColor = XLColor.White;
            head.Style.Fill.BackgroundColor = XLColor.FromHtml(BrandColors.HeaderHex);
            head.Style.Alignment.WrapText = true;
            head.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            for (var c = 0; c < headers.Length; c++)
                if (kinds[c] != PbbKind.Text) ws.Cell(row, c + 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Row(row).Height = 30;
            row++;

            foreach (var r in rows)
            {
                for (var c = 0; c < headers.Length && c < r.Cells.Length; c++)
                {
                    var cell = ws.Cell(row, c + 1);
                    switch (r.Cells[c])
                    {
                        case null: break;
                        case decimal d: cell.Value = d; break;
                        case int i: cell.Value = i; break;
                        case double db: cell.Value = db; break;
                        case var o: cell.Value = o.ToString(); break;
                    }
                    cell.Style.NumberFormat.Format = kinds[c] switch
                    {
                        PbbKind.Money => PbbMoney,
                        PbbKind.Percent => PbbPercent,
                        PbbKind.Number => PbbNumber,
                        _ => "@"
                    };
                    if (kinds[c] == PbbKind.Text) cell.Style.NumberFormat.Format = "";
                }

                var line = ws.Range(row, 1, row, headers.Length);
                line.Style.Border.BottomBorder = XLBorderStyleValues.Hair;
                line.Style.Border.BottomBorderColor = XLColor.FromHtml("#C8D3CF");
                switch (r.Style)
                {
                    case PbbRowStyle.Subtotal:
                        line.Style.Font.Bold = true;
                        line.Style.Fill.BackgroundColor = XLColor.FromHtml(BrandColors.SubtotalHex);
                        break;
                    case PbbRowStyle.Total:
                        line.Style.Font.Bold = true;
                        line.Style.Font.FontColor = XLColor.White;
                        line.Style.Fill.BackgroundColor = XLColor.FromHtml(BrandColors.HeaderAltHex);
                        break;
                    case PbbRowStyle.Muted:
                        line.Style.Font.Italic = true;
                        line.Style.Font.FontColor = XLColor.FromHtml("#9A6B00");
                        break;
                }
                row++;
            }

            if (filter)
            {
                ws.Range(headerRow, 1, Math.Max(headerRow, row - 1), headers.Length).SetAutoFilter();
                ws.SheetView.FreezeRows(headerRow);
            }
            return row;
        }

        private static void PbbWidths(IXLWorksheet ws, params double[] widths)
        {
            for (var i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        }

        private static void PbbPrint(IXLWorksheet ws, bool landscape = true)
        {
            ws.PageSetup.PageOrientation = landscape ? XLPageOrientation.Landscape : XLPageOrientation.Portrait;
            ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
            ws.PageSetup.FitToPages(1, 0);
            ws.PageSetup.Margins.Left = 0.4;
            ws.PageSetup.Margins.Right = 0.4;
            ws.PageSetup.Footer.Center.AddText("Page ");
            ws.PageSetup.Footer.Center.AddText(XLHFPredefinedText.PageNumber);
        }

        private sealed class PbbProgramme
        {
            public string EntityCode { get; set; } = "";
            public int ProgramId { get; set; }
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public string Type { get; set; } = "";
            public int Activities { get; set; }
            public int Kpis { get; set; }
            public decimal Revenue { get; set; }
            public decimal Hr { get; set; }
            public decimal Opex { get; set; }
            public decimal Capex { get; set; }
            public decimal Allocated { get; set; }
            public decimal Direct => Hr + Opex + Capex;
            public decimal Total => Direct + Allocated;
        }

        private sealed class PbbActivity
        {
            public string EntityCode { get; set; } = "";
            public int ProgramId { get; set; }
            public string ProgramCode { get; set; } = "";
            public string ProgramName { get; set; } = "";
            public int ActivityId { get; set; }
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public decimal Revenue { get; set; }
            public decimal Hr { get; set; }
            public decimal Opex { get; set; }
            public decimal Capex { get; set; }
            public decimal Allocated { get; set; }
            public decimal Direct => Hr + Opex + Capex;
            public decimal Total => Direct + Allocated;
            public string OutputMeasure { get; set; } = "";
            public decimal OutputVolume { get; set; }
            public int Kpis { get; set; }
        }
    }
}
