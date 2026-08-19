using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// <summary>
    /// PBB Management Review report pack (Phase 1).
    /// Isolated, additive, read-only. Does not modify budget inputs or existing reports.
    /// Reports: Cost Structure, Capex Discipline, Manpower (cost-per-FTE), Programme Cost (Direct + Allocated).
    /// </summary>
    [Authorize(Roles = "ADMIN,SYSADMIN")]
    public class ManagementReviewController : Controller
    {
        private const decimal CostPerFteBandMin = 250_000m;
        private const decimal CostPerFteBandMax = 450_000m;

        private readonly GovBudgetContext _db;

        public ManagementReviewController(GovBudgetContext db)
        {
            _db = db;
        }

        // Access: SYSADMIN and global ADMINs see all entities and may filter; entity-scoped
        // ADMINs are allowed in but the report data is locked to their own entity (see ResolveEntityScope).

        // compareScenarios / scenarios: opt-in allocation-scenario comparison. Left off, the page is
        // exactly what it was before - every section still reads the official (latest Posted) run.
        [HttpGet]
        public async Task<IActionResult> Index(int? year = null, int? entityId = null,
            bool compareScenarios = false, string[]? scenarios = null)
        {
            var thisYear = DateTime.Now.Year;
            var selectedYear = year ?? HttpContext.Session.GetInt("ctxYear") ?? thisYear;

            var isAdmin = User.IsInRole("ADMIN");
            var isSysAdmin = User.IsInRole("SYSADMIN");
            var isAdminLike = isAdmin || isSysAdmin;
            var scopedEntityId = GetEntityClaimId();
            var isGlobalAdmin = IsGlobalAdmin(isAdmin, isSysAdmin, scopedEntityId);
            var effectiveEntityId = ResolveEntityScope(isAdminLike, isGlobalAdmin, entityId);

            var years = new[] { thisYear - 1, thisYear, thisYear + 1, thisYear + 2 }
                .Select(y => new SelectListItem(y.ToString(), y.ToString(), y == selectedYear))
                .ToList();

            var entityOptions = await BuildEntityOptions(isGlobalAdmin, effectiveEntityId);

            var vm = new ManagementReviewVm
            {
                Year = selectedYear,
                IsAdmin = isAdminLike,
                EntityId = effectiveEntityId,
                YearOptions = years,
                EntityOptions = entityOptions,
                CostStructure = await BuildCostStructure(selectedYear, effectiveEntityId),
                CapexVariance = await BuildCapexVariance(selectedYear, effectiveEntityId),
                Manpower = await BuildManpower(selectedYear, effectiveEntityId),
                ProgrammeCosts = await BuildProgrammeCosts(selectedYear, effectiveEntityId),
                KpiScorecard = await BuildKpiScorecard(selectedYear, effectiveEntityId),
                KpiDetails = await BuildKpiDetails(selectedYear, effectiveEntityId),
                MaturityLadder = await BuildMaturityLadder(selectedYear, effectiveEntityId),
                ActivityUnitCosts = await BuildActivityUnitCosts(selectedYear, effectiveEntityId),
                KpiCostLinks = await BuildKpiCostLinkage(selectedYear, effectiveEntityId),
                CostPerOutput = await BuildCostPerOutput(selectedYear, effectiveEntityId)
            };

            var scopeEntityIds = (effectiveEntityId.HasValue && effectiveEntityId.Value <= 0)
                ? new List<int>()
                : (await EntityScopeList(effectiveEntityId)).Select(e => e.EntityId).ToList();
            vm.ActualsUploaded = await HasActualsForScope(selectedYear, scopeEntityIds);
            vm.AllocationPosted = await HasPostedAllocation(selectedYear, scopeEntityIds);

            vm.EntityProfiles = await BuildEntityProfiles(selectedYear, effectiveEntityId,
                vm.CostStructure, vm.Manpower, vm.KpiScorecard, vm.MaturityLadder);
            vm.Narratives = ToNarrativeVm(await LoadReviewNarratives(selectedYear));
            vm.Scenarios = await BuildScenarioComparison(selectedYear, effectiveEntityId, scenarios, compareScenarios);

            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> Export(int? year = null, int? entityId = null)
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

            var costStructure = await BuildCostStructure(selectedYear, effectiveEntityId);
            var capex = await BuildCapexVariance(selectedYear, effectiveEntityId);
            var manpower = await BuildManpower(selectedYear, effectiveEntityId);
            var programmes = await BuildProgrammeCosts(selectedYear, effectiveEntityId);
            var kpiScorecard = await BuildKpiScorecard(selectedYear, effectiveEntityId);
            var kpiDetails = await BuildKpiDetails(selectedYear, effectiveEntityId);
            var maturity = await BuildMaturityLadder(selectedYear, effectiveEntityId);
            var unitCosts = await BuildActivityUnitCosts(selectedYear, effectiveEntityId);
            var kpiCostLinks = await BuildKpiCostLinkage(selectedYear, effectiveEntityId);
            var costPerOutput = await BuildCostPerOutput(selectedYear, effectiveEntityId);
            var profiles = await BuildEntityProfiles(selectedYear, effectiveEntityId, costStructure, manpower, kpiScorecard, maturity);
            var narratives = ToNarrativeVm(await LoadReviewNarratives(selectedYear));

            using var wb = new XLWorkbook();
            BuildCostStructureSheet(wb, costStructure, selectedYear, entityLabel);
            BuildCapexSheet(wb, capex, selectedYear, entityLabel);
            BuildManpowerSheet(wb, manpower, selectedYear, entityLabel);
            BuildProgrammeSheet(wb, programmes, selectedYear, entityLabel);
            BuildKpiScorecardSheet(wb, kpiScorecard, selectedYear, entityLabel);
            BuildKpiDetailSheet(wb, kpiDetails, selectedYear, entityLabel);
            BuildMaturitySheet(wb, maturity, selectedYear, entityLabel);
            BuildActivityUnitCostSheet(wb, unitCosts, selectedYear, entityLabel);
            BuildKpiCostLinkSheet(wb, kpiCostLinks, selectedYear, entityLabel);
            BuildCostPerOutputSheet(wb, costPerOutput, selectedYear, entityLabel);
            BuildEntityProfileSheet(wb, profiles, selectedYear, entityLabel);
            BuildNarrativeSheet(wb, narratives, selectedYear, entityLabel);

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            var bytes = stream.ToArray();
            var fileName = $"PBB_ManagementReview_{selectedYear}_{entityLabel}.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        // ---------- Scope helpers (mirror ReportsController) ----------

        private int? GetEntityClaimId()
        {
            var entityClaim = User.Claims.FirstOrDefault(c => c.Type == "EntityId")?.Value;
            if (!int.TryParse(entityClaim, out var entityId) || entityId <= 0) return null;
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
                    if (requestedEntityId.HasValue && requestedEntityId.Value > 0) return requestedEntityId.Value;
                    return null;
                }
                var scoped = GetEntityClaimId();
                return scoped.HasValue && scoped.Value > 0 ? scoped.Value : -1;
            }
            var entityId = GetEntityClaimId();
            return entityId.HasValue && entityId.Value > 0 ? entityId.Value : -1;
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

        private async Task<string> GetEntityLabel(int? entityId)
        {
            if (!entityId.HasValue) return "AllEntities";
            var code = await _db.Entities.AsNoTracking()
                .Where(e => e.EntityId == entityId.Value)
                .Select(e => e.EntityCode)
                .FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(code)) return $"Entity{entityId.Value}";
            return new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray());
        }

        private static string NormalizeCategory(string? code)
        {
            var c = (code ?? "").Trim().ToUpperInvariant();
            if (c.Contains("REV")) return "REVENUE";
            if (c.Contains("CAP")) return "CAPEX";
            if (c.Contains("HR") || c.Contains("MANPOWER") || c.Contains("SALAR")) return "HR";
            if (c.Contains("OPEX") || c.Contains("OP")) return "OPEX";
            return "OPEX";
        }

        // ---------- Report builders ----------

        private async Task<List<CostStructureRowVm>> BuildCostStructure(int year, int? entityId)
        {
            if (entityId.HasValue && entityId.Value <= 0) return new List<CostStructureRowVm>();

            var entities = await EntityScopeList(entityId);
            if (entities.Count == 0) return new List<CostStructureRowVm>();
            var entityIds = entities.Select(e => e.EntityId).ToList();

            var costMap = await _db.CostShapeMap.AsNoTracking()
                .Where(m => m.IsActive)
                .OrderBy(m => m.Priority)
                .ToListAsync();

            var lines = await (
                from b in _db.BudgetLines.AsNoTracking()
                join cat in _db.Categories.AsNoTracking() on b.CategoryId equals cat.CategoryId
                join item in _db.Items.AsNoTracking() on b.ItemId equals item.ItemId
                join gl in _db.GLAccounts.AsNoTracking() on item.GLAccountId equals gl.GLAccountId
                where b.BudgetYear == year && entityIds.Contains(b.EntityId)
                select new { b.EntityId, cat.CategoryCode, gl.GLCode, gl.GLName, b.Amount }
            ).ToListAsync();

            var hr = await _db.HrEmployeeCosts.AsNoTracking()
                .Where(x => x.BudgetYear == year && x.EntityId != null && entityIds.Contains(x.EntityId.Value))
                .GroupBy(x => x.EntityId!.Value)
                .Select(g => new { EntityId = g.Key, Cost = g.Sum(x => x.AnnualCost) })
                .ToListAsync();
            var hrMap = hr.ToDictionary(x => x.EntityId, x => x.Cost);

            var rows = new List<CostStructureRowVm>();
            foreach (var e in entities)
            {
                var row = new CostStructureRowVm { EntityCode = e.EntityCode, EntityName = e.EntityName };
                row.Manpower = hrMap.TryGetValue(e.EntityId, out var hc) ? hc : 0m;

                foreach (var l in lines.Where(x => x.EntityId == e.EntityId))
                {
                    var cat = NormalizeCategory(l.CategoryCode);
                    if (cat == "REVENUE") continue;
                    if (cat == "HR") { row.Manpower += l.Amount; continue; }
                    if (cat == "CAPEX") { row.Capital += l.Amount; continue; }
                    var bucket = ClassifyShape(l.GLCode, l.GLName, costMap);
                    if (bucket == "Consultancy") row.Consultancy += l.Amount;
                    else if (bucket == "Maintenance") row.Maintenance += l.Amount;
                    else row.OtherOperating += l.Amount;
                }
                rows.Add(row);
            }
            return rows.OrderBy(x => x.EntityCode).ToList();
        }

        private static string ClassifyShape(string? glCode, string? glName, List<CostShapeMap> map)
        {
            foreach (var m in map)
            {
                if (!string.IsNullOrWhiteSpace(m.GLCode) &&
                    string.Equals(m.GLCode, glCode, StringComparison.OrdinalIgnoreCase))
                    return m.Bucket;
                if (!string.IsNullOrWhiteSpace(m.MatchKeyword) && !string.IsNullOrWhiteSpace(glName) &&
                    glName.IndexOf(m.MatchKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    return m.Bucket;
            }
            return "Other";
        }

        private async Task<List<CapexVarianceRowVm>> BuildCapexVariance(int year, int? entityId)
        {
            if (entityId.HasValue && entityId.Value <= 0) return new List<CapexVarianceRowVm>();

            var entities = await EntityScopeList(entityId);
            if (entities.Count == 0) return new List<CapexVarianceRowVm>();
            var entityIds = entities.Select(e => e.EntityId).ToList();

            var budget = await (
                from b in _db.BudgetLines.AsNoTracking()
                join cat in _db.Categories.AsNoTracking() on b.CategoryId equals cat.CategoryId
                where b.BudgetYear == year && entityIds.Contains(b.EntityId) && cat.CategoryCode == "CAPEX"
                group b by b.EntityId into g
                select new
                {
                    EntityId = g.Key,
                    Annual = g.Sum(x => x.Amount),
                    H1 = g.Sum(x => x.M01 + x.M02 + x.M03 + x.M04 + x.M05 + x.M06)
                }
            ).ToListAsync();
            var budgetMap = budget.ToDictionary(x => x.EntityId);

            var midYear = await _db.MidYearGlActualForecasts.AsNoTracking()
                .Where(x => x.BudgetYear == year && entityIds.Contains(x.EntityId))
                .Select(x => new { x.EntityId, x.GLType, x.ActualH1Amount })
                .ToListAsync();

            var actualH1Map = midYear
                .Where(x => NormalizeCategory(x.GLType) == "CAPEX")
                .GroupBy(x => x.EntityId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.ActualH1Amount));

            var rows = new List<CapexVarianceRowVm>();
            foreach (var e in entities)
            {
                budgetMap.TryGetValue(e.EntityId, out var b);
                var budgetAnnual = b?.Annual ?? 0m;
                var budgetH1 = b?.H1 ?? 0m;
                var actualH1 = actualH1Map.TryGetValue(e.EntityId, out var a) ? a : 0m;
                var variance = actualH1 - budgetH1;
                var variancePct = budgetH1 != 0 ? variance / budgetH1 * 100m : 0m;
                rows.Add(new CapexVarianceRowVm
                {
                    EntityCode = e.EntityCode,
                    EntityName = e.EntityName,
                    BudgetAnnual = budgetAnnual,
                    BudgetH1 = budgetH1,
                    ActualH1 = actualH1,
                    VarianceH1 = variance,
                    VariancePct = variancePct
                });
            }
            return rows.OrderBy(x => x.EntityCode).ToList();
        }

        private async Task<List<ManpowerRowVm>> BuildManpower(int year, int? entityId)
        {
            if (entityId.HasValue && entityId.Value <= 0) return new List<ManpowerRowVm>();

            var entities = await EntityScopeList(entityId);
            if (entities.Count == 0) return new List<ManpowerRowVm>();
            var entityIds = entities.Select(e => e.EntityId).ToList();

            var hr = await _db.HrEmployeeCosts.AsNoTracking()
                .Where(x => x.BudgetYear == year && x.EntityId != null && entityIds.Contains(x.EntityId.Value))
                .GroupBy(x => x.EntityId!.Value)
                .Select(g => new
                {
                    EntityId = g.Key,
                    Cost = g.Sum(x => x.AnnualCost),
                    HeadCount = g.Select(x => x.EmployeeId).Distinct().Count()
                })
                .ToListAsync();
            var hrMap = hr.ToDictionary(x => x.EntityId);

            var rows = new List<ManpowerRowVm>();
            foreach (var e in entities)
            {
                hrMap.TryGetValue(e.EntityId, out var h);
                var cost = h?.Cost ?? 0m;
                var headCount = h?.HeadCount ?? 0;
                var costPerFte = headCount > 0 ? cost / headCount : 0m;
                string band = headCount == 0 ? "" :
                    costPerFte < CostPerFteBandMin ? "Below band" :
                    costPerFte > CostPerFteBandMax ? "Above band" : "Within band";
                rows.Add(new ManpowerRowVm
                {
                    EntityCode = e.EntityCode,
                    EntityName = e.EntityName,
                    ManpowerCost = cost,
                    HeadCount = headCount,
                    CostPerFte = costPerFte,
                    BandStatus = band
                });
            }
            return rows.OrderBy(x => x.EntityCode).ToList();
        }

        private async Task<List<ProgrammeCostRowVm>> BuildProgrammeCosts(int year, int? entityId)
        {
            if (entityId.HasValue && entityId.Value <= 0) return new List<ProgrammeCostRowVm>();

            var entities = await EntityScopeList(entityId);
            if (entities.Count == 0) return new List<ProgrammeCostRowVm>();
            var entityIds = entities.Select(e => e.EntityId).ToList();
            var entityNameMap = entities.ToDictionary(e => e.EntityId);

            var (directMap, overheadMap) = await ProgrammeDirectCost(year, entityIds);

            // Reflect the latest Posted step-down allocation run (Cost Allocation module):
            // net = allocated-in (to Mandate targets) minus allocated-out (from Support sources) per programme.
            var netAlloc = await AllocationNetByProgram(year, entityIds);

            var programIds = directMap.Keys.Select(k => k.programId)
                .Concat(netAlloc.Keys.Select(k => k.programId))
                .Distinct().ToList();
            var programs = await _db.Programs.AsNoTracking()
                .Where(p => programIds.Contains(p.ProgramId))
                .Select(p => new { p.ProgramId, p.EntityId, p.ProgramCode, p.ProgramName })
                .ToListAsync();
            var programMap = programs.ToDictionary(p => p.ProgramId);

            var entityDirectTotal = directMap
                .GroupBy(kv => kv.Key.entityId)
                .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));

            // Union of programmes that have direct cost and/or a step-down allocation.
            var allKeys = directMap.Keys.Union(netAlloc.Keys).ToList();

            var rows = new List<ProgrammeCostRowVm>();
            foreach (var key in allKeys)
            {
                var (entId, progId) = key;
                if (!programMap.TryGetValue(progId, out var prog)) continue;
                var direct = directMap.TryGetValue(key, out var dv) ? dv : 0m;

                // Legacy untagged-overhead pool share (0 when everything is tagged).
                var pool = overheadMap.TryGetValue(entId, out var oh) ? oh : 0m;
                var entTotal = entityDirectTotal.TryGetValue(entId, out var t) ? t : 0m;
                var overheadShare = entTotal > 0 ? Math.Round(pool * (direct / entTotal), 2, MidpointRounding.AwayFromZero) : 0m;

                // Step-down reallocation (positive for Mandate targets, negative for Support sources).
                var stepdown = netAlloc.TryGetValue(key, out var na) ? na : 0m;
                var allocated = overheadShare + stepdown;

                if (direct == 0m && allocated == 0m) continue; // nothing to show

                entityNameMap.TryGetValue(entId, out var ent);
                rows.Add(new ProgrammeCostRowVm
                {
                    ProgramId = progId,
                    EntityCode = ent?.EntityCode ?? "",
                    EntityName = ent?.EntityName ?? "",
                    ProgramCode = prog.ProgramCode,
                    ProgramName = prog.ProgramName,
                    Direct = direct,
                    Allocated = allocated,
                    Total = direct + allocated
                });
            }
            return rows.OrderBy(x => x.EntityCode).ThenByDescending(x => x.Total).ToList();
        }

        // Direct cost per (entity, programme) plus the untagged overhead pool per entity.
        // Direct = non-revenue budget lines tagged to the programme (directly or through their
        // activity) + HR allocated to the programme's activities. Shared by the Programme Cost
        // report and by the allocation-scenario comparison so both rest on the same base.
        private async Task<(Dictionary<(int entityId, int programId), decimal> Direct, Dictionary<int, decimal> OverheadPool)>
            ProgrammeDirectCost(int year, List<int> entityIds)
        {
            var directMap = new Dictionary<(int entityId, int programId), decimal>();
            var overheadMap = new Dictionary<int, decimal>();
            if (entityIds.Count == 0) return (directMap, overheadMap);

            // Materialize non-revenue budget lines (resolve programme in memory to avoid EF translation pitfalls)
            var rawLines = await (
                from b in _db.BudgetLines.AsNoTracking()
                join cat in _db.Categories.AsNoTracking() on b.CategoryId equals cat.CategoryId
                join act in _db.Activities.AsNoTracking() on b.ActivityId equals act.ActivityId into actJoin
                from act in actJoin.DefaultIfEmpty()
                where b.BudgetYear == year && entityIds.Contains(b.EntityId)
                      && cat.CategoryCode != "REVENUE"
                select new
                {
                    b.EntityId,
                    b.ProgramId,
                    ActProgramId = (int?)(act == null ? (int?)null : act.ProgramId),
                    b.ActivityId,
                    b.Amount
                }
            ).ToListAsync();

            // Direct budget per programme (tagged directly or via its activity)
            foreach (var x in rawLines)
            {
                var programId = x.ProgramId ?? x.ActProgramId;
                if (programId == null) continue;
                var key = (x.EntityId, programId.Value);
                directMap[key] = directMap.GetValueOrDefault(key) + x.Amount;
            }

            // Untagged overhead pool per entity (no programme and no activity)
            foreach (var x in rawLines.Where(x => x.ProgramId == null && x.ActProgramId == null && x.ActivityId == null))
                overheadMap[x.EntityId] = overheadMap.GetValueOrDefault(x.EntityId) + x.Amount;

            // Direct HR allocated to a programme's activities
            var directHr = await (
                from a in _db.HrEmployeeCostAllocations.AsNoTracking()
                join emp in _db.HrEmployeeCosts.AsNoTracking() on a.EmployeeCostId equals emp.EmployeeCostId
                join act in _db.Activities.AsNoTracking() on a.ActivityId equals act.ActivityId
                where emp.BudgetYear == year && emp.EntityId != null && entityIds.Contains(emp.EntityId.Value)
                group a.AllocatedAmount by new { EntityId = emp.EntityId!.Value, act.ProgramId } into g
                select new { g.Key.EntityId, g.Key.ProgramId, Amount = g.Sum() }
            ).ToListAsync();

            foreach (var d in directHr)
            {
                var key = (d.EntityId, d.ProgramId);
                directMap[key] = directMap.GetValueOrDefault(key) + d.Amount;
            }

            return (directMap, overheadMap);
        }

        // Net step-down allocation per (entity, programme). Positive = cost allocated IN (Mandate
        // target); negative = cost allocated OUT (Support source). Sums to zero within an entity,
        // so it reallocates cost without changing the entity total.
        // runId: null = the latest Posted run in scope (what every standard report uses);
        //        a value = that specific run, so a Scenario or Superseded run can be compared.
        private async Task<Dictionary<(int entityId, int programId), decimal>> AllocationNetByProgram(
            int year, List<int> entityIds, int? runId = null)
        {
            var result = new Dictionary<(int entityId, int programId), decimal>();
            if (entityIds.Count == 0) return result;
            try
            {
                if (runId.HasValue)
                {
                    // An explicitly chosen run: honour the entity scope of the caller.
                    var chosen = await _db.AllocationRuns.AsNoTracking()
                        .FirstOrDefaultAsync(r => r.RunId == runId.Value && r.BudgetYear == year
                            && (r.EntityId == null || entityIds.Contains(r.EntityId.Value)));
                    if (chosen == null) return result;

                    var chosenTxns = await _db.AllocationTransactions.AsNoTracking()
                        .Where(t => t.RunId == chosen.RunId && entityIds.Contains(t.EntityId))
                        .ToListAsync();
                    foreach (var tx in chosenTxns) Apply(result, tx.EntityId, tx.TargetProgramId, tx.SourceProgramId, tx.Amount);
                    return result;
                }

                // Candidate posted runs: entity-specific runs, plus any global (null-entity) run.
                var posted = await _db.AllocationRuns.AsNoTracking()
                    .Where(r => r.BudgetYear == year && r.Status == "Posted"
                        && (r.EntityId == null || entityIds.Contains(r.EntityId.Value)))
                    .OrderByDescending(r => r.RunAt)
                    .ToListAsync();
                if (posted.Count == 0) return result;

                foreach (var eid in entityIds)
                {
                    // Prefer the latest run scoped to this entity; else fall back to the latest global run.
                    var run = posted.FirstOrDefault(r => r.EntityId == eid)
                              ?? posted.FirstOrDefault(r => r.EntityId == null);
                    if (run == null) continue;

                    var txns = await _db.AllocationTransactions.AsNoTracking()
                        .Where(t => t.RunId == run.RunId && t.EntityId == eid)
                        .ToListAsync();
                    foreach (var tx in txns) Apply(result, eid, tx.TargetProgramId, tx.SourceProgramId, tx.Amount);
                }
            }
            catch
            {
                // Allocation tables may not exist yet (migration not applied) -> Direct only.
            }
            return result;

            static void Apply(Dictionary<(int, int), decimal> map, int entityId, int targetProgramId, int sourceProgramId, decimal amount)
            {
                var kIn = (entityId, targetProgramId);
                var kOut = (entityId, sourceProgramId);
                map[kIn] = map.GetValueOrDefault(kIn) + amount;
                map[kOut] = map.GetValueOrDefault(kOut) - amount;
            }
        }

        // ---------- Allocation scenarios (comparison) ----------

        // Key of the always-available reference scenario: 100% of every Support programme's direct
        // cost split equally across the Mandate programmes of the same entity. It needs no stored
        // run, so management always has a neutral baseline to compare the executed runs against.
        public const string EqualScenarioKey = "equal";
        private const int MaxScenarioRuns = 30;

        private static int? RunIdFromKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            if (!key.StartsWith("run:", StringComparison.OrdinalIgnoreCase)) return null;
            return int.TryParse(key.Substring(4), out var id) ? id : (int?)null;
        }

        // The equal-split reference allocation, computed in memory from the same direct-cost base
        // as the Programme Cost report (mirrors the engine's equal fallback across Mandate targets).
        private async Task<Dictionary<(int entityId, int programId), decimal>> EqualAllocationNetByProgram(
            int year, List<int> entityIds, Dictionary<(int entityId, int programId), decimal> directMap)
        {
            var result = new Dictionary<(int entityId, int programId), decimal>();
            if (entityIds.Count == 0) return result;

            var programs = await _db.Programs.AsNoTracking()
                .Where(p => entityIds.Contains(p.EntityId))
                .Select(p => new { p.ProgramId, p.EntityId, p.ProgramType, p.IsActive })
                .ToListAsync();

            foreach (var eid in entityIds)
            {
                var supports = programs
                    .Where(p => p.EntityId == eid && string.Equals(p.ProgramType, "Support", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.ProgramId).ToList();
                var mandates = programs
                    .Where(p => p.EntityId == eid && p.IsActive
                        && !string.Equals(p.ProgramType, "Support", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.ProgramId).OrderBy(id => id).ToList();
                if (supports.Count == 0 || mandates.Count == 0) continue;

                foreach (var sp in supports)
                {
                    var pool = directMap.GetValueOrDefault((eid, sp));
                    if (pool <= 0m) continue;

                    var kOut = (eid, sp);
                    result[kOut] = result.GetValueOrDefault(kOut) - pool;

                    var running = 0m;
                    for (var i = 0; i < mandates.Count; i++)
                    {
                        var share = (i == mandates.Count - 1)
                            ? pool - running
                            : Math.Round(pool / mandates.Count, 2, MidpointRounding.AwayFromZero);
                        running += share;
                        var kIn = (eid, mandates[i]);
                        result[kIn] = result.GetValueOrDefault(kIn) + share;
                    }
                }
            }
            return result;
        }

        // Selectable scenarios: the equal-split reference first, then every retained run in scope
        // (Posted = official, plus Scenario and Superseded runs), newest first.
        private async Task<List<AllocationScenarioOptionVm>> BuildScenarioOptions(int year, List<int> entityIds)
        {
            var options = new List<AllocationScenarioOptionVm>
            {
                new AllocationScenarioOptionVm
                {
                    Key = EqualScenarioKey,
                    Label = "Standard equal allocation",
                    StatusLabel = "Reference",
                    Description = "Support cost split equally across Mandate programmes. Computed on the fly - no run needed."
                }
            };
            if (entityIds.Count == 0) return options;

            try
            {
                var runs = await _db.AllocationRuns.AsNoTracking()
                    .Where(r => r.BudgetYear == year
                        && (r.Status == "Posted" || r.Status == "Scenario" || r.Status == "Superseded")
                        && (r.EntityId == null || entityIds.Contains(r.EntityId.Value)))
                    .OrderByDescending(r => r.Status == "Posted")
                    .ThenByDescending(r => r.RunAt)
                    .Take(MaxScenarioRuns)
                    .ToListAsync();
                if (runs.Count == 0) return options;

                var runIds = runs.Select(r => r.RunId).ToList();
                var totals = await _db.AllocationTransactions.AsNoTracking()
                    .Where(t => runIds.Contains(t.RunId) && entityIds.Contains(t.EntityId))
                    .GroupBy(t => t.RunId)
                    .Select(g => new { RunId = g.Key, Total = g.Sum(x => x.Amount) })
                    .ToListAsync();
                var totalMap = totals.ToDictionary(x => x.RunId, x => x.Total);

                foreach (var r in runs)
                {
                    options.Add(new AllocationScenarioOptionVm
                    {
                        Key = "run:" + r.RunId,
                        RunId = r.RunId,
                        Label = string.IsNullOrWhiteSpace(r.ScenarioName) ? "Run #" + r.RunId : r.ScenarioName!,
                        StatusLabel = r.Status == "Posted" ? "Official" : r.Status,
                        IsOfficial = r.Status == "Posted",
                        RunAt = r.RunAt,
                        TotalAllocated = totalMap.GetValueOrDefault(r.RunId),
                        Description = $"Run #{r.RunId} - {r.RunAt:yyyy-MM-dd HH:mm} UTC"
                            + (string.IsNullOrWhiteSpace(r.RunBy) ? "" : " by " + r.RunBy)
                    });
                }
            }
            catch
            {
                // Allocation tables may not exist yet -> only the reference scenario is offered.
            }
            return options;
        }

        // Programme cost AFTER allocation for each selected scenario, side by side.
        // Read-only and additive: the standard sections above keep using the official run.
        private async Task<AllocationScenarioComparisonVm> BuildScenarioComparison(int year, int? entityId, string[]? selectedKeys, bool compare)
        {
            var vm = new AllocationScenarioComparisonVm { Compare = compare };
            if (entityId.HasValue && entityId.Value <= 0) return vm;

            var entities = await EntityScopeList(entityId);
            if (entities.Count == 0) return vm;
            var entityIds = entities.Select(e => e.EntityId).ToList();
            var entityNameMap = entities.ToDictionary(e => e.EntityId);

            vm.Options = await BuildScenarioOptions(year, entityIds);
            if (!compare) return vm;

            // Default selection: the official run (when one exists) against the equal-split reference.
            var keys = (selectedKeys ?? Array.Empty<string>())
                .Where(k => vm.Options.Any(o => o.Key == k))
                .Distinct().ToList();
            if (keys.Count == 0)
            {
                var official = vm.Options.FirstOrDefault(o => o.IsOfficial);
                if (official != null) keys.Add(official.Key);
                keys.Add(EqualScenarioKey);
            }
            // Keep the picker order so the columns read predictably.
            keys = vm.Options.Where(o => keys.Contains(o.Key)).Select(o => o.Key).ToList();
            vm.SelectedKeys = keys;
            vm.BaselineKey = keys.FirstOrDefault() ?? "";

            var (directMap, overheadMap) = await ProgrammeDirectCost(year, entityIds);
            var entityDirectTotal = directMap
                .GroupBy(kv => kv.Key.entityId)
                .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));

            var netByScenario = new Dictionary<string, Dictionary<(int entityId, int programId), decimal>>();
            foreach (var key in keys)
            {
                netByScenario[key] = key == EqualScenarioKey
                    ? await EqualAllocationNetByProgram(year, entityIds, directMap)
                    : await AllocationNetByProgram(year, entityIds, RunIdFromKey(key));
            }

            var allKeys = directMap.Keys.ToHashSet();
            foreach (var net in netByScenario.Values)
                foreach (var k in net.Keys) allKeys.Add(k);

            var programIds = allKeys.Select(k => k.programId).Distinct().ToList();
            var programs = await _db.Programs.AsNoTracking()
                .Where(p => programIds.Contains(p.ProgramId))
                .Select(p => new { p.ProgramId, p.ProgramCode, p.ProgramName, p.ProgramType })
                .ToListAsync();
            var programMap = programs.ToDictionary(p => p.ProgramId);

            foreach (var key in allKeys)
            {
                var (entId, progId) = key;
                if (!programMap.TryGetValue(progId, out var prog)) continue;

                var direct = directMap.GetValueOrDefault(key);

                // Legacy untagged-overhead pool share, identical in every scenario (0 when everything is tagged).
                var pool = overheadMap.GetValueOrDefault(entId);
                var entTotal = entityDirectTotal.GetValueOrDefault(entId);
                var overheadShare = entTotal > 0
                    ? Math.Round(pool * (direct / entTotal), 2, MidpointRounding.AwayFromZero)
                    : 0m;

                entityNameMap.TryGetValue(entId, out var ent);
                var row = new AllocationScenarioRowVm
                {
                    EntityCode = ent?.EntityCode ?? "",
                    ProgramCode = prog.ProgramCode,
                    ProgramName = prog.ProgramName,
                    ProgramType = prog.ProgramType,
                    Direct = direct
                };

                var anyValue = direct != 0m;
                foreach (var sk in keys)
                {
                    var net = netByScenario[sk].GetValueOrDefault(key);
                    row.TotalByScenario[sk] = direct + overheadShare + net;
                    if (net != 0m) anyValue = true;
                }
                if (!anyValue) continue;

                vm.Rows.Add(row);
            }

            vm.Rows = vm.Rows
                .OrderBy(r => r.EntityCode)
                .ThenByDescending(r => vm.BaselineKey.Length > 0 ? r.TotalByScenario.GetValueOrDefault(vm.BaselineKey) : r.Direct)
                .ToList();
            return vm;
        }

        private const string DefaultPeriod = "MidYear";

        private static string ComputeKpiStatus(string? direction, decimal? baseline, decimal? target, decimal? actual)
        {
            if (baseline == null || target == null || actual == null) return "";
            var denom = target.Value - baseline.Value;
            decimal progress;
            if (denom == 0)
            {
                var up = !string.Equals(direction, "DOWN", StringComparison.OrdinalIgnoreCase);
                progress = up ? (actual.Value >= target.Value ? 1m : 0m) : (actual.Value <= target.Value ? 1m : 0m);
            }
            else
            {
                // (actual - baseline) / (target - baseline) is direction-agnostic: denom sign follows direction
                progress = (actual.Value - baseline.Value) / denom;
            }
            if (progress >= 0.5m) return "Green";
            if (progress >= 0.1m) return "Watch";
            return "Behind";
        }

        private static string ResolveKpiStatus(Kpis k)
        {
            if (!string.IsNullOrWhiteSpace(k.Status)) return k.Status!;
            return ComputeKpiStatus(k.Direction, k.Baseline, k.Target, k.ActualValue);
        }

        private async Task<List<Kpis>> LoadKpis(int year, int? entityId)
        {
            if (entityId.HasValue && entityId.Value <= 0) return new List<Kpis>();
            var q = _db.Kpis.AsNoTracking().Where(k => k.BudgetYear == year && k.Period == DefaultPeriod);
            if (entityId.HasValue) q = q.Where(k => k.EntityId == entityId.Value);
            return await q.ToListAsync();
        }

        private async Task<List<KpiScorecardRowVm>> BuildKpiScorecard(int year, int? entityId)
        {
            var kpis = await LoadKpis(year, entityId);
            if (kpis.Count == 0) return new List<KpiScorecardRowVm>();

            var entities = await EntityScopeList(entityId);
            var nameMap = entities.ToDictionary(e => e.EntityId);

            return kpis
                .GroupBy(k => k.EntityId)
                .Select(g =>
                {
                    var statuses = g.Select(ResolveKpiStatus).ToList();
                    var green = statuses.Count(s => s == "Green");
                    var watch = statuses.Count(s => s == "Watch");
                    var behind = statuses.Count(s => s == "Behind");
                    var total = statuses.Count;
                    nameMap.TryGetValue(g.Key, out var ent);
                    return new KpiScorecardRowVm
                    {
                        EntityCode = ent?.EntityCode ?? "",
                        EntityName = ent?.EntityName ?? "",
                        Total = total,
                        Green = green,
                        Watch = watch,
                        Behind = behind,
                        PctGreen = total > 0 ? Math.Round((decimal)green / total * 100m, 0) : 0m
                    };
                })
                .OrderBy(x => x.EntityCode)
                .ToList();
        }

        private async Task<List<KpiDetailRowVm>> BuildKpiDetails(int year, int? entityId)
        {
            var kpis = await LoadKpis(year, entityId);
            if (kpis.Count == 0) return new List<KpiDetailRowVm>();

            var entities = await EntityScopeList(entityId);
            var entMap = entities.ToDictionary(e => e.EntityId);
            var progIds = kpis.Where(k => k.ProgramId != null).Select(k => k.ProgramId!.Value).Distinct().ToList();
            var progs = await _db.Programs.AsNoTracking()
                .Where(p => progIds.Contains(p.ProgramId))
                .Select(p => new { p.ProgramId, p.ProgramCode })
                .ToListAsync();
            var progMap = progs.ToDictionary(p => p.ProgramId, p => p.ProgramCode);

            return kpis
                .Select(k =>
                {
                    entMap.TryGetValue(k.EntityId, out var ent);
                    return new KpiDetailRowVm
                    {
                        EntityCode = ent?.EntityCode ?? "",
                        ProgramCode = k.ProgramId != null && progMap.TryGetValue(k.ProgramId.Value, out var pc) ? pc : "",
                        KpiName = k.KpiName,
                        Unit = k.Unit ?? "",
                        Baseline = k.Baseline,
                        Target = k.Target,
                        Actual = k.ActualValue,
                        Status = ResolveKpiStatus(k)
                    };
                })
                .OrderBy(x => x.EntityCode).ThenBy(x => x.ProgramCode).ThenBy(x => x.KpiName)
                .ToList();
        }

        private async Task<List<MaturityRowVm>> BuildMaturityLadder(int year, int? entityId)
        {
            if (entityId.HasValue && entityId.Value <= 0) return new List<MaturityRowVm>();

            var entities = await EntityScopeList(entityId);
            if (entities.Count == 0) return new List<MaturityRowVm>();
            var entityIds = entities.Select(e => e.EntityId).ToList();

            var assessments = await _db.MaturityAssessments.AsNoTracking()
                .Where(m => m.BudgetYear == year && m.Period == DefaultPeriod && entityIds.Contains(m.EntityId))
                .ToListAsync();
            var map = assessments.ToDictionary(m => m.EntityId);

            return entities.Select(e =>
            {
                map.TryGetValue(e.EntityId, out var m);
                return new MaturityRowVm
                {
                    EntityCode = e.EntityCode,
                    EntityName = e.EntityName,
                    Stage = m?.Stage,
                    Form = m?.Form ?? "",
                    StatusLabel = m?.StatusLabel ?? ""
                };
            })
            .OrderByDescending(x => x.Stage ?? -1m)
            .ThenBy(x => x.EntityCode)
            .ToList();
        }

        private async Task<List<ActivityUnitCostRowVm>> BuildActivityUnitCosts(int year, int? entityId)
        {
            if (entityId.HasValue && entityId.Value <= 0) return new List<ActivityUnitCostRowVm>();

            var entities = await EntityScopeList(entityId);
            if (entities.Count == 0) return new List<ActivityUnitCostRowVm>();
            var entityIds = entities.Select(e => e.EntityId).ToList();

            var costMap = await ComputeActivityCostMap(year, entityIds);
            if (costMap.Count == 0) return new List<ActivityUnitCostRowVm>();

            var actualMap = await ComputeActivityActualMap(year, entityIds);
            var hasActuals = await HasActualsForScope(year, entityIds);

            var activityIds = costMap.Keys.ToList();
            var activities = await (
                from act in _db.Activities.AsNoTracking()
                join prog in _db.Programs.AsNoTracking() on act.ProgramId equals prog.ProgramId
                where activityIds.Contains(act.ActivityId)
                select new { act.ActivityId, act.ActivityCode, act.ActivityName, prog.ProgramId, prog.EntityId, prog.ProgramCode, prog.ProgramName }
            ).ToListAsync();
            var actMap = activities.ToDictionary(a => a.ActivityId);

            var outputs = await _db.ActivityOutputs.AsNoTracking()
                .Where(o => o.BudgetYear == year && activityIds.Contains(o.ActivityId) && o.IsPrimary)
                .ToListAsync();
            var outputMap = outputs.GroupBy(o => o.ActivityId).ToDictionary(g => g.Key, g => g.First());

            var entNameMap = entities.ToDictionary(e => e.EntityId);

            // Activity cost AFTER step-down allocation (Total). Support-programme activities net down
            // (cost swept out); Mandate-programme activities net up. When no run is posted, Total == Direct.
            var totalCostMap = await ComputeActivityTotalCostMap(year, entityIds, costMap);

            var rows = new List<ActivityUnitCostRowVm>();
            foreach (var kv in costMap)
            {
                if (!actMap.TryGetValue(kv.Key, out var a)) continue;
                if (!entityIds.Contains(a.EntityId)) continue;
                outputMap.TryGetValue(kv.Key, out var o);
                entNameMap.TryGetValue(a.EntityId, out var ent);
                var volume = o?.OutputVolume ?? 0m;
                var direct = kv.Value;

                var total = totalCostMap.TryGetValue(kv.Key, out var tv) ? tv : direct;
                var allocated = total - direct;

                decimal? actualCost = hasActuals
                    ? Math.Round(actualMap.TryGetValue(kv.Key, out var av) ? av : 0m, 2, MidpointRounding.AwayFromZero)
                    : (decimal?)null;
                decimal? variance = actualCost.HasValue ? actualCost.Value - direct : (decimal?)null;
                rows.Add(new ActivityUnitCostRowVm
                {
                    EntityCode = ent?.EntityCode ?? "",
                    ProgramCode = a.ProgramCode,
                    ActivityCode = a.ActivityCode,
                    ActivityName = a.ActivityName,
                    AnnualCost = direct,
                    AllocatedCost = allocated,
                    TotalCost = total,
                    ActualCost = actualCost,
                    Variance = variance,
                    OutputMeasure = o?.OutputMeasure ?? "",
                    OutputVolume = volume,
                    // Cost per output uses the cost AFTER allocation.
                    CostPerOutput = volume > 0 ? Math.Round(total / volume, 2, MidpointRounding.AwayFromZero) : 0m
                });
            }
            return rows.OrderBy(x => x.EntityCode).ThenBy(x => x.ProgramCode).ThenBy(x => x.ActivityCode).ToList();
        }

        // True when a step-down allocation run has been posted for the scope (so activity/programme
        // costs include allocated cost). Used to drive the "direct cost only" note in the UI.
        private async Task<bool> HasPostedAllocation(int year, List<int> entityIds)
        {
            if (entityIds.Count == 0) return false;
            return await _db.AllocationRuns.AsNoTracking()
                .AnyAsync(r => r.BudgetYear == year && r.Status == "Posted"
                    && (r.EntityId == null || entityIds.Contains(r.EntityId.Value)));
        }

        // Activity cost AFTER step-down allocation. Starts from the direct cost map and adds each
        // activity's share of its programme's net allocation (pro-rata to direct cost). When no run
        // is posted the returned map equals the direct map (Total == Direct).
        private async Task<Dictionary<int, decimal>> ComputeActivityTotalCostMap(int year, List<int> entityIds, Dictionary<int, decimal> directCostMap)
        {
            var totals = new Dictionary<int, decimal>(directCostMap);
            if (directCostMap.Count == 0) return totals;

            var netAlloc = await AllocationNetByProgram(year, entityIds);
            if (netAlloc.Count == 0) return totals;

            var actIds = directCostMap.Keys.ToList();
            var actProg = await (
                from a in _db.Activities.AsNoTracking()
                join p in _db.Programs.AsNoTracking() on a.ProgramId equals p.ProgramId
                where actIds.Contains(a.ActivityId)
                select new { a.ActivityId, p.ProgramId, p.EntityId }
            ).ToListAsync();
            var actProgMap = actProg.ToDictionary(x => x.ActivityId);

            var progBase = new Dictionary<(int entityId, int programId), decimal>();
            foreach (var kv in directCostMap)
                if (actProgMap.TryGetValue(kv.Key, out var ap))
                {
                    var pk = (ap.EntityId, ap.ProgramId);
                    progBase[pk] = progBase.GetValueOrDefault(pk) + kv.Value;
                }

            foreach (var kv in directCostMap)
            {
                if (!actProgMap.TryGetValue(kv.Key, out var ap)) continue;
                var pk = (ap.EntityId, ap.ProgramId);
                var net = netAlloc.TryGetValue(pk, out var na) ? na : 0m;
                var bt = progBase.TryGetValue(pk, out var b) ? b : 0m;
                var alloc = bt > 0m ? Math.Round(net * (kv.Value / bt), 2, MidpointRounding.AwayFromZero) : 0m;
                totals[kv.Key] = kv.Value + alloc;
            }
            return totals;
        }

        // Cost per KPI: the cost linked to each activity is distributed across ALL of that activity's
        // KPIs (Input / Output / Outcome - not only Output), weighted by each KPI's CostWeight.
        // When an activity's KPIs all have null/zero weight, the split falls back to EQUAL.
        // Weighted shares always sum back to the full activity cost (no cost is lost or double-counted),
        // so management can see the cost sitting on non-output KPIs and decide about it.
        // Budget cost drives the planned cost/output; derived actual cost (when actuals exist) drives
        // the actual cost/output. Volume = KPI Actual (achieved) with Target as the fallback.
        private async Task<List<CostPerOutputRowVm>> BuildCostPerOutput(int year, int? entityId)
        {
            if (entityId.HasValue && entityId.Value <= 0) return new List<CostPerOutputRowVm>();

            var entities = await EntityScopeList(entityId);
            if (entities.Count == 0) return new List<CostPerOutputRowVm>();
            var entityIds = entities.Select(e => e.EntityId).ToList();
            var entMap = entities.ToDictionary(e => e.EntityId);

            // ALL activity-linked KPIs, regardless of KPI Type.
            var activityKpis = (await LoadKpis(year, entityId))
                .Where(k => k.ActivityId != null)
                .ToList();
            if (activityKpis.Count == 0) return new List<CostPerOutputRowVm>();

            // Cost distributed to KPIs is the activity cost AFTER step-down allocation (Total),
            // so KPIs on Mandate activities carry the allocated-in cost and Support activities ~0.
            var directCostMap = await ComputeActivityCostMap(year, entityIds);
            var costMap = await ComputeActivityTotalCostMap(year, entityIds, directCostMap);
            var actualMap = await ComputeActivityActualMap(year, entityIds);
            var hasActuals = await HasActualsForScope(year, entityIds);

            var actIds = activityKpis.Select(k => k.ActivityId!.Value).Distinct().ToList();
            var actMeta = await (
                from a in _db.Activities.AsNoTracking()
                join p in _db.Programs.AsNoTracking() on a.ProgramId equals p.ProgramId
                where actIds.Contains(a.ActivityId)
                select new { a.ActivityId, a.ActivityCode, a.ActivityName, p.EntityId, p.ProgramCode }
            ).ToListAsync();
            var actMetaMap = actMeta.ToDictionary(a => a.ActivityId);

            // KPIs grouped by activity, plus the total CostWeight per activity (for weighted split).
            var kpisByActivity = activityKpis.GroupBy(k => k.ActivityId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            var rows = new List<CostPerOutputRowVm>();
            foreach (var k in activityKpis)
            {
                var actId = k.ActivityId!.Value;
                if (!actMetaMap.TryGetValue(actId, out var a)) continue;
                if (!entityIds.Contains(a.EntityId)) continue;

                var siblings = kpisByActivity[actId];
                var count = siblings.Count;
                // Weighted share: use CostWeight; if the whole activity's KPIs are all zero/null,
                // fall back to equal shares so 100% of the activity cost is still distributed.
                var weightSum = siblings.Sum(s => Math.Max(0m, s.CostWeight ?? 0m));
                var myWeight = Math.Max(0m, k.CostWeight ?? 0m);
                decimal share = weightSum > 0m ? (myWeight / weightSum) : (count > 0 ? 1m / count : 0m);

                var activityBudget = costMap.GetValueOrDefault(actId);
                var allocatedBudget = Math.Round(activityBudget * share, 2, MidpointRounding.AwayFromZero);

                decimal? allocatedActual = null;
                if (hasActuals)
                {
                    var activityActual = actualMap.GetValueOrDefault(actId);
                    allocatedActual = Math.Round(activityActual * share, 2, MidpointRounding.AwayFromZero);
                }

                var volumeActual = k.ActualValue;
                var volumeTarget = k.Target;
                decimal? costPerPlanned = (volumeTarget.HasValue && volumeTarget.Value != 0m)
                    ? Math.Round(allocatedBudget / volumeTarget.Value, 2, MidpointRounding.AwayFromZero) : (decimal?)null;
                decimal? costPerActual = (allocatedActual.HasValue && volumeActual.HasValue && volumeActual.Value != 0m)
                    ? Math.Round(allocatedActual.Value / volumeActual.Value, 2, MidpointRounding.AwayFromZero) : (decimal?)null;

                entMap.TryGetValue(a.EntityId, out var ent);
                rows.Add(new CostPerOutputRowVm
                {
                    EntityCode = ent?.EntityCode ?? "",
                    ProgramCode = a.ProgramCode,
                    ActivityLabel = a.ActivityCode + " - " + a.ActivityName,
                    KpiName = k.KpiName,
                    KpiType = k.KpiType ?? "",
                    Unit = k.Unit ?? "",
                    OutputKpiCount = count,
                    CostWeight = myWeight,
                    AllocatedBudget = allocatedBudget,
                    AllocatedActual = allocatedActual,
                    OutputTarget = volumeTarget,
                    OutputActual = volumeActual,
                    CostPerPlannedOutput = costPerPlanned,
                    CostPerActualOutput = costPerActual
                });
            }
            return rows
                .OrderBy(x => x.EntityCode).ThenBy(x => x.ProgramCode).ThenBy(x => x.ActivityLabel).ThenBy(x => x.KpiName)
                .ToList();
        }

        // Activity cost = direct non-revenue budget lines tagged to the activity + HR allocated to it.
        private async Task<Dictionary<int, decimal>> ComputeActivityCostMap(int year, List<int> entityIds)
        {
            var budgetByActivity = await (
                from b in _db.BudgetLines.AsNoTracking()
                join cat in _db.Categories.AsNoTracking() on b.CategoryId equals cat.CategoryId
                where b.BudgetYear == year && entityIds.Contains(b.EntityId)
                      && cat.CategoryCode != "REVENUE" && b.ActivityId != null
                group b.Amount by b.ActivityId!.Value into g
                select new { ActivityId = g.Key, Amount = g.Sum() }
            ).ToListAsync();

            var hrByActivity = await (
                from a in _db.HrEmployeeCostAllocations.AsNoTracking()
                join emp in _db.HrEmployeeCosts.AsNoTracking() on a.EmployeeCostId equals emp.EmployeeCostId
                where emp.BudgetYear == year && emp.EntityId != null && entityIds.Contains(emp.EntityId.Value)
                group a.AllocatedAmount by a.ActivityId into g
                select new { ActivityId = g.Key, Amount = g.Sum() }
            ).ToListAsync();

            var costMap = new Dictionary<int, decimal>();
            foreach (var x in budgetByActivity) costMap[x.ActivityId] = costMap.GetValueOrDefault(x.ActivityId) + x.Amount;
            foreach (var x in hrByActivity) costMap[x.ActivityId] = costMap.GetValueOrDefault(x.ActivityId) + x.Amount;
            return costMap;
        }

        // Whether any current-year actuals have been imported for the scope (drives the "not uploaded" note).
        private async Task<bool> HasActualsForScope(int year, List<int> entityIds)
        {
            if (entityIds.Count == 0) return false;
            if (await _db.ActualPostings.AsNoTracking()
                .AnyAsync(p => p.BudgetYear == year && entityIds.Contains(p.EntityId))) return true;
            return await _db.HrActualPostings.AsNoTracking()
                .AnyAsync(p => p.BudgetYear == year && entityIds.Contains(p.EntityId));
        }

        // Derived actual per activity. Actuals are posted at GL level, so we split each GL's actual
        // across the activities that budgeted on that GL, proportional to their budget share on that GL.
        // Non-HR budget-by-GL comes from BudgetLines -> Items -> GLAccounts; HR from allocations x employee GL.
        private async Task<Dictionary<int, decimal>> ComputeActivityActualMap(int year, List<int> entityIds)
        {
            var result = new Dictionary<int, decimal>();
            if (entityIds.Count == 0) return result;

            var budgetByActGl = await (
                from b in _db.BudgetLines.AsNoTracking()
                join cat in _db.Categories.AsNoTracking() on b.CategoryId equals cat.CategoryId
                join it in _db.Items.AsNoTracking() on b.ItemId equals it.ItemId
                join gl in _db.GLAccounts.AsNoTracking() on it.GLAccountId equals gl.GLAccountId
                where b.BudgetYear == year && entityIds.Contains(b.EntityId)
                      && cat.CategoryCode != "REVENUE" && b.ActivityId != null
                group b.Amount by new { ActivityId = b.ActivityId!.Value, gl.GLCode } into g
                select new { g.Key.ActivityId, g.Key.GLCode, Amount = g.Sum() }
            ).ToListAsync();

            var hrByActGl = await (
                from a in _db.HrEmployeeCostAllocations.AsNoTracking()
                join emp in _db.HrEmployeeCosts.AsNoTracking() on a.EmployeeCostId equals emp.EmployeeCostId
                where emp.BudgetYear == year && emp.EntityId != null && entityIds.Contains(emp.EntityId.Value)
                group a.AllocatedAmount by new { a.ActivityId, emp.GLCode } into g
                select new { g.Key.ActivityId, g.Key.GLCode, Amount = g.Sum() }
            ).ToListAsync();

            // budget share table: GLCode -> (ActivityId -> budget)
            var budgetActByGl = new Dictionary<string, Dictionary<int, decimal>>(StringComparer.OrdinalIgnoreCase);
            void AddBudget(string? gl, int activityId, decimal amount)
            {
                if (string.IsNullOrWhiteSpace(gl)) return;
                if (!budgetActByGl.TryGetValue(gl, out var m)) { m = new Dictionary<int, decimal>(); budgetActByGl[gl] = m; }
                m[activityId] = m.GetValueOrDefault(activityId) + amount;
            }
            foreach (var x in budgetByActGl) AddBudget(x.GLCode, x.ActivityId, x.Amount);
            foreach (var x in hrByActGl) AddBudget(x.GLCode, x.ActivityId, x.Amount);

            var actualByGl = (await _db.ActualPostings.AsNoTracking()
                    .Where(p => p.BudgetYear == year && entityIds.Contains(p.EntityId))
                    .Select(p => new { p.GLCode, p.Amount }).ToListAsync())
                .GroupBy(x => x.GLCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount), StringComparer.OrdinalIgnoreCase);

            foreach (var kv in actualByGl)
            {
                if (!budgetActByGl.TryGetValue(kv.Key, out var actMap)) continue; // GL not tied to any activity budget
                var totalBudget = actMap.Values.Sum();
                if (totalBudget <= 0) continue;
                foreach (var ab in actMap)
                {
                    var share = kv.Value * (ab.Value / totalBudget);
                    result[ab.Key] = result.GetValueOrDefault(ab.Key) + share;
                }
            }

            // EXACT HR: per-employee HR actuals split to activities by budgeted allocation share.
            var hrActList = await _db.HrActualPostings.AsNoTracking()
                .Where(p => p.BudgetYear == year && entityIds.Contains(p.EntityId) && p.EmployeeCostId != null)
                .Select(p => new { EmployeeCostId = p.EmployeeCostId!.Value, p.Amount })
                .ToListAsync();
            if (hrActList.Count > 0)
            {
                var empActual = hrActList.GroupBy(x => x.EmployeeCostId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
                var empIds = empActual.Keys.ToList();
                var allocByEmp = (await _db.HrEmployeeCostAllocations.AsNoTracking()
                        .Where(a => empIds.Contains(a.EmployeeCostId))
                        .Select(a => new { a.EmployeeCostId, a.ActivityId, a.AllocatedAmount }).ToListAsync())
                    .GroupBy(a => a.EmployeeCostId).ToDictionary(g => g.Key, g => g.ToList());
                foreach (var kv in empActual)
                {
                    if (!allocByEmp.TryGetValue(kv.Key, out var allocs)) continue;
                    var totalAlloc = allocs.Sum(a => a.AllocatedAmount);
                    if (totalAlloc <= 0m) continue;
                    foreach (var a in allocs)
                        result[a.ActivityId] = result.GetValueOrDefault(a.ActivityId) + kv.Value * (a.AllocatedAmount / totalAlloc);
                }
            }
            return result;
        }

        // KPI <-> cost linkage: ties each KPI to the cost of its tagged activity (preferred) or programme,
        // and computes direction-aware improvement and cost-per-unit-of-improvement.
        private async Task<List<KpiCostLinkRowVm>> BuildKpiCostLinkage(int year, int? entityId)
        {
            var kpis = await LoadKpis(year, entityId);
            if (kpis.Count == 0) return new List<KpiCostLinkRowVm>();

            var entities = await EntityScopeList(entityId);
            if (entities.Count == 0) return new List<KpiCostLinkRowVm>();
            var entityIds = entities.Select(e => e.EntityId).ToList();
            var entMap = entities.ToDictionary(e => e.EntityId);

            var activityCost = await ComputeActivityCostMap(year, entityIds);
            var programmeRows = await BuildProgrammeCosts(year, entityId);
            var programmeCost = programmeRows
                .GroupBy(p => p.ProgramId)
                .ToDictionary(g => g.Key, g => g.Sum(p => p.Total));

            var actIds = kpis.Where(k => k.ActivityId != null).Select(k => k.ActivityId!.Value).Distinct().ToList();
            var actMeta = await _db.Activities.AsNoTracking()
                .Where(a => actIds.Contains(a.ActivityId))
                .Select(a => new { a.ActivityId, a.ActivityCode, a.ActivityName })
                .ToListAsync();
            var actMetaMap = actMeta.ToDictionary(a => a.ActivityId);

            var progIds = kpis.Where(k => k.ProgramId != null).Select(k => k.ProgramId!.Value).Distinct().ToList();
            var progMeta = await _db.Programs.AsNoTracking()
                .Where(p => progIds.Contains(p.ProgramId))
                .Select(p => new { p.ProgramId, p.ProgramCode, p.ProgramName })
                .ToListAsync();
            var progMetaMap = progMeta.ToDictionary(p => p.ProgramId);

            var rows = new List<KpiCostLinkRowVm>();
            foreach (var k in kpis)
            {
                string linkLevel;
                string linkLabel;
                decimal? linkedCost = null;

                if (k.ActivityId != null && activityCost.TryGetValue(k.ActivityId.Value, out var ac))
                {
                    linkLevel = "Activity";
                    linkLabel = actMetaMap.TryGetValue(k.ActivityId.Value, out var am)
                        ? am.ActivityCode + " - " + am.ActivityName : "Activity " + k.ActivityId.Value;
                    linkedCost = ac;
                }
                else if (k.ProgramId != null && programmeCost.TryGetValue(k.ProgramId.Value, out var pc))
                {
                    linkLevel = "Programme";
                    linkLabel = progMetaMap.TryGetValue(k.ProgramId.Value, out var pm)
                        ? pm.ProgramCode + " - " + pm.ProgramName : "Programme " + k.ProgramId.Value;
                    linkedCost = pc;
                }
                else
                {
                    linkLevel = "Unlinked";
                    linkLabel = "";
                }

                decimal? improvement = null;
                if (k.Baseline != null && k.ActualValue != null)
                {
                    improvement = string.Equals(k.Direction, "DOWN", StringComparison.OrdinalIgnoreCase)
                        ? k.Baseline.Value - k.ActualValue.Value
                        : k.ActualValue.Value - k.Baseline.Value;
                }

                decimal? costPerImprovement = null;
                if (linkedCost.HasValue && improvement.HasValue && improvement.Value > 0)
                    costPerImprovement = Math.Round(linkedCost.Value / improvement.Value, 2, MidpointRounding.AwayFromZero);

                entMap.TryGetValue(k.EntityId, out var ent);
                rows.Add(new KpiCostLinkRowVm
                {
                    EntityCode = ent?.EntityCode ?? "",
                    EntityName = ent?.EntityName ?? "",
                    KpiName = k.KpiName,
                    Unit = k.Unit ?? "",
                    LinkLevel = linkLevel,
                    LinkLabel = linkLabel,
                    LinkedCost = linkedCost,
                    Baseline = k.Baseline,
                    Actual = k.ActualValue,
                    Improvement = improvement,
                    CostPerImprovement = costPerImprovement,
                    Status = ResolveKpiStatus(k)
                });
            }
            return rows
                .OrderBy(x => x.EntityCode)
                .ThenByDescending(x => x.LinkedCost ?? -1m)
                .ToList();
        }

        // Entity Profile: combines headline data (maturity, budget, FTE, KPI %) with narrative
        // notes (Performance Assessment, Key Outcomes, Performance Issues) per entity.
        private async Task<List<EntityProfileVm>> BuildEntityProfiles(int year, int? entityId,
            List<CostStructureRowVm> cost, List<ManpowerRowVm> manpower,
            List<KpiScorecardRowVm> scorecard, List<MaturityRowVm> maturity)
        {
            if (entityId.HasValue && entityId.Value <= 0) return new List<EntityProfileVm>();

            var entities = await EntityScopeList(entityId);
            if (entities.Count == 0) return new List<EntityProfileVm>();
            var entityIds = entities.Select(e => e.EntityId).ToList();

            var notes = await _db.EntityReviewNotes.AsNoTracking()
                .Where(n => n.BudgetYear == year && n.Period == DefaultPeriod && entityIds.Contains(n.EntityId))
                .OrderBy(n => n.SortOrder).ThenBy(n => n.EntityReviewNoteId)
                .ToListAsync();
            var notesByEntity = notes.GroupBy(n => n.EntityId).ToDictionary(g => g.Key, g => g.ToList());

            var costByCode = cost.GroupBy(x => x.EntityCode).ToDictionary(g => g.Key, g => g.First());
            var manpowerByCode = manpower.GroupBy(x => x.EntityCode).ToDictionary(g => g.Key, g => g.First());
            var scoreByCode = scorecard.GroupBy(x => x.EntityCode).ToDictionary(g => g.Key, g => g.First());
            var matByCode = maturity.GroupBy(x => x.EntityCode).ToDictionary(g => g.Key, g => g.First());

            var list = new List<EntityProfileVm>();
            foreach (var e in entities)
            {
                costByCode.TryGetValue(e.EntityCode, out var c);
                manpowerByCode.TryGetValue(e.EntityCode, out var m);
                scoreByCode.TryGetValue(e.EntityCode, out var s);
                matByCode.TryGetValue(e.EntityCode, out var mat);
                notesByEntity.TryGetValue(e.EntityId, out var ns);
                ns ??= new List<EntityReviewNotes>();

                bool IsType(EntityReviewNotes n, string t) => string.Equals(n.NoteType, t, StringComparison.OrdinalIgnoreCase);

                list.Add(new EntityProfileVm
                {
                    EntityCode = e.EntityCode,
                    EntityName = e.EntityName,
                    Stage = mat?.Stage,
                    Form = mat?.Form ?? "",
                    StatusLabel = mat?.StatusLabel ?? "",
                    Budget = c?.Total ?? 0m,
                    HeadCount = m?.HeadCount ?? 0,
                    KpiGreen = s?.Green ?? 0,
                    KpiTotal = s?.Total ?? 0,
                    PctGreen = s?.PctGreen ?? 0m,
                    Assessment = string.Join("\n\n", ns.Where(n => IsType(n, "Assessment") && !string.IsNullOrWhiteSpace(n.Body)).Select(n => n.Body!.Trim())),
                    Outcomes = ns.Where(n => IsType(n, "Outcome") && !string.IsNullOrWhiteSpace(n.Body)).Select(n => n.Body!.Trim()).ToList(),
                    Issues = ns.Where(n => IsType(n, "Issue") && !string.IsNullOrWhiteSpace(n.Body)).Select(n => n.Body!.Trim()).ToList()
                });
            }
            return list.OrderByDescending(x => x.Stage ?? -1m).ThenBy(x => x.EntityCode).ToList();
        }

        private async Task<List<ReviewNarratives>> LoadReviewNarratives(int year)
        {
            return await _db.ReviewNarratives.AsNoTracking()
                .Where(n => n.BudgetYear == year && n.Period == DefaultPeriod)
                .OrderBy(n => n.SortOrder).ThenBy(n => n.ReviewNarrativeId)
                .ToListAsync();
        }

        private static ReviewNarrativesVm ToNarrativeVm(List<ReviewNarratives> all)
        {
            bool Is(ReviewNarratives n, string s) => string.Equals(n.Section, s, StringComparison.OrdinalIgnoreCase);
            return new ReviewNarrativesVm
            {
                Findings = all.Where(n => Is(n, "Finding")).ToList(),
                Recommendations = all.Where(n => Is(n, "Recommendation")).ToList(),
                Actions = all.Where(n => Is(n, "Action")).ToList()
            };
        }

        private async Task<List<EntityRef>> EntityScopeList(int? entityId)
        {
            var q = _db.Entities.AsNoTracking().AsQueryable();
            if (entityId.HasValue) q = q.Where(e => e.EntityId == entityId.Value);
            return await q.OrderBy(e => e.EntityCode)
                .Select(e => new EntityRef { EntityId = e.EntityId, EntityCode = e.EntityCode, EntityName = e.EntityName })
                .ToListAsync();
        }

        // ---------- Excel worksheets ----------

        private static void ApplyHeaderStyle(IXLRange range)
        {
            range.Style.Font.Bold = true;
            range.Style.Fill.BackgroundColor = XLColor.FromHtml(BrandColors.HeaderHex);
            range.Style.Font.FontColor = XLColor.White;
            range.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        }

        private static void TitleRows(IXLWorksheet ws, string title, int year, string entityLabel, int colCount)
        {
            ws.Cell(1, 1).Value = title;
            ws.Cell(2, 1).Value = $"Year: {year}    Entity: {entityLabel}";
            ws.Range(1, 1, 1, colCount).Merge().Style.Font.Bold = true;
            ws.Range(1, 1, 1, colCount).Style.Font.FontSize = 14;
            ws.Range(2, 1, 2, colCount).Merge().Style.Font.Bold = true;
        }

        private static void BuildCostStructureSheet(XLWorkbook wb, List<CostStructureRowVm> rows, int year, string entityLabel)
        {
            var ws = wb.Worksheets.Add("Cost Structure");
            TitleRows(ws, "Cost Structure (Cost Shape)", year, entityLabel, 8);
            var r = 4;
            string[] headers = { "Entity", "Entity Name", "Manpower", "Consultancy", "Maintenance", "Other Operating", "Capital", "Total" };
            for (var i = 0; i < headers.Length; i++) ws.Cell(r, i + 1).Value = headers[i];
            ApplyHeaderStyle(ws.Range(r, 1, r, headers.Length));
            r++;
            foreach (var x in rows)
            {
                ws.Cell(r, 1).Value = x.EntityCode;
                ws.Cell(r, 2).Value = x.EntityName;
                ws.Cell(r, 3).Value = x.Manpower;
                ws.Cell(r, 4).Value = x.Consultancy;
                ws.Cell(r, 5).Value = x.Maintenance;
                ws.Cell(r, 6).Value = x.OtherOperating;
                ws.Cell(r, 7).Value = x.Capital;
                ws.Cell(r, 8).Value = x.Total;
                ws.Range(r, 3, r, 8).Style.NumberFormat.Format = "#,##0.00";
                r++;
            }
            ws.Columns(1, 8).AdjustToContents();
        }

        private static void BuildCapexSheet(XLWorkbook wb, List<CapexVarianceRowVm> rows, int year, string entityLabel)
        {
            var ws = wb.Worksheets.Add("Capex Discipline");
            TitleRows(ws, "Capex Discipline (Budget vs Mid-Year Actual, H1)", year, entityLabel, 7);
            var r = 4;
            string[] headers = { "Entity", "Entity Name", "Budget (Annual)", "Budget H1", "Actual H1", "Variance H1", "Variance %" };
            for (var i = 0; i < headers.Length; i++) ws.Cell(r, i + 1).Value = headers[i];
            ApplyHeaderStyle(ws.Range(r, 1, r, headers.Length));
            r++;
            foreach (var x in rows)
            {
                ws.Cell(r, 1).Value = x.EntityCode;
                ws.Cell(r, 2).Value = x.EntityName;
                ws.Cell(r, 3).Value = x.BudgetAnnual;
                ws.Cell(r, 4).Value = x.BudgetH1;
                ws.Cell(r, 5).Value = x.ActualH1;
                ws.Cell(r, 6).Value = x.VarianceH1;
                ws.Cell(r, 7).Value = x.VariancePct / 100m;
                ws.Range(r, 3, r, 6).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(r, 7).Style.NumberFormat.Format = "0.0%";
                r++;
            }
            ws.Columns(1, 7).AdjustToContents();
        }

        private static void BuildManpowerSheet(XLWorkbook wb, List<ManpowerRowVm> rows, int year, string entityLabel)
        {
            var ws = wb.Worksheets.Add("Manpower");
            TitleRows(ws, $"Manpower & Cost-per-FTE (OECD band {CostPerFteBandMin:#,##0}-{CostPerFteBandMax:#,##0} AED)", year, entityLabel, 6);
            var r = 4;
            string[] headers = { "Entity", "Entity Name", "Manpower Cost", "Head Count", "Cost / FTE", "Band Status" };
            for (var i = 0; i < headers.Length; i++) ws.Cell(r, i + 1).Value = headers[i];
            ApplyHeaderStyle(ws.Range(r, 1, r, headers.Length));
            r++;
            foreach (var x in rows)
            {
                ws.Cell(r, 1).Value = x.EntityCode;
                ws.Cell(r, 2).Value = x.EntityName;
                ws.Cell(r, 3).Value = x.ManpowerCost;
                ws.Cell(r, 4).Value = x.HeadCount;
                ws.Cell(r, 5).Value = x.CostPerFte;
                ws.Cell(r, 6).Value = x.BandStatus;
                ws.Range(r, 3, r, 3).Style.NumberFormat.Format = "#,##0.00";
                ws.Range(r, 5, r, 5).Style.NumberFormat.Format = "#,##0.00";
                r++;
            }
            ws.Columns(1, 6).AdjustToContents();
        }

        private static void BuildProgrammeSheet(XLWorkbook wb, List<ProgrammeCostRowVm> rows, int year, string entityLabel)
        {
            var ws = wb.Worksheets.Add("Programme Cost");
            TitleRows(ws, "Programme Cost (Direct + Allocated)", year, entityLabel, 7);
            var r = 4;
            string[] headers = { "Entity", "Entity Name", "Programme", "Programme Name", "Direct", "Allocated", "Total" };
            for (var i = 0; i < headers.Length; i++) ws.Cell(r, i + 1).Value = headers[i];
            ApplyHeaderStyle(ws.Range(r, 1, r, headers.Length));
            r++;
            foreach (var x in rows)
            {
                ws.Cell(r, 1).Value = x.EntityCode;
                ws.Cell(r, 2).Value = x.EntityName;
                ws.Cell(r, 3).Value = x.ProgramCode;
                ws.Cell(r, 4).Value = x.ProgramName;
                ws.Cell(r, 5).Value = x.Direct;
                ws.Cell(r, 6).Value = x.Allocated;
                ws.Cell(r, 7).Value = x.Total;
                ws.Range(r, 5, r, 7).Style.NumberFormat.Format = "#,##0.00";
                r++;
            }
            ws.Columns(1, 7).AdjustToContents();
        }

        private static void BuildKpiScorecardSheet(XLWorkbook wb, List<KpiScorecardRowVm> rows, int year, string entityLabel)
        {
            var ws = wb.Worksheets.Add("KPI Scorecard");
            TitleRows(ws, "KPI Performance Scorecard", year, entityLabel, 6);
            var r = 4;
            string[] headers = { "Entity", "Entity Name", "Total KPIs", "Green", "Watch", "Behind", "% Green" };
            for (var i = 0; i < headers.Length; i++) ws.Cell(r, i + 1).Value = headers[i];
            ApplyHeaderStyle(ws.Range(r, 1, r, headers.Length));
            r++;
            foreach (var x in rows)
            {
                ws.Cell(r, 1).Value = x.EntityCode;
                ws.Cell(r, 2).Value = x.EntityName;
                ws.Cell(r, 3).Value = x.Total;
                ws.Cell(r, 4).Value = x.Green;
                ws.Cell(r, 5).Value = x.Watch;
                ws.Cell(r, 6).Value = x.Behind;
                ws.Cell(r, 7).Value = x.PctGreen / 100m;
                ws.Cell(r, 7).Style.NumberFormat.Format = "0%";
                r++;
            }
            ws.Columns(1, 7).AdjustToContents();
        }

        private static void BuildKpiDetailSheet(XLWorkbook wb, List<KpiDetailRowVm> rows, int year, string entityLabel)
        {
            var ws = wb.Worksheets.Add("KPI Detail");
            TitleRows(ws, "KPI Detail (Baseline / Target / Actual)", year, entityLabel, 8);
            var r = 4;
            string[] headers = { "Entity", "Programme", "KPI", "Unit", "Baseline", "Target", "Actual", "Status" };
            for (var i = 0; i < headers.Length; i++) ws.Cell(r, i + 1).Value = headers[i];
            ApplyHeaderStyle(ws.Range(r, 1, r, headers.Length));
            r++;
            foreach (var x in rows)
            {
                ws.Cell(r, 1).Value = x.EntityCode;
                ws.Cell(r, 2).Value = x.ProgramCode;
                ws.Cell(r, 3).Value = x.KpiName;
                ws.Cell(r, 4).Value = x.Unit;
                if (x.Baseline.HasValue) ws.Cell(r, 5).Value = x.Baseline.Value;
                if (x.Target.HasValue) ws.Cell(r, 6).Value = x.Target.Value;
                if (x.Actual.HasValue) ws.Cell(r, 7).Value = x.Actual.Value;
                ws.Cell(r, 8).Value = x.Status;
                r++;
            }
            ws.Columns(1, 8).AdjustToContents();
        }

        private static void BuildMaturitySheet(XLWorkbook wb, List<MaturityRowVm> rows, int year, string entityLabel)
        {
            var ws = wb.Worksheets.Add("Maturity Ladder");
            TitleRows(ws, "PBB Maturity Ladder", year, entityLabel, 5);
            var r = 4;
            string[] headers = { "Entity", "Entity Name", "Stage", "Form", "Status" };
            for (var i = 0; i < headers.Length; i++) ws.Cell(r, i + 1).Value = headers[i];
            ApplyHeaderStyle(ws.Range(r, 1, r, headers.Length));
            r++;
            foreach (var x in rows)
            {
                ws.Cell(r, 1).Value = x.EntityCode;
                ws.Cell(r, 2).Value = x.EntityName;
                if (x.Stage.HasValue) ws.Cell(r, 3).Value = x.Stage.Value;
                ws.Cell(r, 4).Value = x.Form;
                ws.Cell(r, 5).Value = x.StatusLabel;
                ws.Cell(r, 3).Style.NumberFormat.Format = "0.0";
                r++;
            }
            ws.Columns(1, 5).AdjustToContents();
        }

        private static void BuildActivityUnitCostSheet(XLWorkbook wb, List<ActivityUnitCostRowVm> rows, int year, string entityLabel)
        {
            var ws = wb.Worksheets.Add("Activity Unit Cost");
            TitleRows(ws, "Activity Cost after Allocation (Total = Direct + Allocated; variance = Actual - Direct)", year, entityLabel, 11);
            var r = 4;
            string[] headers = { "Entity", "Programme", "Activity", "Direct", "Allocated", "Total", "Actual (derived)", "Variance", "Output Measure", "Output Volume", "Cost / Output" };
            for (var i = 0; i < headers.Length; i++) ws.Cell(r, i + 1).Value = headers[i];
            ApplyHeaderStyle(ws.Range(r, 1, r, headers.Length));
            r++;
            foreach (var x in rows)
            {
                ws.Cell(r, 1).Value = x.EntityCode;
                ws.Cell(r, 2).Value = x.ProgramCode;
                ws.Cell(r, 3).Value = string.IsNullOrWhiteSpace(x.ActivityCode) ? x.ActivityName : (x.ActivityCode + " - " + x.ActivityName);
                ws.Cell(r, 4).Value = x.AnnualCost;
                ws.Cell(r, 5).Value = x.AllocatedCost;
                ws.Cell(r, 6).Value = x.TotalCost;
                if (x.ActualCost.HasValue) ws.Cell(r, 7).Value = x.ActualCost.Value;
                if (x.Variance.HasValue) ws.Cell(r, 8).Value = x.Variance.Value;
                ws.Cell(r, 9).Value = x.OutputMeasure;
                ws.Cell(r, 10).Value = x.OutputVolume;
                ws.Cell(r, 11).Value = x.CostPerOutput;
                ws.Range(r, 4, r, 8).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(r, 10).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(r, 11).Style.NumberFormat.Format = "#,##0.00";
                r++;
            }
            ws.Columns(1, 11).AdjustToContents();
        }

        private static void BuildKpiCostLinkSheet(XLWorkbook wb, List<KpiCostLinkRowVm> rows, int year, string entityLabel)
        {
            var ws = wb.Worksheets.Add("KPI Cost Linkage");
            TitleRows(ws, "KPI <-> Cost Linkage (Cost per Unit Improvement)", year, entityLabel, 10);
            var r = 4;
            string[] headers = { "Entity", "KPI", "Unit", "Linked To", "Cost Driver", "Linked Cost", "Baseline", "Actual", "Improvement", "Cost / Improvement" };
            for (var i = 0; i < headers.Length; i++) ws.Cell(r, i + 1).Value = headers[i];
            ApplyHeaderStyle(ws.Range(r, 1, r, headers.Length));
            r++;
            foreach (var x in rows)
            {
                ws.Cell(r, 1).Value = x.EntityCode;
                ws.Cell(r, 2).Value = x.KpiName;
                ws.Cell(r, 3).Value = x.Unit;
                ws.Cell(r, 4).Value = x.LinkLevel;
                ws.Cell(r, 5).Value = x.LinkLabel;
                if (x.LinkedCost.HasValue) ws.Cell(r, 6).Value = x.LinkedCost.Value;
                if (x.Baseline.HasValue) ws.Cell(r, 7).Value = x.Baseline.Value;
                if (x.Actual.HasValue) ws.Cell(r, 8).Value = x.Actual.Value;
                if (x.Improvement.HasValue) ws.Cell(r, 9).Value = x.Improvement.Value;
                if (x.CostPerImprovement.HasValue) ws.Cell(r, 10).Value = x.CostPerImprovement.Value;
                ws.Cell(r, 6).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(r, 10).Style.NumberFormat.Format = "#,##0.00";
                r++;
            }
            ws.Columns(1, 10).AdjustToContents();
        }

        private static void BuildCostPerOutputSheet(XLWorkbook wb, List<CostPerOutputRowVm> rows, int year, string entityLabel)
        {
            var ws = wb.Worksheets.Add("Cost per KPI");
            TitleRows(ws, "Cost per KPI (activity cost distributed to all linked KPIs, weighted)", year, entityLabel, 14);
            var r = 4;
            string[] headers = { "Entity", "Programme", "Activity", "KPI", "Type", "Unit", "KPIs on Activity", "Weight", "Allocated Budget", "Allocated Actual", "Target", "Actual", "Cost / Planned Unit", "Cost / Actual Unit" };
            for (var i = 0; i < headers.Length; i++) ws.Cell(r, i + 1).Value = headers[i];
            ApplyHeaderStyle(ws.Range(r, 1, r, headers.Length));
            r++;
            foreach (var x in rows)
            {
                ws.Cell(r, 1).Value = x.EntityCode;
                ws.Cell(r, 2).Value = x.ProgramCode;
                ws.Cell(r, 3).Value = x.ActivityLabel;
                ws.Cell(r, 4).Value = x.KpiName;
                ws.Cell(r, 5).Value = x.KpiType;
                ws.Cell(r, 6).Value = x.Unit;
                ws.Cell(r, 7).Value = x.OutputKpiCount;
                ws.Cell(r, 8).Value = x.CostWeight;
                ws.Cell(r, 9).Value = x.AllocatedBudget;
                if (x.AllocatedActual.HasValue) ws.Cell(r, 10).Value = x.AllocatedActual.Value;
                if (x.OutputTarget.HasValue) ws.Cell(r, 11).Value = x.OutputTarget.Value;
                if (x.OutputActual.HasValue) ws.Cell(r, 12).Value = x.OutputActual.Value;
                if (x.CostPerPlannedOutput.HasValue) ws.Cell(r, 13).Value = x.CostPerPlannedOutput.Value;
                if (x.CostPerActualOutput.HasValue) ws.Cell(r, 14).Value = x.CostPerActualOutput.Value;
                ws.Range(r, 9, r, 10).Style.NumberFormat.Format = "#,##0.00";
                ws.Range(r, 13, r, 14).Style.NumberFormat.Format = "#,##0.00";
                r++;
            }
            ws.Columns(1, 14).AdjustToContents();
        }

        private static void BuildEntityProfileSheet(XLWorkbook wb, List<EntityProfileVm> rows, int year, string entityLabel)
        {
            var ws = wb.Worksheets.Add("Entity Profiles");
            TitleRows(ws, "Entity Profiles", year, entityLabel, 9);
            var r = 4;
            string[] headers = { "Entity", "Entity Name", "Stage", "Form", "Status", "Budget", "FTE", "KPIs Green", "% Green" };
            for (var i = 0; i < headers.Length; i++) ws.Cell(r, i + 1).Value = headers[i];
            ApplyHeaderStyle(ws.Range(r, 1, r, headers.Length));
            r++;
            foreach (var x in rows)
            {
                ws.Cell(r, 1).Value = x.EntityCode;
                ws.Cell(r, 2).Value = x.EntityName;
                if (x.Stage.HasValue) ws.Cell(r, 3).Value = x.Stage.Value;
                ws.Cell(r, 3).Style.NumberFormat.Format = "0.0";
                ws.Cell(r, 4).Value = x.Form;
                ws.Cell(r, 5).Value = x.StatusLabel;
                ws.Cell(r, 6).Value = x.Budget;
                ws.Cell(r, 6).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(r, 7).Value = x.HeadCount;
                ws.Cell(r, 8).Value = $"{x.KpiGreen} of {x.KpiTotal}";
                ws.Cell(r, 9).Value = x.PctGreen / 100m;
                ws.Cell(r, 9).Style.NumberFormat.Format = "0%";
                r++;
            }
            r++;
            foreach (var x in rows)
            {
                ws.Cell(r, 1).Value = $"{x.EntityCode} - Performance Assessment";
                ws.Range(r, 1, r, 9).Merge().Style.Font.Bold = true;
                r++;
                ws.Cell(r, 1).Value = x.Assessment;
                ws.Range(r, 1, r, 9).Merge().Style.Alignment.WrapText = true;
                r++;
                foreach (var o in x.Outcomes) { ws.Cell(r, 1).Value = "Outcome: " + o; ws.Range(r, 1, r, 9).Merge(); r++; }
                foreach (var iss in x.Issues) { ws.Cell(r, 1).Value = "Issue: " + iss; ws.Range(r, 1, r, 9).Merge(); r++; }
                r++;
            }
            ws.Columns(1, 9).AdjustToContents();
        }

        private static void BuildNarrativeSheet(XLWorkbook wb, ReviewNarrativesVm vm, int year, string entityLabel)
        {
            var ws = wb.Worksheets.Add("Findings & Actions");
            TitleRows(ws, "Headline Findings, Recommendations & 90-Day Plan", year, entityLabel, 4);
            var r = 4;

            ws.Cell(r, 1).Value = "Headline Findings";
            ws.Range(r, 1, r, 4).Merge().Style.Font.Bold = true; r++;
            foreach (var f in vm.Findings) { ws.Cell(r, 1).Value = f.Title; ws.Cell(r, 2).Value = f.Body; ws.Range(r, 2, r, 4).Merge(); r++; }
            r++;

            ws.Cell(r, 1).Value = "Recommendations (Executive Decisions)";
            ws.Range(r, 1, r, 4).Merge().Style.Font.Bold = true; r++;
            foreach (var rec in vm.Recommendations) { ws.Cell(r, 1).Value = rec.Title; ws.Cell(r, 2).Value = rec.Body; ws.Range(r, 2, r, 4).Merge(); r++; }
            r++;

            ws.Cell(r, 1).Value = "90-Day Plan";
            ws.Range(r, 1, r, 4).Merge().Style.Font.Bold = true; r++;
            string[] aHeaders = { "Action", "Owner", "Due", "Success Measure" };
            for (var i = 0; i < aHeaders.Length; i++) ws.Cell(r, i + 1).Value = aHeaders[i];
            ApplyHeaderStyle(ws.Range(r, 1, r, aHeaders.Length)); r++;
            foreach (var a in vm.Actions)
            {
                ws.Cell(r, 1).Value = a.Title;
                ws.Cell(r, 2).Value = a.Owner;
                ws.Cell(r, 3).Value = a.DueText;
                ws.Cell(r, 4).Value = a.SuccessMeasure;
                r++;
            }
            ws.Columns(1, 4).AdjustToContents();
        }
    }

    // ---------- View models ----------

    public class ManagementReviewVm
    {
        public int Year { get; set; }
        public bool IsAdmin { get; set; }
        public int? EntityId { get; set; }
        public List<SelectListItem> YearOptions { get; set; } = new();
        public List<SelectListItem> EntityOptions { get; set; } = new();
        public List<CostStructureRowVm> CostStructure { get; set; } = new();
        public List<CapexVarianceRowVm> CapexVariance { get; set; } = new();
        public List<ManpowerRowVm> Manpower { get; set; } = new();
        public List<ProgrammeCostRowVm> ProgrammeCosts { get; set; } = new();
        public List<KpiScorecardRowVm> KpiScorecard { get; set; } = new();
        public List<KpiDetailRowVm> KpiDetails { get; set; } = new();
        public List<MaturityRowVm> MaturityLadder { get; set; } = new();
        public List<ActivityUnitCostRowVm> ActivityUnitCosts { get; set; } = new();
        public bool ActualsUploaded { get; set; }
        // True when a step-down allocation run has been posted for the scope, so activity costs
        // include allocated cost. False => the figures are direct costs only.
        public bool AllocationPosted { get; set; }
        public List<KpiCostLinkRowVm> KpiCostLinks { get; set; } = new();
        public List<CostPerOutputRowVm> CostPerOutput { get; set; } = new();
        public List<EntityProfileVm> EntityProfiles { get; set; } = new();
        public ReviewNarrativesVm Narratives { get; set; } = new();
        // Opt-in allocation-scenario comparison (empty Rows unless the user asks for it).
        public AllocationScenarioComparisonVm Scenarios { get; set; } = new();
    }

    // ---------- Allocation scenario comparison ----------

    public class AllocationScenarioComparisonVm
    {
        public bool Compare { get; set; }
        public List<AllocationScenarioOptionVm> Options { get; set; } = new();
        public List<string> SelectedKeys { get; set; } = new();
        // First selected scenario: every other column is measured against it.
        public string BaselineKey { get; set; } = "";
        public List<AllocationScenarioRowVm> Rows { get; set; } = new();

        public AllocationScenarioOptionVm? Option(string key) => Options.FirstOrDefault(o => o.Key == key);
        public string Label(string key) => Option(key)?.Label ?? key;
    }

    public class AllocationScenarioOptionVm
    {
        public string Key { get; set; } = "";
        public int? RunId { get; set; }
        public string Label { get; set; } = "";
        // Official (latest Posted run), Scenario, Superseded, or Reference (equal split).
        public string StatusLabel { get; set; } = "";
        public bool IsOfficial { get; set; }
        public DateTime? RunAt { get; set; }
        public decimal TotalAllocated { get; set; }
        public string Description { get; set; } = "";
    }

    public class AllocationScenarioRowVm
    {
        public string EntityCode { get; set; } = "";
        public string ProgramCode { get; set; } = "";
        public string ProgramName { get; set; } = "";
        public string ProgramType { get; set; } = "";
        public decimal Direct { get; set; }
        // Cost after allocation per scenario key.
        public Dictionary<string, decimal> TotalByScenario { get; set; } = new();

        public decimal Total(string key) => TotalByScenario.TryGetValue(key, out var v) ? v : 0m;
        public decimal Variance(string key, string baselineKey) => Total(key) - Total(baselineKey);
        public decimal VariancePct(string key, string baselineKey)
        {
            var b = Total(baselineKey);
            return b == 0m ? 0m : Math.Round(Variance(key, baselineKey) / Math.Abs(b) * 100m, 1);
        }
    }

    public class EntityProfileVm
    {
        public string EntityCode { get; set; } = "";
        public string EntityName { get; set; } = "";
        public decimal? Stage { get; set; }
        public string Form { get; set; } = "";
        public string StatusLabel { get; set; } = "";
        public decimal Budget { get; set; }
        public int HeadCount { get; set; }
        public int KpiGreen { get; set; }
        public int KpiTotal { get; set; }
        public decimal PctGreen { get; set; }
        public string Assessment { get; set; } = "";
        public List<string> Outcomes { get; set; } = new();
        public List<string> Issues { get; set; } = new();
    }

    public class ReviewNarrativesVm
    {
        public List<ReviewNarratives> Findings { get; set; } = new();
        public List<ReviewNarratives> Recommendations { get; set; } = new();
        public List<ReviewNarratives> Actions { get; set; } = new();
        public bool HasAny => Findings.Count > 0 || Recommendations.Count > 0 || Actions.Count > 0;
    }

    public class EntityRef
    {
        public int EntityId { get; set; }
        public string EntityCode { get; set; } = "";
        public string EntityName { get; set; } = "";
    }

    public class CostStructureRowVm
    {
        public string EntityCode { get; set; } = "";
        public string EntityName { get; set; } = "";
        public decimal Manpower { get; set; }
        public decimal Consultancy { get; set; }
        public decimal Maintenance { get; set; }
        public decimal OtherOperating { get; set; }
        public decimal Capital { get; set; }
        public decimal Total => Manpower + Consultancy + Maintenance + OtherOperating + Capital;
        public decimal Pct(decimal part) => Total != 0 ? Math.Round(part / Total * 100m, 1) : 0m;
    }

    public class CapexVarianceRowVm
    {
        public string EntityCode { get; set; } = "";
        public string EntityName { get; set; } = "";
        public decimal BudgetAnnual { get; set; }
        public decimal BudgetH1 { get; set; }
        public decimal ActualH1 { get; set; }
        public decimal VarianceH1 { get; set; }
        public decimal VariancePct { get; set; }
    }

    public class ManpowerRowVm
    {
        public string EntityCode { get; set; } = "";
        public string EntityName { get; set; } = "";
        public decimal ManpowerCost { get; set; }
        public int HeadCount { get; set; }
        public decimal CostPerFte { get; set; }
        public string BandStatus { get; set; } = "";
    }

    public class ProgrammeCostRowVm
    {
        public int ProgramId { get; set; }
        public string EntityCode { get; set; } = "";
        public string EntityName { get; set; } = "";
        public string ProgramCode { get; set; } = "";
        public string ProgramName { get; set; } = "";
        public decimal Direct { get; set; }
        public decimal Allocated { get; set; }
        public decimal Total { get; set; }
    }

    public class KpiScorecardRowVm
    {
        public string EntityCode { get; set; } = "";
        public string EntityName { get; set; } = "";
        public int Total { get; set; }
        public int Green { get; set; }
        public int Watch { get; set; }
        public int Behind { get; set; }
        public decimal PctGreen { get; set; }
    }

    public class KpiDetailRowVm
    {
        public string EntityCode { get; set; } = "";
        public string ProgramCode { get; set; } = "";
        public string KpiName { get; set; } = "";
        public string Unit { get; set; } = "";
        public decimal? Baseline { get; set; }
        public decimal? Target { get; set; }
        public decimal? Actual { get; set; }
        public string Status { get; set; } = "";
    }

    public class MaturityRowVm
    {
        public string EntityCode { get; set; } = "";
        public string EntityName { get; set; } = "";
        public decimal? Stage { get; set; }
        public string Form { get; set; } = "";
        public string StatusLabel { get; set; } = "";
    }

    public class ActivityUnitCostRowVm
    {
        public string EntityCode { get; set; } = "";
        public string ProgramCode { get; set; } = "";
        public string ActivityCode { get; set; } = "";
        public string ActivityName { get; set; } = "";
        // Direct cost tagged to the activity (budget lines + HR), before any step-down allocation.
        public decimal AnnualCost { get; set; }
        // Activity's share of its programme's net step-down allocation (in from Mandate / out from
        // Support). Zero when no allocation run is posted. Can be negative for Support activities.
        public decimal AllocatedCost { get; set; }
        // Cost after allocation = AnnualCost + AllocatedCost.
        public decimal TotalCost { get; set; }
        // Derived actual (budget-share of GL actuals). Null when no actuals have been imported.
        public decimal? ActualCost { get; set; }
        // Variance = Actual - Budget (negative = under budget).
        public decimal? Variance { get; set; }
        public string OutputMeasure { get; set; } = "";
        public decimal OutputVolume { get; set; }
        public decimal CostPerOutput { get; set; }
    }

    public class KpiCostLinkRowVm
    {
        public string EntityCode { get; set; } = "";
        public string EntityName { get; set; } = "";
        public string KpiName { get; set; } = "";
        public string Unit { get; set; } = "";
        public string LinkLevel { get; set; } = "";
        public string LinkLabel { get; set; } = "";
        public decimal? LinkedCost { get; set; }
        public decimal? Baseline { get; set; }
        public decimal? Actual { get; set; }
        public decimal? Improvement { get; set; }
        public decimal? CostPerImprovement { get; set; }
        public string Status { get; set; } = "";
    }

    // Cost per KPI: activity cost distributed to ALL its linked KPIs (not only Output),
    // weighted by each KPI's CostWeight. Lets management see the cost sitting on
    // non-output KPIs (Input/Outcome) too.
    public class CostPerOutputRowVm
    {
        public string EntityCode { get; set; } = "";
        public string ProgramCode { get; set; } = "";
        public string ActivityLabel { get; set; } = "";
        public string KpiName { get; set; } = "";
        public string KpiType { get; set; } = "";
        public string Unit { get; set; } = "";
        public int OutputKpiCount { get; set; }        // count of KPIs sharing the activity
        public decimal CostWeight { get; set; }        // this KPI's weight in the split
        public decimal AllocatedBudget { get; set; }
        public decimal? AllocatedActual { get; set; }
        public decimal? OutputTarget { get; set; }
        public decimal? OutputActual { get; set; }
        public decimal? CostPerPlannedOutput { get; set; }
        public decimal? CostPerActualOutput { get; set; }
    }
}
