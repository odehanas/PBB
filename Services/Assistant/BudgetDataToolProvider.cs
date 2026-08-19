using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GovBudget.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GovBudget.Services.Assistant
{
    /// <summary>
    /// Read-only questions the assistant may ask of the budget database. Every query is
    /// filtered by the caller's entity (and cost center for non-administrators), so the
    /// assistant can only surface data the same user could open in the screens.
    /// </summary>
    public sealed class BudgetDataToolProvider : IAssistantToolProvider
    {
        private const string HrCategoryCode = "HR";
        private const string AmountUnit = "whole currency units, not thousands or millions";

        private sealed record SummaryRow(string name, decimal amount, int lines);

        private readonly GovBudgetContext _db;
        private readonly AssistantOptions _options;

        public BudgetDataToolProvider(GovBudgetContext db, IOptions<AssistantOptions> options)
        {
            _db = db;
            _options = options.Value;
        }

        public IEnumerable<AssistantToolDefinition> GetTools()
        {
            yield return new AssistantToolDefinition(
                "get_user_scope",
                "Return who the user is, which entity and cost center their data is limited to, the working budget year, and the years that hold data. Call this first when the question does not name a year.",
                """{"type":"object","properties":{},"additionalProperties":false}""",
                GetScopeAsync);

            yield return new AssistantToolDefinition(
                "get_budget_summary",
                "Total budgeted amounts for a year, grouped by category (Revenue/OPEX/CAPEX/HR), department, program, activity or item. HR staff cost comes from the HR employee cost tables, so it appears when grouping by category or department but not by program, activity or item.",
                """
                {"type":"object","properties":{
                  "year":{"type":"integer","description":"Budget year. Defaults to the working year."},
                  "group_by":{"type":"string","enum":["category","department","program","activity","item"],"description":"Grouping level. Default category."},
                  "category_code":{"type":"string","description":"Optional filter: REVENUE, OPEX, CAPEX or HR."}
                },"additionalProperties":false}
                """,
                GetBudgetSummaryAsync);

            yield return new AssistantToolDefinition(
                "search_budget_lines",
                "Find individual budget lines by free-text description, item, program or activity name.",
                """
                {"type":"object","properties":{
                  "year":{"type":"integer"},
                  "query":{"type":"string","description":"Text to look for in the line description, item, program or activity."},
                  "top":{"type":"integer","description":"Maximum lines to return (default 25)."}
                },"required":["query"],"additionalProperties":false}
                """,
                SearchBudgetLinesAsync);

            yield return new AssistantToolDefinition(
                "get_budget_vs_actual",
                "Compare the budget with posted actuals for a year, grouped by GL account or by month, including variance and execution rate.",
                """
                {"type":"object","properties":{
                  "year":{"type":"integer"},
                  "group_by":{"type":"string","enum":["gl","month"],"description":"Default gl."}
                },"additionalProperties":false}
                """,
                GetBudgetVsActualAsync);

            yield return new AssistantToolDefinition(
                "get_kpis",
                "Performance KPIs for a year: type, dimension, baseline, target, actual, direction, achievement percentage and status.",
                """
                {"type":"object","properties":{
                  "year":{"type":"integer"},
                  "period":{"type":"string","description":"MidYear or YearEnd. Default: all periods."},
                  "program":{"type":"string","description":"Optional program code or name filter."},
                  "activity":{"type":"string","description":"Optional activity code or name filter."}
                },"additionalProperties":false}
                """,
                GetKpisAsync);

            yield return new AssistantToolDefinition(
                "get_activity_unit_cost",
                "Cost per unit of output by activity for a year: budgeted activity cost divided by the recorded output volume. Use for efficiency questions.",
                """
                {"type":"object","properties":{
                  "year":{"type":"integer"},
                  "top":{"type":"integer","description":"Maximum activities to return (default 50)."}
                },"additionalProperties":false}
                """,
                GetActivityUnitCostAsync);

            yield return new AssistantToolDefinition(
                "get_pbb_maturity",
                "Recorded performance-based budgeting maturity assessments (stage, status label, notes) for a year.",
                """{"type":"object","properties":{"year":{"type":"integer"}},"additionalProperties":false}""",
                GetMaturityAsync);

            yield return new AssistantToolDefinition(
                "list_master_data",
                "List the entities, departments (cost centers), programs, activities or items visible to the user.",
                """
                {"type":"object","properties":{
                  "type":{"type":"string","enum":["entities","departments","programs","activities","items"]},
                  "search":{"type":"string","description":"Optional name or code filter."}
                },"required":["type"],"additionalProperties":false}
                """,
                ListMasterDataAsync);
        }

        // ---------------- helpers ----------------

        private static int? Int(JsonElement args, string name) =>
            args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var i) ? i : null;

        private static string? Str(JsonElement args, string name) =>
            args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

        private int Year(JsonElement args, AssistantUserContext user) => Int(args, "year") ?? user.DefaultYear;

        private int Top(JsonElement args, int fallback) =>
            Math.Clamp(Int(args, "top") ?? fallback, 1, _options.MaxRows);

        private static string Json(object payload) =>
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });

        /// <summary>Entity filter applied to every query. -1 means "no entity" and matches nothing.</summary>
        private int? ScopeEntityId(AssistantUserContext user) =>
            user.IsGlobalAdmin ? null : (user.EntityId ?? -1);

        /// <summary>Cost-center filter. Only non-administrators are pinned to their own department.</summary>
        private static int? ScopeDepartmentId(AssistantUserContext user) =>
            user.IsGlobalAdmin || user.Role is "ADMIN" or "SYSADMIN" ? null : user.DepartmentId;

        /// <summary>
        /// Budget lines the user may see. HR-categorised lines are excluded because staff cost
        /// is sourced from the HR employee cost tables, exactly as the reports do it.
        /// </summary>
        private IQueryable<BudgetLines> ScopedLines(AssistantUserContext user, int year)
        {
            var q = _db.BudgetLines.AsNoTracking()
                .Where(b => b.BudgetYear == year && b.Category.CategoryCode != HrCategoryCode);
            var entityId = ScopeEntityId(user);
            if (entityId.HasValue) q = q.Where(b => b.EntityId == entityId.Value);
            var deptId = ScopeDepartmentId(user);
            if (deptId.HasValue) q = q.Where(b => b.DepartmentId == deptId.Value);
            return q;
        }

        private IQueryable<HrEmployeeCosts> ScopedHrCosts(AssistantUserContext user, int year)
        {
            var q = _db.HrEmployeeCosts.AsNoTracking().Where(h => h.BudgetYear == year);
            var entityId = ScopeEntityId(user);
            if (entityId.HasValue) q = q.Where(h => h.EntityId == entityId.Value);
            var deptId = ScopeDepartmentId(user);
            if (deptId.HasValue) q = q.Where(h => h.DepartmentId == deptId.Value);
            return q;
        }

        // ---------------- tools ----------------

        private async Task<string> GetScopeAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var entityId = ScopeEntityId(user);

            var entityName = user.EntityId.HasValue
                ? await _db.Entities.AsNoTracking()
                    .Where(e => e.EntityId == user.EntityId.Value)
                    .Select(e => e.EntityName)
                    .FirstOrDefaultAsync(ct)
                : null;

            var departmentName = user.DepartmentId.HasValue
                ? await _db.Departments.AsNoTracking()
                    .Where(d => d.DepartmentId == user.DepartmentId.Value)
                    .Select(d => d.DeptName)
                    .FirstOrDefaultAsync(ct)
                : null;

            var yearsQuery = _db.BudgetLines.AsNoTracking().AsQueryable();
            if (entityId.HasValue) yearsQuery = yearsQuery.Where(b => b.EntityId == entityId.Value);
            var years = await yearsQuery.Select(b => b.BudgetYear).Distinct().OrderBy(y => y).ToListAsync(ct);

            return Json(new
            {
                user = user.UserName,
                role = user.Role,
                sees_all_entities = user.IsGlobalAdmin,
                entity = entityName,
                cost_center = departmentName,
                working_year = user.DefaultYear,
                years_with_budget_data = years
            });
        }

        private async Task<string> GetBudgetSummaryAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var year = Year(args, user);
            var groupBy = (Str(args, "group_by") ?? "category").ToLowerInvariant();
            var categoryCode = Str(args, "category_code");

            var lines = ScopedLines(user, year);
            if (categoryCode is not null)
            {
                lines = lines.Where(b => b.Category.CategoryCode == categoryCode);
            }

            var wantsHr = categoryCode is null || string.Equals(categoryCode, HrCategoryCode, StringComparison.OrdinalIgnoreCase);
            var onlyHr = string.Equals(categoryCode, HrCategoryCode, StringComparison.OrdinalIgnoreCase);

            var rows = groupBy switch
            {
                "department" => await lines
                    .GroupBy(b => b.Department.DeptName)
                    .Select(g => new { name = g.Key, amount = g.Sum(x => x.Amount), lines = g.Count() })
                    .OrderByDescending(r => r.amount).Take(_options.MaxRows).ToListAsync(ct),
                "program" => await lines
                    .GroupBy(b => b.Program != null ? b.Program.ProgramName : "(unassigned)")
                    .Select(g => new { name = g.Key, amount = g.Sum(x => x.Amount), lines = g.Count() })
                    .OrderByDescending(r => r.amount).Take(_options.MaxRows).ToListAsync(ct),
                "activity" => await lines
                    .GroupBy(b => b.Activity != null ? b.Activity.ActivityName : "(unassigned)")
                    .Select(g => new { name = g.Key, amount = g.Sum(x => x.Amount), lines = g.Count() })
                    .OrderByDescending(r => r.amount).Take(_options.MaxRows).ToListAsync(ct),
                "item" => await lines
                    .GroupBy(b => b.Item.ItemName)
                    .Select(g => new { name = g.Key, amount = g.Sum(x => x.Amount), lines = g.Count() })
                    .OrderByDescending(r => r.amount).Take(_options.MaxRows).ToListAsync(ct),
                _ => await lines
                    .GroupBy(b => b.Category.CategoryName)
                    .Select(g => new { name = g.Key, amount = g.Sum(x => x.Amount), lines = g.Count() })
                    .OrderByDescending(r => r.amount).Take(_options.MaxRows).ToListAsync(ct)
            };

            var all = onlyHr
                ? new List<SummaryRow>()
                : rows.Select(r => new SummaryRow(r.name, r.amount, r.lines)).ToList();

            // Staff cost lives in the HR tables, so it is added to the groupings that carry it.
            if (wantsHr && groupBy is "category" or "department")
            {
                var hrRows = groupBy == "department"
                    ? await ScopedHrCosts(user, year)
                        .GroupBy(h => h.DepartmentName)
                        .Select(g => new SummaryRow(g.Key, g.Sum(x => x.AnnualCost), g.Count()))
                        .ToListAsync(ct)
                    : await ScopedHrCosts(user, year)
                        .GroupBy(h => 1)
                        .Select(g => new SummaryRow("HR (staff cost)", g.Sum(x => x.AnnualCost), g.Count()))
                        .ToListAsync(ct);

                foreach (var hr in hrRows.Where(r => r.amount != 0m))
                {
                    var existing = all.FindIndex(r => r.name == hr.name);
                    if (groupBy == "department" && existing >= 0)
                    {
                        all[existing] = all[existing] with
                        {
                            amount = all[existing].amount + hr.amount,
                            lines = all[existing].lines + hr.lines
                        };
                    }
                    else
                    {
                        all.Add(hr);
                    }
                }

                all = all.OrderByDescending(r => r.amount).ToList();
            }

            return Json(new
            {
                year,
                grouped_by = groupBy,
                category_code = categoryCode,
                amount_unit = AmountUnit,
                includes_hr_staff_cost = wantsHr && groupBy is "category" or "department",
                excludes_hr_staff_cost = groupBy is not ("category" or "department"),
                total = all.Sum(r => r.amount),
                rows = all
            });
        }

        private async Task<string> SearchBudgetLinesAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var year = Year(args, user);
            var query = Str(args, "query") ?? "";
            var top = Top(args, 25);

            var rows = await ScopedLines(user, year)
                .Where(b => EF.Functions.Like(b.Description, $"%{query}%")
                            || EF.Functions.Like(b.Item.ItemName, $"%{query}%")
                            || (b.Program != null && EF.Functions.Like(b.Program.ProgramName, $"%{query}%"))
                            || (b.Activity != null && EF.Functions.Like(b.Activity.ActivityName, $"%{query}%")))
                .OrderByDescending(b => b.Amount)
                .Take(top)
                .Select(b => new
                {
                    b.BudgetLineId,
                    description = b.Description,
                    category = b.Category.CategoryName,
                    department = b.Department.DeptName,
                    item = b.Item.ItemName,
                    program = b.Program != null ? b.Program.ProgramName : null,
                    activity = b.Activity != null ? b.Activity.ActivityName : null,
                    b.Quantity,
                    b.UnitPrice,
                    b.Amount
                })
                .ToListAsync(ct);

            return Json(new { year, query, count = rows.Count, rows });
        }

        private async Task<string> GetBudgetVsActualAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var year = Year(args, user);
            var groupBy = (Str(args, "group_by") ?? "gl").ToLowerInvariant();
            var entityId = ScopeEntityId(user);

            var actuals = _db.ActualPostings.AsNoTracking().Where(a => a.BudgetYear == year);
            if (entityId.HasValue) actuals = actuals.Where(a => a.EntityId == entityId.Value);

            var lines = ScopedLines(user, year);

            if (groupBy == "month")
            {
                var totals = await lines
                    .GroupBy(b => 1)
                    .Select(g => new
                    {
                        m1 = g.Sum(x => x.M01), m2 = g.Sum(x => x.M02), m3 = g.Sum(x => x.M03),
                        m4 = g.Sum(x => x.M04), m5 = g.Sum(x => x.M05), m6 = g.Sum(x => x.M06),
                        m7 = g.Sum(x => x.M07), m8 = g.Sum(x => x.M08), m9 = g.Sum(x => x.M09),
                        m10 = g.Sum(x => x.M10), m11 = g.Sum(x => x.M11), m12 = g.Sum(x => x.M12)
                    })
                    .FirstOrDefaultAsync(ct);

                var monthlyBudget = totals is null
                    ? new decimal[12]
                    : new[]
                    {
                        totals.m1, totals.m2, totals.m3, totals.m4, totals.m5, totals.m6,
                        totals.m7, totals.m8, totals.m9, totals.m10, totals.m11, totals.m12
                    };

                var monthlyActual = (await actuals
                        .GroupBy(a => a.PeriodMonth)
                        .Select(g => new { month = g.Key, amount = g.Sum(x => x.Amount) })
                        .ToListAsync(ct))
                    .ToDictionary(r => (int)r.month, r => r.amount);

                var months = Enumerable.Range(1, 12).Select(m => new
                {
                    month = m,
                    budget = monthlyBudget[m - 1],
                    actual = monthlyActual.TryGetValue(m, out var a) ? a : 0m,
                    variance = monthlyBudget[m - 1] - (monthlyActual.TryGetValue(m, out var a2) ? a2 : 0m)
                }).ToList();

                return Json(new { year, grouped_by = "month", amount_unit = AmountUnit, excludes_hr_staff_cost = true, rows = months });
            }

            var budgetByGl = await lines
                .GroupBy(b => b.Item.GLAccount.GLCode)
                .Select(g => new { gl = g.Key, budget = g.Sum(x => x.Amount) })
                .ToListAsync(ct);

            var actualByGl = (await actuals
                    .GroupBy(a => a.GLCode)
                    .Select(g => new { gl = g.Key, actual = g.Sum(x => x.Amount) })
                    .ToListAsync(ct))
                .ToDictionary(r => r.gl, r => r.actual);

            var rows = budgetByGl
                .Select(b =>
                {
                    var actual = actualByGl.TryGetValue(b.gl, out var a) ? a : 0m;
                    return new
                    {
                        gl_code = string.IsNullOrWhiteSpace(b.gl) ? "(unmapped)" : b.gl,
                        budget = b.budget,
                        actual,
                        variance = b.budget - actual,
                        execution_rate_pct = b.budget == 0 ? (decimal?)null : Math.Round(actual / b.budget * 100m, 2)
                    };
                })
                .OrderByDescending(r => Math.Abs(r.variance))
                .Take(_options.MaxRows)
                .ToList();

            return Json(new
            {
                year,
                grouped_by = "gl",
                amount_unit = AmountUnit,
                excludes_hr_staff_cost = true,
                total_budget = rows.Sum(r => r.budget),
                total_actual = rows.Sum(r => r.actual),
                rows
            });
        }

        private async Task<string> GetKpisAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var year = Year(args, user);
            var period = Str(args, "period");
            var program = Str(args, "program");
            var activity = Str(args, "activity");
            var entityId = ScopeEntityId(user);

            var q = _db.Kpis.AsNoTracking().Where(k => k.BudgetYear == year);
            if (entityId.HasValue) q = q.Where(k => k.EntityId == entityId.Value);
            if (period is not null) q = q.Where(k => k.Period == period);
            if (program is not null)
                q = q.Where(k => k.Program != null
                                 && (EF.Functions.Like(k.Program.ProgramName, $"%{program}%")
                                     || k.Program.ProgramCode == program));
            if (activity is not null)
                q = q.Where(k => k.Activity != null
                                 && (EF.Functions.Like(k.Activity.ActivityName, $"%{activity}%")
                                     || k.Activity.ActivityCode == activity));

            var rows = await q
                .OrderBy(k => k.KpiName)
                .Take(_options.MaxRows)
                .Select(k => new
                {
                    k.KpiCode,
                    kpi = k.KpiName,
                    k.Unit,
                    type = k.KpiType,
                    k.Dimension,
                    k.Period,
                    program = k.Program != null ? k.Program.ProgramName : null,
                    activity = k.Activity != null ? k.Activity.ActivityName : null,
                    k.Direction,
                    k.Baseline,
                    k.Target,
                    actual = k.ActualValue,
                    k.Status,
                    strategic_target_2029 = k.StrategicTarget2029
                })
                .ToListAsync(ct);

            var withAchievement = rows.Select(r => new
            {
                r.KpiCode,
                r.kpi,
                r.Unit,
                r.type,
                r.Dimension,
                r.Period,
                r.program,
                r.activity,
                r.Direction,
                r.Baseline,
                r.Target,
                r.actual,
                r.Status,
                r.strategic_target_2029,
                achievement_pct = Achievement(r.Direction, r.Target, r.actual)
            });

            return Json(new { year, count = rows.Count, rows = withAchievement });
        }

        /// <summary>
        /// Achievement against target. For "UP" indicators higher is better, for "DOWN"
        /// indicators the target over the actual, so 100% always means "on target".
        /// </summary>
        private static decimal? Achievement(string? direction, decimal? target, decimal? actual)
        {
            if (target is null || actual is null) return null;
            var isDown = string.Equals(direction, "DOWN", StringComparison.OrdinalIgnoreCase);
            if (isDown)
            {
                if (actual.Value == 0) return null;
                return Math.Round(target.Value / actual.Value * 100m, 2);
            }
            if (target.Value == 0) return null;
            return Math.Round(actual.Value / target.Value * 100m, 2);
        }

        private async Task<string> GetActivityUnitCostAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var year = Year(args, user);
            var top = Top(args, 50);

            var costs = await ScopedLines(user, year)
                .Where(b => b.ActivityId != null)
                .GroupBy(b => new { b.ActivityId, b.Activity!.ActivityCode, b.Activity!.ActivityName })
                .Select(g => new
                {
                    activity_id = g.Key.ActivityId!.Value,
                    code = g.Key.ActivityCode,
                    activity = g.Key.ActivityName,
                    cost = g.Sum(x => x.Amount)
                })
                .OrderByDescending(r => r.cost)
                .Take(top)
                .ToListAsync(ct);

            var activityIds = costs.Select(c => c.activity_id).ToList();

            var outputs = await _db.ActivityOutputs.AsNoTracking()
                .Where(o => o.BudgetYear == year && activityIds.Contains(o.ActivityId))
                .OrderByDescending(o => o.IsPrimary)
                .Select(o => new { o.ActivityId, o.OutputMeasure, o.OutputVolume, o.IsPrimary })
                .ToListAsync(ct);

            var primary = outputs
                .GroupBy(o => o.ActivityId)
                .ToDictionary(g => g.Key, g => g.First());

            var rows = costs.Select(c =>
            {
                primary.TryGetValue(c.activity_id, out var o);
                return new
                {
                    c.code,
                    c.activity,
                    c.cost,
                    output_measure = o?.OutputMeasure,
                    output_volume = o?.OutputVolume,
                    unit_cost = o is null || o.OutputVolume == 0 ? (decimal?)null : Math.Round(c.cost / o.OutputVolume, 2)
                };
            });

            return Json(new { year, rows });
        }

        private async Task<string> GetMaturityAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var year = Year(args, user);
            var entityId = ScopeEntityId(user);

            var q = _db.MaturityAssessments.AsNoTracking().Where(m => m.BudgetYear == year);
            if (entityId.HasValue) q = q.Where(m => m.EntityId == entityId.Value);

            var rows = await q
                .OrderBy(m => m.Entity.EntityName)
                .Take(_options.MaxRows)
                .Select(m => new
                {
                    entity = m.Entity.EntityName,
                    m.Period,
                    m.Stage,
                    m.Form,
                    status = m.StatusLabel,
                    m.Notes,
                    assessed_at = m.AssessedAt
                })
                .ToListAsync(ct);

            return Json(new { year, count = rows.Count, rows });
        }

        private async Task<string> ListMasterDataAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var type = (Str(args, "type") ?? "entities").ToLowerInvariant();
            var search = Str(args, "search");
            var entityId = ScopeEntityId(user);

            switch (type)
            {
                case "departments":
                {
                    var q = _db.Departments.AsNoTracking().AsQueryable();
                    if (entityId.HasValue) q = q.Where(d => d.EntityId == entityId.Value);
                    if (search is not null) q = q.Where(d => EF.Functions.Like(d.DeptName, $"%{search}%") || d.DeptCode == search);
                    var rows = await q.OrderBy(d => d.DeptName).Take(_options.MaxRows)
                        .Select(d => new { d.DepartmentId, code = d.DeptCode, name = d.DeptName, d.IsActive }).ToListAsync(ct);
                    return Json(new { type, rows });
                }
                case "programs":
                {
                    var q = _db.Programs.AsNoTracking().AsQueryable();
                    if (entityId.HasValue) q = q.Where(p => p.EntityId == entityId.Value);
                    if (search is not null) q = q.Where(p => EF.Functions.Like(p.ProgramName, $"%{search}%") || p.ProgramCode == search);
                    var rows = await q.OrderBy(p => p.ProgramName).Take(_options.MaxRows)
                        .Select(p => new { p.ProgramId, code = p.ProgramCode, name = p.ProgramName, p.ProgramType, p.IsActive }).ToListAsync(ct);
                    return Json(new { type, rows });
                }
                case "activities":
                {
                    var q = _db.Activities.AsNoTracking().AsQueryable();
                    if (entityId.HasValue) q = q.Where(a => a.Program.EntityId == entityId.Value);
                    if (search is not null) q = q.Where(a => EF.Functions.Like(a.ActivityName, $"%{search}%") || a.ActivityCode == search);
                    var rows = await q.OrderBy(a => a.ActivityName).Take(_options.MaxRows)
                        .Select(a => new
                        {
                            a.ActivityId,
                            code = a.ActivityCode,
                            name = a.ActivityName,
                            program = a.Program.ProgramName,
                            department = a.Department.DeptName,
                            a.IsActive
                        }).ToListAsync(ct);
                    return Json(new { type, rows });
                }
                case "items":
                {
                    var q = _db.Items.AsNoTracking().AsQueryable();
                    if (search is not null) q = q.Where(i => EF.Functions.Like(i.ItemName, $"%{search}%") || i.ItemCode == search);
                    var rows = await q.OrderBy(i => i.ItemName).Take(_options.MaxRows)
                        .Select(i => new { i.ItemId, code = i.ItemCode, name = i.ItemName, gl = i.GLAccount.GLCode, i.IsActive }).ToListAsync(ct);
                    return Json(new { type, rows });
                }
                default:
                {
                    var q = _db.Entities.AsNoTracking().AsQueryable();
                    if (entityId.HasValue) q = q.Where(e => e.EntityId == entityId.Value);
                    if (search is not null) q = q.Where(e => EF.Functions.Like(e.EntityName, $"%{search}%") || e.EntityCode == search);
                    var rows = await q.OrderBy(e => e.EntityName).Take(_options.MaxRows)
                        .Select(e => new { e.EntityId, code = e.EntityCode, name = e.EntityName, e.IsActive }).ToListAsync(ct);
                    return Json(new { type = "entities", rows });
                }
            }
        }
    }
}
