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
using System.Diagnostics;

namespace GovBudget.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        private readonly GovBudgetContext _db;
        private readonly ILogger<HomeController> _logger;

        public HomeController(GovBudgetContext db, ILogger<HomeController> logger)
        {
            _db = db;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Index(int? entityId = null, int? year = null)
        {
            var thisYear = DateTime.Now.Year;
            var selectedYear = year ?? HttpContext.Session.GetInt("ctxYear") ?? thisYear;

            var isAdmin = User.IsInRole("ADMIN");
            var isSysAdmin = User.IsInRole("SYSADMIN");
            var isAdminLike = isAdmin || isSysAdmin;
            var isGlobalAdmin = IsGlobalAdmin();
            var scopeEntityId = GetEntityClaimId();

            int? selectedEntityId = null;
            if (isAdminLike)
            {
                if (!isGlobalAdmin)
                {
                    if (!scopeEntityId.HasValue || scopeEntityId.Value <= 0)
                    {
                        var emptyVm = new HomeExecutiveSummaryVm
                        {
                            IsAdmin = true,
                            CanAccessAdminRoom = true,
                            Year = selectedYear,
                            YearOptions = new[] { thisYear - 1, thisYear, thisYear + 1, thisYear + 2 }
                                .Select(y => new SelectListItem(y.ToString(), y.ToString(), y == selectedYear))
                                .ToList(),
                            DonutEntityId = null,
                            EntityOptions = new List<SelectListItem>(),
                            Overall = new OverallSummaryVm(),
                            Donut = new DonutSummaryVm { EntityLabel = "All Departments -- combined" },
                            Entities = new List<EntitySummaryRowVm>()
                        };
                        return View(emptyVm);
                    }

                    selectedEntityId = scopeEntityId.Value;
                }
                else if (entityId.HasValue && entityId.Value > 0)
                {
                    selectedEntityId = entityId.Value;
                }
            }
            else
            {
                if (!scopeEntityId.HasValue || scopeEntityId.Value <= 0)
                {
                    var emptyVm = new HomeExecutiveSummaryVm
                    {
                        IsAdmin = false,
                        CanAccessAdminRoom = false,
                        Year = selectedYear,
                        YearOptions = new[] { thisYear - 1, thisYear, thisYear + 1, thisYear + 2 }
                            .Select(y => new SelectListItem(y.ToString(), y.ToString(), y == selectedYear))
                            .ToList(),
                        DonutEntityId = null,
                        EntityOptions = new List<SelectListItem>(),
                        Overall = new OverallSummaryVm(),
                        Donut = new DonutSummaryVm { EntityLabel = "All Departments -- combined" },
                        Entities = new List<EntitySummaryRowVm>()
                    };
                    return View(emptyVm);
                }

                selectedEntityId = scopeEntityId.Value;
            }

            var effectiveEntityId = selectedEntityId;

            var entitiesQuery = _db.Entities
                .AsNoTracking()
                .OrderBy(e => e.EntityCode)
                .Select(e => new { e.EntityId, e.EntityCode, e.EntityName });

            if (effectiveEntityId.HasValue)
            {
                entitiesQuery = entitiesQuery.Where(e => e.EntityId == effectiveEntityId.Value);
            }

            var entities = await entitiesQuery.ToListAsync();

            var budgetAgg = await (from b in _db.BudgetLines.AsNoTracking()
                                   join cat in _db.Categories.AsNoTracking() on b.CategoryId equals cat.CategoryId
                                   where b.BudgetYear == selectedYear
                                   select new { b.EntityId, cat.CategoryCode, b.Amount })
                .GroupBy(x => new { x.EntityId, x.CategoryCode })
                .Select(g => new { g.Key.EntityId, g.Key.CategoryCode, Total = g.Sum(x => x.Amount) })
                .ToListAsync();

            var hrAgg = await _db.HrEmployeeCosts
                .AsNoTracking()
                .Where(x => x.BudgetYear == selectedYear)
                .GroupBy(x => x.EntityId ?? 0)
                .Select(g => new { EntityId = g.Key, Total = g.Sum(x => x.AnnualCost) })
                .ToListAsync();

            var headcountAgg = await _db.HrEmployeeCosts
                .AsNoTracking()
                .Where(x => x.BudgetYear == selectedYear)
                .GroupBy(x => x.EntityId ?? 0)
                .Select(g => new { EntityId = g.Key, Headcount = g.Count() })
                .ToListAsync();

            if (effectiveEntityId.HasValue)
            {
                budgetAgg = budgetAgg.Where(x => x.EntityId == effectiveEntityId.Value).ToList();
                hrAgg = hrAgg.Where(x => x.EntityId == effectiveEntityId.Value).ToList();
                headcountAgg = headcountAgg.Where(x => x.EntityId == effectiveEntityId.Value).ToList();
            }

            var budgetMap = budgetAgg
                .GroupBy(x => x.EntityId)
                .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.CategoryCode, x => x.Total));

            var hrMap = hrAgg.ToDictionary(x => x.EntityId, x => x.Total);
            var headcountMap = headcountAgg.ToDictionary(x => x.EntityId, x => x.Headcount);

            var rows = new List<EntitySummaryRowVm>();
            foreach (var e in entities)
            {
                budgetMap.TryGetValue(e.EntityId, out var catTotals);
                catTotals ??= new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

                var revenue = catTotals.TryGetValue("REVENUE", out var r) ? r : 0m;
                var capex = catTotals.TryGetValue("CAPEX", out var c) ? c : 0m;
                var opex = catTotals.TryGetValue("OPEX", out var o) ? o : 0m;
                var hr = hrMap.TryGetValue(e.EntityId, out var h) ? h : 0m;
                var headcount = headcountMap.TryGetValue(e.EntityId, out var hc) ? hc : 0;

                rows.Add(new EntitySummaryRowVm
                {
                    EntityId = e.EntityId,
                    EntityCode = e.EntityCode ?? "",
                    EntityName = e.EntityName ?? "",
                    Revenue = revenue,
                    Hr = hr,
                    Opex = opex,
                    Capex = capex,
                    Headcount = headcount
                });
            }

            rows = rows
                .OrderByDescending(x => x.Revenue)
                .ThenBy(x => x.EntityCode)
                .ToList();

            var overallRevenue = rows.Sum(x => x.Revenue);
            var overallHr = rows.Sum(x => x.Hr);
            var overallOpex = rows.Sum(x => x.Opex);
            var overallCapex = rows.Sum(x => x.Capex);
            var overallHeadcount = rows.Sum(x => x.Headcount);

            var entityOptions = new List<SelectListItem>();
            if (isGlobalAdmin)
            {
                entityOptions.Add(new SelectListItem("All Departments -- combined", "", !effectiveEntityId.HasValue));
            }

            var allEntitiesQuery = _db.Entities
                .AsNoTracking()
                .OrderBy(e => e.EntityCode)
                .Select(e => new { e.EntityId, e.EntityCode, e.EntityName })
                .AsQueryable();

            if (!isGlobalAdmin && effectiveEntityId.HasValue)
            {
                allEntitiesQuery = allEntitiesQuery.Where(e => e.EntityId == effectiveEntityId.Value);
            }

            var allEntities = await allEntitiesQuery.ToListAsync();

            foreach (var e in allEntities)
            {
                var text = string.IsNullOrWhiteSpace(e.EntityCode) ? e.EntityName : (e.EntityCode + " - " + e.EntityName);
                entityOptions.Add(new SelectListItem(text, e.EntityId.ToString(), effectiveEntityId.HasValue && e.EntityId == effectiveEntityId.Value));
            }

            var years = new[] { thisYear - 1, thisYear, thisYear + 1, thisYear + 2 }
                .Select(y => new SelectListItem(y.ToString(), y.ToString(), y == selectedYear))
                .ToList();

            var projects = await BuildProjectSummary(selectedYear, effectiveEntityId);

            var canAccessAdminRoom = User.IsInRole("ADMIN") || User.IsInRole("SYSADMIN");
            var vm = new HomeExecutiveSummaryVm
            {
                IsAdmin = isAdminLike,
                CanAccessAdminRoom = canAccessAdminRoom,
                Year = selectedYear,
                YearOptions = years,
                DonutEntityId = effectiveEntityId,
                EntityOptions = entityOptions,
                Overall = new OverallSummaryVm
                {
                    Revenue = overallRevenue,
                    Hr = overallHr,
                    Opex = overallOpex,
                    Capex = overallCapex,
                    Headcount = overallHeadcount
                },
                Donut = effectiveEntityId.HasValue && rows.Count > 0
                    ? new DonutSummaryVm
                    {
                        EntityLabel = string.IsNullOrWhiteSpace(rows[0].EntityCode) ? rows[0].EntityName : (rows[0].EntityCode + " - " + rows[0].EntityName),
                        Hr = rows[0].Hr,
                        Opex = rows[0].Opex,
                        Capex = rows[0].Capex,
                        Headcount = rows[0].Headcount
                    }
                    : new DonutSummaryVm
                    {
                        EntityLabel = "All Departments -- combined",
                        Hr = overallHr,
                        Opex = overallOpex,
                        Capex = overallCapex,
                        Headcount = overallHeadcount
                    },
                Entities = rows,
                Projects = projects
            };

            return View(vm);
        }

        // Projects are a second way of looking at money that is already in the Revenue, HR, OPEX
        // and CAPEX totals: a budget line or an HR allocation may carry a project. This lists that
        // part per project. It is a breakdown of the totals above, never an addition to them.
        private async Task<List<ProjectSummaryRowVm>> BuildProjectSummary(int year, int? entityId)
        {
            var lineQuery =
                from b in _db.BudgetLines.AsNoTracking()
                join cat in _db.Categories.AsNoTracking() on b.CategoryId equals cat.CategoryId
                where b.BudgetYear == year && b.ProjectId != null
                select new { b.EntityId, ProjectId = b.ProjectId!.Value, cat.CategoryCode, b.Amount };
            if (entityId.HasValue) lineQuery = lineQuery.Where(x => x.EntityId == entityId.Value);
            var lineAgg = await lineQuery
                .GroupBy(x => new { x.ProjectId, x.CategoryCode })
                .Select(g => new { g.Key.ProjectId, g.Key.CategoryCode, Total = g.Sum(x => x.Amount) })
                .ToListAsync();

            // HR reaches a project only through an employee cost allocation that names one.
            var hrQuery =
                from a in _db.HrEmployeeCostAllocations.AsNoTracking()
                join emp in _db.HrEmployeeCosts.AsNoTracking() on a.EmployeeCostId equals emp.EmployeeCostId
                where emp.BudgetYear == year && a.ProjectId != null
                select new { emp.EntityId, ProjectId = a.ProjectId!.Value, a.EmployeeCostId, a.AllocatedAmount };
            if (entityId.HasValue) hrQuery = hrQuery.Where(x => x.EntityId == entityId.Value);
            var hrRows = await hrQuery.ToListAsync();
            var hrAgg = hrRows.GroupBy(x => x.ProjectId)
                .ToDictionary(g => g.Key, g => (Total: g.Sum(x => x.AllocatedAmount), Staff: g.Select(x => x.EmployeeCostId).Distinct().Count()));

            var projectIds = lineAgg.Select(x => x.ProjectId).Concat(hrAgg.Keys).Distinct().ToList();
            if (projectIds.Count == 0) return new List<ProjectSummaryRowVm>();

            var meta = await _db.Projects.AsNoTracking()
                .Where(p => projectIds.Contains(p.ProjectId))
                .Select(p => new { p.ProjectId, p.ProjectCode, p.ProjectName })
                .ToDictionaryAsync(p => p.ProjectId);

            decimal Line(int projectId, string category) => lineAgg
                .Where(x => x.ProjectId == projectId && string.Equals(x.CategoryCode, category, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Total);

            return projectIds
                .Select(id =>
                {
                    meta.TryGetValue(id, out var m);
                    hrAgg.TryGetValue(id, out var hr);
                    return new ProjectSummaryRowVm
                    {
                        ProjectId = id,
                        ProjectCode = m?.ProjectCode ?? "",
                        ProjectName = m?.ProjectName ?? "(Project " + id + ")",
                        Revenue = Line(id, "REVENUE"),
                        Hr = hr.Total,
                        Staff = hr.Staff,
                        Opex = Line(id, "OPEX"),
                        Capex = Line(id, "CAPEX")
                    };
                })
                .OrderByDescending(x => x.TotalExpense)
                .ThenBy(x => x.ProjectCode)
                .ToList();
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        [AllowAnonymous]
        public IActionResult Error()
        {
            var vm = new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier };

            var feature = HttpContext.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
            var ex = feature?.Error;

            if (ex != null)
            {
                // Always log the full error server-side.
                _logger?.LogError(ex, "Unhandled exception for {Path} (RequestId {RequestId})", feature?.Path, vm.RequestId);

                // Only reveal details to admins so we can diagnose the deployed site safely.
                var isAdmin = User?.Identity?.IsAuthenticated == true &&
                              (User.IsInRole("ADMIN") || User.IsInRole("SYSADMIN"));
                if (isAdmin)
                {
                    var root = ex;
                    while (root.InnerException != null) root = root.InnerException;

                    vm.Path = feature?.Path;
                    vm.ExceptionType = root.GetType().FullName;
                    vm.Message = root.Message;
                    vm.StackTrace = root.StackTrace;
                }
            }

            return View(vm);
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

        private bool IsGlobalAdmin()
        {
            var scopedEntityId = GetEntityClaimId();
            return User.IsInRole("SYSADMIN") || (User.IsInRole("ADMIN") && !scopedEntityId.HasValue);
        }
    }

    public class HomeExecutiveSummaryVm
    {
        public bool IsAdmin { get; set; }
        public bool CanAccessAdminRoom { get; set; }
        public int Year { get; set; }
        public List<SelectListItem> YearOptions { get; set; } = new();
        public int? DonutEntityId { get; set; }
        public List<SelectListItem> EntityOptions { get; set; } = new();
        public OverallSummaryVm Overall { get; set; } = new();
        public DonutSummaryVm Donut { get; set; } = new();
        public List<EntitySummaryRowVm> Entities { get; set; } = new();
        public List<ProjectSummaryRowVm> Projects { get; set; } = new();
    }

    // One project's share of the budget: revenue and cost tagged to it, by category.
    public class ProjectSummaryRowVm
    {
        public int ProjectId { get; set; }
        public string ProjectCode { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public decimal Revenue { get; set; }
        public decimal Hr { get; set; }
        public int Staff { get; set; }
        public decimal Opex { get; set; }
        public decimal Capex { get; set; }
        public decimal TotalExpense => Hr + Opex + Capex;
        public decimal Net => Revenue - TotalExpense;
    }

    public class OverallSummaryVm
    {
        public decimal Revenue { get; set; }
        public decimal Hr { get; set; }
        public decimal Opex { get; set; }
        public decimal Capex { get; set; }
        public int Headcount { get; set; }
        public decimal TotalExpense => Hr + Opex + Capex;
        public decimal Net => Revenue - TotalExpense;
    }

    public class DonutSummaryVm
    {
        public string EntityLabel { get; set; } = "";
        public decimal Hr { get; set; }
        public decimal Opex { get; set; }
        public decimal Capex { get; set; }
        public int Headcount { get; set; }
        public decimal TotalExpense => Hr + Opex + Capex;
    }

    public class EntitySummaryRowVm
    {
        public int EntityId { get; set; }
        public string EntityCode { get; set; } = "";
        public string EntityName { get; set; } = "";
        public decimal Revenue { get; set; }
        public decimal Hr { get; set; }
        public decimal Opex { get; set; }
        public decimal Capex { get; set; }
        public int Headcount { get; set; }
        public decimal TotalExpense => Hr + Opex + Capex;
        public decimal Net => Revenue - TotalExpense;
    }
}
