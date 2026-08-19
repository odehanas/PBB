using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GovBudget.Models;
using GovBudget.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Controllers
{
    /// <summary>
    /// Cost reallocation admin + engine: classify programs (Mandate/Support), define
    /// allocation rules and driver values, run the step-down allocation, and audit
    /// the resulting transactions. Fully additive.
    ///
    /// Access: SYSADMIN and global ADMINs manage all entities. Entity-scoped ADMINs are
    /// allowed in but every read and write is locked to their own entity.
    /// </summary>
    [Authorize(Roles = "ADMIN,SYSADMIN")]
    public class AllocationController : Controller
    {
        private readonly GovBudgetContext _db;

        public AllocationController(GovBudgetContext db)
        {
            _db = db;
        }

        // The entity an entity-scoped admin is locked to (null for global admins / SYSADMIN).
        private int? GetEntityClaimId()
        {
            var entityClaim = User.Claims.FirstOrDefault(c => c.Type == "EntityId")?.Value;
            if (!int.TryParse(entityClaim, out var eid) || eid <= 0) return null;
            return eid;
        }

        // SYSADMIN, or an ADMIN with no entity scope, may act across all entities.
        private bool IsGlobalAdmin()
        {
            if (User.IsInRole("SYSADMIN")) return true;
            if (!User.IsInRole("ADMIN")) return false;
            return !GetEntityClaimId().HasValue;
        }

        // Global admins: honor the requested entity (null = all).
        // Entity admins: forced to their own entity (-1 if their claim is missing).
        private int? EffectiveEntityId(int? requested)
        {
            if (IsGlobalAdmin())
                return (requested.HasValue && requested.Value > 0) ? requested : (int?)null;
            return GetEntityClaimId() ?? -1;
        }

        // The entity id to carry on redirects so an entity admin's scope always persists.
        private int? RedirectEntityId(int? requested) => IsGlobalAdmin() ? requested : GetEntityClaimId();

        private int ResolveYear(int? year) => year ?? HttpContext.Session.GetInt("ctxYear") ?? DateTime.Now.Year;

        private List<SelectListItem> YearOptions(int selected)
        {
            var thisYear = DateTime.Now.Year;
            return new[] { thisYear - 1, thisYear, thisYear + 1, thisYear + 2 }
                .Select(y => new SelectListItem(y.ToString(), y.ToString(), y == selected))
                .ToList();
        }

        private async Task<List<SelectListItem>> EntityOptions(int? selected, bool includeAll)
        {
            // Entity admins are locked to their own entity - no "All entities" and no other options.
            if (!IsGlobalAdmin())
            {
                var myId = GetEntityClaimId();
                return await _db.Entities.AsNoTracking()
                    .Where(e => myId.HasValue && e.EntityId == myId.Value)
                    .OrderBy(e => e.EntityCode)
                    .Select(e => new SelectListItem(e.EntityCode + " - " + e.EntityName, e.EntityId.ToString(), true))
                    .ToListAsync();
            }

            var list = new List<SelectListItem>();
            if (includeAll) list.Add(new SelectListItem("All entities", "", !selected.HasValue));
            list.AddRange(await _db.Entities.AsNoTracking().OrderBy(e => e.EntityCode)
                .Select(e => new SelectListItem(e.EntityCode + " - " + e.EntityName, e.EntityId.ToString(), selected.HasValue && e.EntityId == selected.Value))
                .ToListAsync());
            return list;
        }

        // ---------------- Index: rules + program classification ----------------

        [HttpGet]
        public async Task<IActionResult> Index(int? year = null, int? entityId = null)
        {
            var selectedYear = ResolveYear(year);
            var scope = EffectiveEntityId(entityId);       // -1 = entity admin with no claim
            int? eff = (scope.HasValue && scope.Value > 0) ? scope : (int?)null; // null = all entities (global)
            var noAccess = scope.HasValue && scope.Value < 0;

            var programs = noAccess ? new List<Programs>() : await _db.Programs.AsNoTracking()
                .Where(p => !eff.HasValue || p.EntityId == eff.Value)
                .OrderBy(p => p.EntityId).ThenBy(p => p.ProgramCode)
                .ToListAsync();
            var progMap = programs.ToDictionary(p => p.ProgramId, p => p.ProgramCode + " - " + p.ProgramName);
            var entityMap = await _db.Entities.AsNoTracking().ToDictionaryAsync(e => e.EntityId, e => e.EntityCode);

            var rules = noAccess ? new List<AllocationRules>() : await _db.AllocationRules.AsNoTracking()
                .Where(r => r.BudgetYear == selectedYear && (!eff.HasValue || r.EntityId == eff.Value))
                .OrderBy(r => r.Sequence).ToListAsync();
            var drivers = await _db.AllocationDrivers.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.DriverName).ToListAsync();

            var latestRun = noAccess ? null : await _db.AllocationRuns.AsNoTracking()
                .Where(r => r.BudgetYear == selectedYear && r.Status == "Posted"
                    && (!eff.HasValue || r.EntityId == eff.Value))
                .OrderByDescending(r => r.RunAt).FirstOrDefaultAsync();

            var vm = new AllocationIndexVm
            {
                Year = selectedYear,
                EntityId = eff,
                YearOptions = YearOptions(selectedYear),
                EntityOptions = await EntityOptions(eff, includeAll: true),
                Programs = programs,
                EntityMap = entityMap,
                Rules = rules,
                ProgramMap = progMap,
                Drivers = drivers,
                LatestRun = latestRun,
                SupportPrograms = programs.Where(p => string.Equals(p.ProgramType, "Support", StringComparison.OrdinalIgnoreCase)).ToList(),
                MandatePrograms = programs.Where(p => !string.Equals(p.ProgramType, "Support", StringComparison.OrdinalIgnoreCase)).ToList()
            };
            return View(vm);
        }

        // ---- Program classification (Mandate/Support + step-down order) ----
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClassifyProgram(int programId, string programType, int? allocationSequence, int year, int? entityId)
        {
            var p = await _db.Programs.FirstOrDefaultAsync(x => x.ProgramId == programId);
            if (p != null)
            {
                if (!IsGlobalAdmin() && p.EntityId != (GetEntityClaimId() ?? -1)) return Forbid();
                p.ProgramType = string.Equals(programType, "Support", StringComparison.OrdinalIgnoreCase) ? "Support" : "Mandate";
                p.AllocationSequence = p.ProgramType == "Support" ? allocationSequence : null;
                await _db.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index), new { year, entityId = RedirectEntityId(entityId) });
        }

        // ---- Allocation rule CRUD ----
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RuleSave(int ruleId, int year, int? entityIdFilter, int ruleEntityId,
            int sourceProgramId, string method, int? driverId, string categoryScopeCsv, string targetScope,
            decimal sourcePercent, int sequence, bool isActive,
            List<int>? targetProgramIds, List<decimal>? targetWeights)
        {
            // Entity admins can only create/edit rules for their own entity.
            if (!IsGlobalAdmin())
            {
                var myId = GetEntityClaimId() ?? -1;
                ruleEntityId = myId;
                var srcEntity = await _db.Programs.AsNoTracking()
                    .Where(p => p.ProgramId == sourceProgramId).Select(p => (int?)p.EntityId).FirstOrDefaultAsync();
                if (srcEntity != myId) return Forbid();
            }

            AllocationRules rule;
            if (ruleId > 0)
            {
                rule = await _db.AllocationRules.Include(r => r.Targets).FirstOrDefaultAsync(r => r.RuleId == ruleId)
                       ?? new AllocationRules();
                if (!IsGlobalAdmin() && rule.RuleId > 0 && rule.EntityId != (GetEntityClaimId() ?? -1)) return Forbid();
            }
            else
            {
                rule = new AllocationRules { CreatedAt = DateTime.UtcNow, CreatedBy = User.Identity?.Name };
                _db.AllocationRules.Add(rule);
            }

            rule.BudgetYear = year;
            rule.EntityId = ruleEntityId;
            rule.SourceProgramId = sourceProgramId;
            rule.Method = NormalizeMethod(method);
            rule.DriverId = (rule.Method == "Driver" || rule.Method == "Headcount") ? driverId : null;
            rule.CategoryScopeCsv = string.IsNullOrWhiteSpace(categoryScopeCsv) ? "OPEX,HR" : categoryScopeCsv.ToUpperInvariant();
            rule.TargetScope = string.Equals(targetScope, "Explicit", StringComparison.OrdinalIgnoreCase) ? "Explicit" : "AllMandate";
            rule.SourcePercent = sourcePercent <= 0 ? 100m : Math.Min(sourcePercent, 100m);
            rule.Sequence = sequence;
            rule.IsActive = isActive;

            // Replace explicit targets
            if (rule.RuleId > 0)
            {
                var existing = await _db.AllocationRuleTargets.Where(t => t.RuleId == rule.RuleId).ToListAsync();
                _db.AllocationRuleTargets.RemoveRange(existing);
            }
            await _db.SaveChangesAsync();

            if (rule.TargetScope == "Explicit" && targetProgramIds != null)
            {
                for (var i = 0; i < targetProgramIds.Count; i++)
                {
                    if (targetProgramIds[i] <= 0) continue;
                    var weight = (targetWeights != null && i < targetWeights.Count) ? targetWeights[i] : 0m;
                    _db.AllocationRuleTargets.Add(new AllocationRuleTargets
                    {
                        RuleId = rule.RuleId,
                        TargetProgramId = targetProgramIds[i],
                        Weight = weight
                    });
                }
                await _db.SaveChangesAsync();
            }

            return RedirectToAction(nameof(Index), new { year, entityId = RedirectEntityId(entityIdFilter) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RuleDelete(int ruleId, int year, int? entityId)
        {
            var rule = await _db.AllocationRules.FirstOrDefaultAsync(r => r.RuleId == ruleId);
            if (rule != null)
            {
                if (!IsGlobalAdmin() && rule.EntityId != (GetEntityClaimId() ?? -1)) return Forbid();
                _db.AllocationRules.Remove(rule);
                await _db.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index), new { year, entityId = RedirectEntityId(entityId) });
        }

        // ---------------- Driver values ----------------
        [HttpGet]
        public async Task<IActionResult> Drivers(int? year = null, int? entityId = null, int? driverId = null)
        {
            var selectedYear = ResolveYear(year);
            var scope = EffectiveEntityId(entityId);
            int? eff = (scope.HasValue && scope.Value > 0) ? scope : (int?)null;
            var noAccess = scope.HasValue && scope.Value < 0;

            var drivers = await _db.AllocationDrivers.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.DriverName).ToListAsync();
            var selectedDriver = driverId ?? drivers.FirstOrDefault()?.DriverId;

            var programs = noAccess ? new List<Programs>() : await _db.Programs.AsNoTracking()
                .Where(p => p.IsActive && (!eff.HasValue || p.EntityId == eff.Value))
                .OrderBy(p => p.EntityId).ThenBy(p => p.ProgramCode).ToListAsync();

            var values = new Dictionary<int, decimal>();
            if (selectedDriver.HasValue && !noAccess)
            {
                var progIds = programs.Select(p => p.ProgramId).ToList();
                values = await _db.AllocationDriverValues.AsNoTracking()
                    .Where(v => v.BudgetYear == selectedYear && v.DriverId == selectedDriver.Value && v.TargetActivityId == null
                        && (!eff.HasValue || progIds.Contains(v.TargetProgramId)))
                    .ToDictionaryAsync(v => v.TargetProgramId, v => v.Value);
            }

            var vm = new AllocationDriversVm
            {
                Year = selectedYear,
                EntityId = eff,
                DriverId = selectedDriver,
                YearOptions = YearOptions(selectedYear),
                EntityOptions = await EntityOptions(eff, includeAll: true),
                Drivers = drivers,
                Programs = programs,
                Values = values
            };
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DriverValueSave(int year, int driverId, int? entityId,
            List<int> programIds, List<decimal> programValues)
        {
            if (programIds != null)
            {
                // Entity admins may only set driver values for programs in their own entity.
                HashSet<int>? allowedProgramIds = null;
                if (!IsGlobalAdmin())
                {
                    var myId = GetEntityClaimId() ?? -1;
                    allowedProgramIds = (await _db.Programs.AsNoTracking()
                        .Where(p => p.EntityId == myId).Select(p => p.ProgramId).ToListAsync()).ToHashSet();
                }

                var existing = await _db.AllocationDriverValues
                    .Where(v => v.BudgetYear == year && v.DriverId == driverId && v.TargetActivityId == null)
                    .ToDictionaryAsync(v => v.TargetProgramId);

                for (var i = 0; i < programIds.Count; i++)
                {
                    var pid = programIds[i];
                    if (allowedProgramIds != null && !allowedProgramIds.Contains(pid)) continue; // skip out-of-scope
                    var val = (programValues != null && i < programValues.Count) ? programValues[i] : 0m;
                    if (existing.TryGetValue(pid, out var row))
                    {
                        row.Value = val;
                    }
                    else if (val != 0m)
                    {
                        _db.AllocationDriverValues.Add(new AllocationDriverValues
                        {
                            BudgetYear = year,
                            DriverId = driverId,
                            TargetProgramId = pid,
                            Value = val
                        });
                    }
                }
                await _db.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Drivers), new { year, entityId = RedirectEntityId(entityId), driverId });
        }

        // ---------------- Runs / audit ----------------
        [HttpGet]
        public async Task<IActionResult> Runs(int? year = null, int? entityId = null, int? runId = null)
        {
            var selectedYear = ResolveYear(year);
            var scope = EffectiveEntityId(entityId);
            int? eff = (scope.HasValue && scope.Value > 0) ? scope : (int?)null;
            var noAccess = scope.HasValue && scope.Value < 0;

            // Entity admins only see runs scoped to their own entity (not global/all-entity runs).
            var runs = noAccess ? new List<AllocationRuns>() : await _db.AllocationRuns.AsNoTracking()
                .Where(r => r.BudgetYear == selectedYear && (!eff.HasValue || r.EntityId == eff.Value))
                .OrderByDescending(r => r.RunAt).Take(500).ToListAsync();

            var selected = runId ?? runs.FirstOrDefault(r => r.Status == "Posted")?.RunId ?? runs.FirstOrDefault()?.RunId;
            var txns = new List<AllocationTransactions>();
            // Only load transactions for a run the caller is actually allowed to see.
            // Superseded (and Draft) runs keep all their transactions, so their full
            // detail remains viewable here even after a newer run supersedes them.
            if (selected.HasValue && runs.Any(r => r.RunId == selected.Value))
            {
                txns = await _db.AllocationTransactions.AsNoTracking()
                    .Where(t => t.RunId == selected.Value).ToListAsync();
            }
            var progMap = await _db.Programs.AsNoTracking().ToDictionaryAsync(p => p.ProgramId, p => p.ProgramCode + " - " + p.ProgramName);
            var entityMap = await _db.Entities.AsNoTracking().ToDictionaryAsync(e => e.EntityId, e => e.EntityCode + " - " + e.EntityName);

            // Per-run rollups (transaction count + total allocated) across the whole history list.
            var runIds = runs.Select(r => r.RunId).ToList();
            var rollups = runIds.Count == 0
                ? new List<RunRollup>()
                : await _db.AllocationTransactions.AsNoTracking()
                    .Where(t => runIds.Contains(t.RunId))
                    .GroupBy(t => t.RunId)
                    .Select(g => new RunRollup { RunId = g.Key, Count = g.Count(), Total = g.Sum(x => x.Amount) })
                    .ToListAsync();

            var vm = new AllocationRunsVm
            {
                Year = selectedYear,
                EntityId = eff,
                YearOptions = YearOptions(selectedYear),
                Runs = runs,
                SelectedRunId = selected,
                Transactions = txns,
                ProgramMap = progMap,
                EntityMap = entityMap,
                RunTxnCounts = rollups.ToDictionary(x => x.RunId, x => x.Count),
                RunTotals = rollups.ToDictionary(x => x.RunId, x => x.Total),
                ShowEntityColumn = !eff.HasValue // global admins may see runs from multiple entities
            };
            return View(vm);
        }

        // ---- Execute the step-down allocation ----
        // scenarioName: optional management label for the run ("Headcount basis").
        // scenarioOnly: post the run as a comparison Scenario, leaving the official (Posted) run
        //               in place, so the standard reports keep using the official allocation.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Run(int year, int? entityId, string? scenarioName = null, bool scenarioOnly = false)
        {
            // Entity admins can only run the allocation for their own entity.
            if (!IsGlobalAdmin())
            {
                var myId = GetEntityClaimId();
                if (!myId.HasValue) return Forbid();
                entityId = myId.Value;
            }
            var result = await RunAllocation(year, entityId, scenarioName, scenarioOnly);
            TempData["AllocMsg"] = result;
            return RedirectToAction(nameof(Runs), new { year, entityId });
        }

        private static string NormalizeMethod(string? m)
        {
            var v = (m ?? "Equal").Trim();
            if (string.Equals(v, "Percentage", StringComparison.OrdinalIgnoreCase)) return "Percentage";
            if (string.Equals(v, "Headcount", StringComparison.OrdinalIgnoreCase)) return "Headcount";
            if (string.Equals(v, "Driver", StringComparison.OrdinalIgnoreCase)) return "Driver";
            return "Equal";
        }

        // =====================================================================
        // Step-down allocation engine
        // =====================================================================
        private async Task<string> RunAllocation(int year, int? entityId, string? scenarioName = null, bool scenarioOnly = false)
        {
            var label = string.IsNullOrWhiteSpace(scenarioName) ? null : scenarioName.Trim();
            if (label != null && label.Length > 120) label = label.Substring(0, 120);

            var programs = await _db.Programs.AsNoTracking()
                .Where(p => !entityId.HasValue || p.EntityId == entityId.Value)
                .ToListAsync();
            var progById = programs.ToDictionary(p => p.ProgramId);

            var supports = programs
                .Where(p => string.Equals(p.ProgramType, "Support", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.AllocationSequence ?? int.MaxValue).ThenBy(p => p.ProgramId)
                .ToList();
            if (supports.Count == 0) return "No Support programs are defined - nothing to allocate.";

            var rules = await _db.AllocationRules.AsNoTracking()
                .Where(r => r.BudgetYear == year && r.IsActive && (!entityId.HasValue || r.EntityId == entityId.Value))
                .OrderBy(r => r.Sequence).ToListAsync();
            // Force-sweep mode does not require rules: a support program with no rule is split
            // equally across all mandate programs in its entity (see TargetsFor below).

            var ruleTargets = await _db.AllocationRuleTargets.AsNoTracking()
                .Where(t => rules.Select(r => r.RuleId).Contains(t.RuleId)).ToListAsync();
            var targetsByRule = ruleTargets.GroupBy(t => t.RuleId).ToDictionary(g => g.Key, g => g.ToList());

            var driverValues = await _db.AllocationDriverValues.AsNoTracking()
                .Where(v => v.BudgetYear == year && v.TargetActivityId == null).ToListAsync();

            // Working balances: (programId, categoryUpper) -> amount (direct cost, expense only)
            var balances = await DirectCostByProgramCategory(year, entityId);

            // Create the run header first to obtain RunId.
            var run = new AllocationRuns
            {
                BudgetYear = year,
                EntityId = entityId,
                Period = "Annual",
                Status = "Draft",
                ScenarioName = label,
                Method = "StepDown",
                RunAt = DateTime.UtcNow,
                RunBy = User.Identity?.Name
            };
            _db.AllocationRuns.Add(run);
            await _db.SaveChangesAsync();

            // Mandate programs are the only valid destinations: 100% of support cost must land here.
            var mandateIds = programs
                .Where(p => p.IsActive && !string.Equals(p.ProgramType, "Support", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.ProgramId).ToHashSet();

            // Target distribution for a support program (FORCE-SWEEP mode):
            //   - Use the support program's primary active rule (lowest Sequence) for the split
            //     method/weights (Explicit / Driver / Headcount / Equal), but restrict destinations
            //     to MANDATE programs only.
            //   - If the program has no rule (or the rule resolves to no mandate target), fall back
            //     to an equal split across all mandate programs in the same entity.
            // Category scope and SourcePercent are intentionally ignored here: management requires
            // 100% of every support-program cost category (incl. CAPEX) to be allocated.
            List<TargetBasis> TargetsFor(Programs sp)
            {
                var primary = rules.Where(r => r.SourceProgramId == sp.ProgramId)
                    .OrderBy(r => r.Sequence).ThenBy(r => r.RuleId).FirstOrDefault();
                List<TargetBasis> targets = primary != null
                    ? ResolveTargets(primary, programs, targetsByRule, driverValues, year)
                        .Where(t => mandateIds.Contains(t.ProgramId) && t.ProgramId != sp.ProgramId).ToList()
                    : new List<TargetBasis>();
                if (targets.Count == 0)
                {
                    targets = programs
                        .Where(p => p.EntityId == sp.EntityId && mandateIds.Contains(p.ProgramId))
                        .Select(p => new TargetBasis { ProgramId = p.ProgramId, Basis = 0m })
                        .ToList();
                }
                return targets;
            }

            var txns = new List<AllocationTransactions>();
            decimal totalIn = 0m, totalOut = 0m;
            var skipped = 0;

            // Because every destination is a mandate program (never a support program), no support
            // program can receive reallocated cost, so a single pass in step-down order fully
            // clears every support program's balance. Sweep 100% of each category.
            foreach (var sp in supports.OrderBy(p => p.AllocationSequence ?? int.MaxValue).ThenBy(p => p.ProgramId))
            {
                var targets = TargetsFor(sp);
                var catKeys = balances.Keys.Where(k => k.Item1 == sp.ProgramId && balances[k] > 0m).ToList();
                if (catKeys.Count == 0) continue;
                if (targets.Count == 0) { skipped++; continue; }

                var basisTotal = targets.Sum(t => t.Basis);
                var useEqual = basisTotal <= 0m; // fallback to equal split when no basis data

                foreach (var key in catKeys)
                {
                    var cat = key.Item2;
                    var pool = balances[key]; // FORCE-SWEEP: allocate the full remaining balance (100%)
                    if (pool <= 0m) continue;

                    decimal running = 0m;
                    for (var i = 0; i < targets.Count; i++)
                    {
                        var t = targets[i];
                        var weight = useEqual ? (1m / targets.Count) : (t.Basis / basisTotal);
                        var amt = (i == targets.Count - 1) ? (pool - running) : Math.Round(pool * weight, 2);
                        running += amt;
                        if (amt == 0m) continue;

                        txns.Add(new AllocationTransactions
                        {
                            RunId = run.RunId,
                            BudgetYear = year,
                            Period = "Annual",
                            EntityId = sp.EntityId,
                            SourceProgramId = sp.ProgramId,
                            SourceCategoryCode = cat,
                            TargetProgramId = t.ProgramId,
                            DriverId = null,
                            BasisValue = useEqual ? 1m : t.Basis,
                            BasisTotal = useEqual ? targets.Count : basisTotal,
                            AllocationPct = weight,
                            Amount = amt
                        });

                        var tkey = (t.ProgramId, cat);
                        balances[tkey] = balances.GetValueOrDefault(tkey) + amt;
                        totalIn += amt;
                    }
                    balances[key] = 0m; // fully allocated
                    totalOut += pool;
                }
            }

            // Reconciliation: confirm no support program has any residual (unallocated) balance.
            var residual = balances
                .Where(kv => supports.Any(s => s.ProgramId == kv.Key.Item1) && kv.Value > 0.01m)
                .Sum(kv => kv.Value);

            _db.AllocationTransactions.AddRange(txns);
            run.ReconciledOk = Math.Abs(totalIn - totalOut) < 0.01m && residual < 0.01m;
            run.Notes = $"Force-sweep: posted {txns.Count} transactions. In={totalIn:N2} Out={totalOut:N2}. "
                + $"Support residual (unallocated)={residual:N2}. "
                + (skipped > 0 ? skipped + " support program(s) skipped (no mandate target available)." : "");

            if (scenarioOnly)
            {
                // A comparison scenario: kept alongside the official run, which stays Posted so
                // every standard report is unaffected.
                run.Status = "Scenario";
            }
            else
            {
                // Supersede prior posted runs for the same scope (Scenario runs are left alone).
                var priorPosted = await _db.AllocationRuns
                    .Where(r => r.RunId != run.RunId && r.BudgetYear == year && r.Status == "Posted"
                        && ((r.EntityId == null && entityId == null) || r.EntityId == entityId))
                    .ToListAsync();
                foreach (var p in priorPosted) p.Status = "Superseded";

                run.Status = "Posted";
            }
            await _db.SaveChangesAsync();

            var what = scenarioOnly ? "Scenario saved" : "Allocation posted";
            var named = label == null ? "" : $" \"{label}\"";
            return $"{what}{named}: {txns.Count} transaction(s), {(run.ReconciledOk ? "reconciled OK" : "RECONCILIATION MISMATCH")}. In={totalIn:N2} Out={totalOut:N2}.";
        }

        private struct TargetBasis { public int ProgramId; public decimal Basis; }

        private List<TargetBasis> ResolveTargets(AllocationRules rule, List<Programs> programs,
            Dictionary<int, List<AllocationRuleTargets>> targetsByRule, List<AllocationDriverValues> driverValues, int year)
        {
            // Determine candidate target programs.
            List<int> candidateIds;
            Dictionary<int, decimal> explicitWeights = new();
            if (rule.TargetScope == "Explicit" && targetsByRule.TryGetValue(rule.RuleId, out var ts) && ts.Count > 0)
            {
                candidateIds = ts.Select(t => t.TargetProgramId).Distinct().ToList();
                explicitWeights = ts.GroupBy(t => t.TargetProgramId).ToDictionary(g => g.Key, g => g.Sum(x => x.Weight));
            }
            else
            {
                candidateIds = programs
                    .Where(p => p.ProgramId != rule.SourceProgramId
                        && !string.Equals(p.ProgramType, "Support", StringComparison.OrdinalIgnoreCase)
                        && p.IsActive && p.EntityId == rule.EntityId)
                    .Select(p => p.ProgramId).ToList();
            }
            if (candidateIds.Count == 0) return new List<TargetBasis>();

            var result = new List<TargetBasis>();
            switch (rule.Method)
            {
                case "Percentage":
                    foreach (var id in candidateIds)
                        result.Add(new TargetBasis { ProgramId = id, Basis = explicitWeights.GetValueOrDefault(id) });
                    // If no explicit weights provided, basis sum is 0 -> engine falls back to equal.
                    break;
                case "Headcount":
                case "Driver":
                    var did = rule.DriverId;
                    foreach (var id in candidateIds)
                    {
                        var v = driverValues.FirstOrDefault(dv => dv.DriverId == did && dv.TargetProgramId == id)?.Value ?? 0m;
                        result.Add(new TargetBasis { ProgramId = id, Basis = v });
                    }
                    break;
                default: // Equal
                    foreach (var id in candidateIds)
                        result.Add(new TargetBasis { ProgramId = id, Basis = 0m }); // basis 0 -> equal fallback
                    break;
            }
            return result;
        }

        // Direct expense cost per (programId, categoryUpper).
        // HR is taken EXCLUSIVELY from HrEmployeeCostAllocations (the activity-allocated grain),
        // NEVER from imported HrEmployeeCosts and NEVER from any HR-categorised budget line.
        // This mirrors the AllocatedOnly ledger used by the Builder's Total cost basis and
        // guarantees the imported (GL-mapped) HR total and the allocated HR total are not summed
        // together (they represent the same money at two different grains).
        private async Task<Dictionary<(int, string), decimal>> DirectCostByProgramCategory(int year, int? entityId)
        {
            var dict = new Dictionary<(int, string), decimal>();

            var blQuery =
                from b in _db.BudgetLines.AsNoTracking()
                join cat in _db.Categories.AsNoTracking() on b.CategoryId equals cat.CategoryId
                join act in _db.Activities.AsNoTracking() on b.ActivityId equals act.ActivityId into actJoin
                from act in actJoin.DefaultIfEmpty()
                where b.BudgetYear == year
                    && (!entityId.HasValue || b.EntityId == entityId.Value)
                select new
                {
                    LineProgramId = b.ProgramId,
                    ActProgramId = (int?)(act != null ? act.ProgramId : (int?)null),
                    Category = cat.CategoryCode,
                    b.Amount
                };
            var bl = await blQuery.ToListAsync();
            foreach (var r in bl)
            {
                var programId = r.LineProgramId ?? r.ActProgramId ?? 0;
                if (programId <= 0) continue;
                var cat = (r.Category ?? "").ToUpperInvariant();
                // Skip REVENUE (not a cost) and HR (HR is added below from allocations only,
                // to avoid double-counting with the activity-allocated HR).
                if (cat == "REVENUE" || cat == "HR") continue;
                var key = (programId, cat);
                dict[key] = dict.GetValueOrDefault(key) + r.Amount;
            }

            var hrQuery =
                from a in _db.HrEmployeeCostAllocations.AsNoTracking()
                join act in _db.Activities.AsNoTracking() on a.ActivityId equals act.ActivityId
                join emp in _db.HrEmployeeCosts.AsNoTracking() on a.EmployeeCostId equals emp.EmployeeCostId
                where emp.BudgetYear == year
                    && (!entityId.HasValue || emp.EntityId == entityId.Value)
                select new { act.ProgramId, a.AllocatedAmount };
            var hr = await hrQuery.ToListAsync();
            foreach (var r in hr)
            {
                if (r.ProgramId <= 0) continue;
                var key = (r.ProgramId, "HR");
                dict[key] = dict.GetValueOrDefault(key) + r.AllocatedAmount;
            }

            return dict;
        }
    }

    // ---------------- View models ----------------
    public class AllocationIndexVm
    {
        public int Year { get; set; }
        public int? EntityId { get; set; }
        public List<SelectListItem> YearOptions { get; set; } = new();
        public List<SelectListItem> EntityOptions { get; set; } = new();
        public List<Programs> Programs { get; set; } = new();
        public Dictionary<int, string> EntityMap { get; set; } = new();
        public List<AllocationRules> Rules { get; set; } = new();
        public Dictionary<int, string> ProgramMap { get; set; } = new();
        public List<AllocationDrivers> Drivers { get; set; } = new();
        public AllocationRuns? LatestRun { get; set; }
        public List<Programs> SupportPrograms { get; set; } = new();
        public List<Programs> MandatePrograms { get; set; } = new();
    }

    public class AllocationDriversVm
    {
        public int Year { get; set; }
        public int? EntityId { get; set; }
        public int? DriverId { get; set; }
        public List<SelectListItem> YearOptions { get; set; } = new();
        public List<SelectListItem> EntityOptions { get; set; } = new();
        public List<AllocationDrivers> Drivers { get; set; } = new();
        public List<Programs> Programs { get; set; } = new();
        public Dictionary<int, decimal> Values { get; set; } = new();
    }

    public class AllocationRunsVm
    {
        public int Year { get; set; }
        public int? EntityId { get; set; }
        public List<SelectListItem> YearOptions { get; set; } = new();
        public List<AllocationRuns> Runs { get; set; } = new();
        public int? SelectedRunId { get; set; }
        public List<AllocationTransactions> Transactions { get; set; } = new();
        public Dictionary<int, string> ProgramMap { get; set; } = new();
        public Dictionary<int, string> EntityMap { get; set; } = new();
        // Per-run rollups for the history list (RunId -> value).
        public Dictionary<int, int> RunTxnCounts { get; set; } = new();
        public Dictionary<int, decimal> RunTotals { get; set; } = new();
        public bool ShowEntityColumn { get; set; }
    }

    public class RunRollup
    {
        public int RunId { get; set; }
        public int Count { get; set; }
        public decimal Total { get; set; }
    }
}
