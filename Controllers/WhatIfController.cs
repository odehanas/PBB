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
    [Authorize]
    public class WhatIfController : Controller
    {
        private readonly GovBudgetContext _db;

        public WhatIfController(GovBudgetContext db)
        {
            _db = db;
        }

        public class ProjectRateRowVm
        {
            public int ProjectId { get; set; }
            public string ProjectCode { get; set; } = "";
            public string ProjectName { get; set; } = "";
            public decimal? CostInflationRate { get; set; }
            public decimal? RevenueGrowthRate { get; set; }
        }

        public class BudgetLineImpactVm
        {
            public long BudgetLineId { get; set; }
            public string CategoryCode { get; set; } = "";
            public string ItemCode { get; set; } = "";
            public string ItemName { get; set; } = "";
            public string Description { get; set; } = "";
            public string GLCode { get; set; } = "";
            public string GLName { get; set; } = "";
            public string GLType { get; set; } = "";
            public string? ProjectCode { get; set; }
            public string? ProjectName { get; set; }
            public int? ProjectId { get; set; }
            public decimal Quantity { get; set; }
            public decimal UnitPrice { get; set; }
            public decimal BaseAmount { get; set; }
            public decimal ScenarioUnitPrice { get; set; }
            public decimal ScenarioAmount { get; set; }
            public decimal DeltaAmount { get; set; }
        }

        private class ActiveScenarioVm
        {
            public WhatIfScenarios Scenario { get; set; } = null!;
            public decimal CostInflationRate { get; set; }
            public decimal RevenueGrowthRate { get; set; }
            public Dictionary<int, WhatIfScenarioProjectRates> ProjectRates { get; set; } = new();
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var scope = GetScope();
            if (!scope.HasValue)
            {
                await PopulateContextPicker();
                ViewBag.RequiresContext = true;
                ViewBag.ReturnUrl = Url.Action(nameof(Index), "WhatIf") ?? "/WhatIf";
                return View(new WhatIfScenarios
                {
                    BudgetYear = DateTime.Now.Year,
                    ScenarioName = "",
                    IsActive = true
                });
            }

            var (year, entityId, deptId) = scope.Value;

            var scenarios = await _db.WhatIfScenarios.AsNoTracking()
                .Where(s => s.BudgetYear == year && s.EntityId == entityId && s.DepartmentId == deptId && s.IsActive)
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync();

            var activeScenarioId = HttpContext.Session.GetInt("ctxScenarioId");
            var activeScenario = activeScenarioId.HasValue && activeScenarioId.Value > 0
                ? scenarios.FirstOrDefault(s => s.ScenarioId == activeScenarioId.Value)
                : null;

            ActiveScenarioVm? activeScenarioVm = null;
            if (activeScenarioId.HasValue && activeScenarioId.Value > 0)
            {
                activeScenarioVm = await LoadActiveScenario(activeScenarioId.Value, year, entityId, deptId);
                if (activeScenarioVm != null)
                {
                    activeScenario = activeScenarioVm.Scenario;
                }
            }

            ViewBag.ContextYear = year;
            ViewBag.ContextEntityId = entityId;
            ViewBag.ContextDeptId = deptId;
            ViewBag.ActiveScenario = activeScenario;
            ViewBag.Scenarios = scenarios;

            if (activeScenarioVm != null)
            {
                var impacts = await BuildScenarioImpacts(year, entityId, deptId, activeScenarioVm);
                ViewBag.ScenarioImpacts = impacts;
                ViewBag.ScenarioBaseTotal = impacts.Sum(i => i.BaseAmount);
                ViewBag.ScenarioAmountTotal = impacts.Sum(i => i.ScenarioAmount);
                ViewBag.ScenarioDeltaTotal = impacts.Sum(i => i.DeltaAmount);
            }

            var model = new WhatIfScenarios
            {
                BudgetYear = year,
                EntityId = entityId,
                DepartmentId = deptId,
                ScenarioName = "",
                IsActive = true
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(string scenarioName, decimal costInflationRate, decimal revenueGrowthRate)
        {
            var scope = GetScope();
            if (!scope.HasValue) return RedirectToAction("Select", "Context", new { returnUrl = $"{Request.Path}{Request.QueryString}" });

            var (year, entityId, deptId) = scope.Value;
            var userName = User.Identity?.Name ?? "Unknown";

            if (string.IsNullOrWhiteSpace(scenarioName))
            {
                TempData["Error"] = "Scenario name is required.";
                return RedirectToAction(nameof(Index));
            }

            var scenario = new WhatIfScenarios
            {
                BudgetYear = year,
                EntityId = entityId,
                DepartmentId = deptId,
                ScenarioName = scenarioName.Trim(),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = userName
            };

            _db.WhatIfScenarios.Add(scenario);
            await _db.SaveChangesAsync();

            _db.WhatIfScenarioDefaults.Add(new WhatIfScenarioDefaults
            {
                ScenarioId = scenario.ScenarioId,
                CostInflationRate = costInflationRate,
                RevenueGrowthRate = revenueGrowthRate
            });

            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = userName,
                Action = "INSERT",
                EntityName = "WhatIfScenarios",
                RecordId = scenario.ScenarioId.ToString(),
                Timestamp = DateTime.UtcNow,
                Details = $"Created what-if scenario '{scenario.ScenarioName}'."
            });

            await _db.SaveChangesAsync();

            HttpContext.Session.SetInt("ctxScenarioId", scenario.ScenarioId);
            TempData["Success"] = "Scenario created and activated.";
            return RedirectToAction(nameof(Edit), new { id = scenario.ScenarioId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Activate(int id)
        {
            var scope = GetScope();
            if (!scope.HasValue) return RedirectToAction("Select", "Context", new { returnUrl = $"{Request.Path}{Request.QueryString}" });

            var (year, entityId, deptId) = scope.Value;

            var scenario = await _db.WhatIfScenarios.AsNoTracking()
                .FirstOrDefaultAsync(s => s.ScenarioId == id
                                          && s.BudgetYear == year
                                          && s.EntityId == entityId
                                          && s.DepartmentId == deptId
                                          && s.IsActive);
            if (scenario == null)
            {
                TempData["Error"] = "Scenario not found for the selected context.";
                return RedirectToAction(nameof(Index));
            }

            HttpContext.Session.SetInt("ctxScenarioId", scenario.ScenarioId);
            TempData["Success"] = $"Scenario activated: {scenario.ScenarioName}";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Clear()
        {
            HttpContext.Session.SetInt("ctxScenarioId", 0);
            TempData["Success"] = "Scenario cleared.";
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var scope = GetScope();
            if (!scope.HasValue) return RedirectToAction("Select", "Context", new { returnUrl = $"{Request.Path}{Request.QueryString}" });

            var (year, entityId, deptId) = scope.Value;

            var scenario = await _db.WhatIfScenarios
                .Include(s => s.WhatIfScenarioDefaults)
                .FirstOrDefaultAsync(s => s.ScenarioId == id
                                          && s.BudgetYear == year
                                          && s.EntityId == entityId
                                          && s.DepartmentId == deptId
                                          && s.IsActive);
            if (scenario == null) return NotFound();

            var rateMap = await _db.WhatIfScenarioProjectRates.AsNoTracking()
                .Where(r => r.ScenarioId == scenario.ScenarioId)
                .ToDictionaryAsync(r => r.ProjectId, r => r);

            var projects = await _db.Projects.AsNoTracking()
                .Where(p => p.IsActive && (p.OwningDepartmentId == null || p.OwningDepartmentId == deptId))
                .OrderBy(p => p.ProjectCode)
                .Select(p => new { p.ProjectId, p.ProjectCode, p.ProjectName })
                .ToListAsync();

            var rows = new List<ProjectRateRowVm>(projects.Count);
            foreach (var p in projects)
            {
                rateMap.TryGetValue(p.ProjectId, out var r);
                rows.Add(new ProjectRateRowVm
                {
                    ProjectId = p.ProjectId,
                    ProjectCode = p.ProjectCode,
                    ProjectName = p.ProjectName,
                    CostInflationRate = r?.CostInflationRate,
                    RevenueGrowthRate = r?.RevenueGrowthRate
                });
            }

            ViewBag.ProjectRows = rows;
            return View(scenario);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, string scenarioName, decimal costInflationRate, decimal revenueGrowthRate, int[] projectId, decimal?[] projectCostInflationRate, decimal?[] projectRevenueGrowthRate)
        {
            var scope = GetScope();
            if (!scope.HasValue) return RedirectToAction("Select", "Context", new { returnUrl = $"{Request.Path}{Request.QueryString}" });

            var (year, entityId, deptId) = scope.Value;

            var scenario = await _db.WhatIfScenarios
                .Include(s => s.WhatIfScenarioDefaults)
                .FirstOrDefaultAsync(s => s.ScenarioId == id
                                          && s.BudgetYear == year
                                          && s.EntityId == entityId
                                          && s.DepartmentId == deptId
                                          && s.IsActive);
            if (scenario == null) return NotFound();

            if (string.IsNullOrWhiteSpace(scenarioName))
            {
                TempData["Error"] = "Scenario name is required.";
                return RedirectToAction(nameof(Edit), new { id });
            }

            var userName = User.Identity?.Name ?? "Unknown";

            scenario.ScenarioName = scenarioName.Trim();
            scenario.UpdatedAt = DateTime.UtcNow;
            scenario.UpdatedBy = userName;

            if (scenario.WhatIfScenarioDefaults == null)
            {
                scenario.WhatIfScenarioDefaults = new WhatIfScenarioDefaults
                {
                    ScenarioId = scenario.ScenarioId,
                    CostInflationRate = costInflationRate,
                    RevenueGrowthRate = revenueGrowthRate
                };
            }
            else
            {
                scenario.WhatIfScenarioDefaults.CostInflationRate = costInflationRate;
                scenario.WhatIfScenarioDefaults.RevenueGrowthRate = revenueGrowthRate;
            }

            var existingRates = await _db.WhatIfScenarioProjectRates
                .Where(r => r.ScenarioId == scenario.ScenarioId)
                .ToListAsync();
            _db.WhatIfScenarioProjectRates.RemoveRange(existingRates);

            for (var i = 0; i < projectId.Length; i++)
            {
                var pid = projectId[i];
                var cr = i < projectCostInflationRate.Length ? projectCostInflationRate[i] : null;
                var rr = i < projectRevenueGrowthRate.Length ? projectRevenueGrowthRate[i] : null;

                if (cr.HasValue || rr.HasValue)
                {
                    _db.WhatIfScenarioProjectRates.Add(new WhatIfScenarioProjectRates
                    {
                        ScenarioId = scenario.ScenarioId,
                        ProjectId = pid,
                        CostInflationRate = cr,
                        RevenueGrowthRate = rr
                    });
                }
            }

            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = userName,
                Action = "UPDATE",
                EntityName = "WhatIfScenarios",
                RecordId = scenario.ScenarioId.ToString(),
                Timestamp = DateTime.UtcNow,
                Details = $"Updated what-if scenario '{scenario.ScenarioName}'."
            });

            await _db.SaveChangesAsync();

            TempData["Success"] = "Scenario updated.";
            return RedirectToAction(nameof(Edit), new { id = scenario.ScenarioId });
        }

        private (int Year, int EntityId, int DeptId)? GetScope()
        {
            var year = HttpContext.Session.GetInt("ctxYear") ?? DateTime.Now.Year;
            var entityId = HttpContext.Session.GetInt("ctxEntityId");
            var deptId = HttpContext.Session.GetInt("ctxDeptId");
            if (!(entityId.HasValue && deptId.HasValue)) return null;
            return (year, entityId.Value, deptId.Value);
        }

        private async Task PopulateContextPicker()
        {
            var thisYear = DateTime.Now.Year;
            var years = new[] { thisYear - 1, thisYear, thisYear + 1, thisYear + 2 }
                .Select(y => new { Id = y, Name = y.ToString() })
                .ToList();
            ViewBag.BudgetYear = new SelectList(years, "Id", "Name", thisYear);

            var entityClaim = User.Claims.FirstOrDefault(c => c.Type == "EntityId")?.Value;
            var isSysAdmin = User.IsInRole("SYSADMIN");
            int? userEntityId = null;
            if (int.TryParse(entityClaim, out var e)) userEntityId = e;

            List<Entities> entities;
            if (!isSysAdmin)
            {
                if (userEntityId.HasValue)
                {
                    entities = await _db.Entities
                        .Include(x => x.Departments)
                        .Where(x => x.IsActive && x.EntityId == userEntityId.Value)
                        .OrderBy(x => x.EntityCode)
                        .ToListAsync();
                }
                else
                {
                    entities = new List<Entities>();
                }
            }
            else
            {
                entities = await _db.Entities
                    .Include(x => x.Departments)
                    .Where(x => x.IsActive)
                    .OrderBy(x => x.EntityCode)
                    .ToListAsync();
            }

            foreach (var ent in entities)
            {
                ent.Departments = ent.Departments
                    .Where(d => d.IsActive)
                    .OrderBy(d => d.DeptCode)
                    .ToList();
            }

            ViewBag.ContextEntities = entities;
        }

        private async Task<ActiveScenarioVm?> LoadActiveScenario(int scenarioId, int year, int entityId, int deptId)
        {
            var scenario = await _db.WhatIfScenarios.AsNoTracking()
                .Include(s => s.WhatIfScenarioDefaults)
                .FirstOrDefaultAsync(s => s.ScenarioId == scenarioId
                                          && s.BudgetYear == year
                                          && s.EntityId == entityId
                                          && s.DepartmentId == deptId
                                          && s.IsActive);
            if (scenario == null) return null;

            var defaults = scenario.WhatIfScenarioDefaults ?? new WhatIfScenarioDefaults
            {
                ScenarioId = scenario.ScenarioId,
                CostInflationRate = 0m,
                RevenueGrowthRate = 0m
            };

            var projectRates = await _db.WhatIfScenarioProjectRates.AsNoTracking()
                .Where(r => r.ScenarioId == scenario.ScenarioId)
                .ToDictionaryAsync(r => r.ProjectId, r => r);

            return new ActiveScenarioVm
            {
                Scenario = scenario,
                CostInflationRate = defaults.CostInflationRate,
                RevenueGrowthRate = defaults.RevenueGrowthRate,
                ProjectRates = projectRates
            };
        }

        private async Task<List<BudgetLineImpactVm>> BuildScenarioImpacts(int year, int entityId, int deptId, ActiveScenarioVm scenario)
        {
            var lines = await (
                from b in _db.BudgetLines.AsNoTracking()
                join cat in _db.Categories.AsNoTracking() on b.CategoryId equals cat.CategoryId
                join item in _db.Items.AsNoTracking() on b.ItemId equals item.ItemId
                join gl in _db.GLAccounts.AsNoTracking() on item.GLAccountId equals gl.GLAccountId
                join proj in _db.Projects.AsNoTracking() on b.ProjectId equals proj.ProjectId into projJoin
                from proj in projJoin.DefaultIfEmpty()
                where b.BudgetYear == year
                      && b.EntityId == entityId
                      && b.DepartmentId == deptId
                orderby cat.CategoryCode, item.ItemCode, b.BudgetLineId
                select new
                {
                    b.BudgetLineId,
                    CategoryCode = cat.CategoryCode,
                    item.ItemCode,
                    item.ItemName,
                    b.Description,
                    gl.GLCode,
                    gl.GLName,
                    gl.GLType,
                    b.ProjectId,
                    ProjectCode = proj != null ? proj.ProjectCode : null,
                    ProjectName = proj != null ? proj.ProjectName : null,
                    b.Quantity,
                    b.UnitPrice,
                    b.Amount
                }
            ).ToListAsync();

            var results = new List<BudgetLineImpactVm>(lines.Count);
            foreach (var r in lines)
            {
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
                else if (string.Equals(r.CategoryCode, "REVENUE", StringComparison.OrdinalIgnoreCase))
                {
                    rateToApply = revRate;
                }
                else
                {
                    rateToApply = costRate;
                }

                var multiplier = 1m + (rateToApply / 100m);
                var scenarioUnitPrice = Math.Round(r.UnitPrice * multiplier, 2, MidpointRounding.AwayFromZero);
                var scenarioAmount = Math.Round(r.Quantity * scenarioUnitPrice, 2, MidpointRounding.AwayFromZero);

                results.Add(new BudgetLineImpactVm
                {
                    BudgetLineId = r.BudgetLineId,
                    CategoryCode = r.CategoryCode,
                    ItemCode = r.ItemCode,
                    ItemName = r.ItemName,
                    Description = r.Description,
                    GLCode = r.GLCode,
                    GLName = r.GLName,
                    GLType = r.GLType,
                    ProjectId = r.ProjectId,
                    ProjectCode = r.ProjectCode,
                    ProjectName = r.ProjectName,
                    Quantity = r.Quantity,
                    UnitPrice = r.UnitPrice,
                    BaseAmount = r.Amount,
                    ScenarioUnitPrice = scenarioUnitPrice,
                    ScenarioAmount = scenarioAmount,
                    DeltaAmount = scenarioAmount - r.Amount
                });
            }

            return results;
        }
    }
}
