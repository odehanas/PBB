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
    public class ContextController : Controller
    {
        private readonly GovBudgetContext _db;
        public ContextController(GovBudgetContext db) { _db = db; }

        // The year picker shown straight after sign-in. It offers the same four years as the
        // dashboard filter, and each card shows the dashboard's own totals for that year so
        // the user can see where the data is before going in.
        [HttpGet]
        public async Task<IActionResult> Year(string? returnUrl = null)
        {
            int thisYear = System.DateTime.Now.Year;
            int firstYear = thisYear - 1, lastYear = thisYear + 2;

            var scopeEntityId = int.TryParse(User.FindFirst("EntityId")?.Value, out var claimEntity) && claimEntity > 0
                ? claimEntity
                : (int?)null;
            var isGlobalAdmin = User.IsInRole("SYSADMIN") || (User.IsInRole("ADMIN") && !scopeEntityId.HasValue);

            var vm = new YearPickerVm
            {
                ReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : null
            };

            var budget = new List<(int Year, string Code, decimal Total, int Lines)>();
            var hr = new List<(int Year, decimal Total, int Headcount)>();

            // Same scoping as the dashboard: an entity user sees their entity, a global admin
            // sees everything, and an account with no entity sees nothing.
            if (isGlobalAdmin || scopeEntityId.HasValue)
            {
                var budgetRows = await (from b in _db.BudgetLines.AsNoTracking()
                                        join cat in _db.Categories.AsNoTracking() on b.CategoryId equals cat.CategoryId
                                        where b.BudgetYear >= firstYear && b.BudgetYear <= lastYear
                                              && (isGlobalAdmin || b.EntityId == scopeEntityId)
                                        group b by new { b.BudgetYear, cat.CategoryCode } into g
                                        select new { g.Key.BudgetYear, g.Key.CategoryCode, Total = g.Sum(x => x.Amount), Lines = g.Count() })
                    .ToListAsync();
                budget = budgetRows.Select(r => (r.BudgetYear, r.CategoryCode, r.Total, r.Lines)).ToList();

                var hrRows = await _db.HrEmployeeCosts
                    .AsNoTracking()
                    .Where(x => x.BudgetYear >= firstYear && x.BudgetYear <= lastYear && x.EntityId != null
                                && (isGlobalAdmin || x.EntityId == scopeEntityId))
                    .GroupBy(x => x.BudgetYear)
                    .Select(g => new { Year = g.Key, Total = g.Sum(x => x.AnnualCost), Headcount = g.Count() })
                    .ToListAsync();
                hr = hrRows.Select(r => (r.Year, r.Total, r.Headcount)).ToList();
            }

            var currentSelection = HttpContext.Session.GetInt("ctxYear");

            for (var y = firstYear; y <= lastYear; y++)
            {
                var year = y;
                var lines = budget.Where(b => b.Year == year).ToList();
                var hrRow = hr.FirstOrDefault(h => h.Year == year);

                decimal Category(string code) => lines
                    .Where(b => string.Equals(b.Code, code, System.StringComparison.OrdinalIgnoreCase))
                    .Sum(b => b.Total);

                vm.Choices.Add(new YearChoiceVm
                {
                    Year = year,
                    Label = (year - thisYear) switch
                    {
                        -1 => "Last year",
                        0 => "This year",
                        1 => "Next year",
                        _ => "Planning ahead"
                    },
                    IsCurrentSelection = currentSelection == year,
                    Revenue = Category("REVENUE"),
                    Expenditure = Category("OPEX") + Category("CAPEX") + hrRow.Total,
                    Records = lines.Sum(b => b.Lines) + hrRow.Headcount
                });
            }

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Year(int year, string? returnUrl)
        {
            int thisYear = System.DateTime.Now.Year;
            if (year < thisYear - 1 || year > thisYear + 2)
            {
                return RedirectToAction(nameof(Year), new { returnUrl });
            }

            HttpContext.Session.SetInt("ctxYear", year);

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction("Index", "Home");
        }

        [HttpGet]
        public async Task<IActionResult> Select(string? returnUrl = null)
        {
            int thisYear = System.DateTime.Now.Year;
            var years = new[] { thisYear - 1, thisYear, thisYear + 1, thisYear + 2 }
                        .Select(y => new { Id = y, Name = y.ToString() }).ToList();
            // Start on the year already chosen, so saving a cost center cannot quietly switch
            // the user back to the calendar year.
            ViewBag.BudgetYear = new SelectList(years, "Id", "Name", HttpContext.Session.GetInt("ctxYear") ?? thisYear);
            ViewBag.ReturnUrl = returnUrl;

            var user = User;
            var isSysAdmin = user.IsInRole("SYSADMIN");
            var isEntityAdmin = user.IsInRole("ADMIN");
            var isAdminLike = isSysAdmin || isEntityAdmin;
            var role = isAdminLike ? (isSysAdmin ? "SYSADMIN" : "ADMIN") : "USER";
            int? userEntityId = null;
            var entityClaim = user.Claims.FirstOrDefault(c => c.Type == "EntityId")?.Value;
            if (int.TryParse(entityClaim, out var e)) userEntityId = e;
            int? userDeptId = null;
            var deptClaim = user.Claims.FirstOrDefault(c => c.Type == "DepartmentId")?.Value;
            if (int.TryParse(deptClaim, out var d)) userDeptId = d;

            List<Entities> model;

            if (!isAdminLike && userEntityId.HasValue)
            {
                model = await _db.Entities
                    .Include(e => e.Departments)
                    .Where(e => e.IsActive && e.EntityId == userEntityId.Value)
                    .OrderBy(e => e.EntityCode)
                    .ToListAsync();
            }
            else if (!isAdminLike)
            {
                model = new List<Entities>();
            }
            else
            {
                var q = _db.Entities
                    .Include(ent => ent.Departments)
                    .Where(ent => ent.IsActive)
                    .AsQueryable();

                if (userEntityId.HasValue && userEntityId.Value > 0)
                {
                    q = q.Where(ent => ent.EntityId == userEntityId.Value);
                }

                model = await q
                    .OrderBy(ent => ent.EntityCode)
                    .ToListAsync();
            }

            foreach (var ent in model)
            {
                var deps = ent.Departments.Where(dep => dep.IsActive);
                if (userDeptId.HasValue && userDeptId.Value > 0)
                {
                    deps = deps.Where(dep => dep.DepartmentId == userDeptId.Value);
                }
                ent.Departments = deps.OrderBy(dep => dep.DeptCode).ToList();
            }

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Select(int BudgetYear, int EntityId, int DepartmentId, string? returnUrl = null)
        {
            var entityClaim = User.Claims.FirstOrDefault(c => c.Type == "EntityId")?.Value;
            var deptClaim = User.Claims.FirstOrDefault(c => c.Type == "DepartmentId")?.Value;
            var hasAllowedEntity = int.TryParse(entityClaim, out var allowedEntityId) && allowedEntityId > 0;
            var hasAllowedDept = int.TryParse(deptClaim, out var allowedDeptId) && allowedDeptId > 0;
            var isAdminLike = User.IsInRole("ADMIN") || User.IsInRole("SYSADMIN");

            if (!isAdminLike && !hasAllowedEntity)
            {
                return Forbid();
            }

            if (hasAllowedEntity)
            {
                if (EntityId != allowedEntityId)
                {
                    return Forbid();
                }

                var deptBelongs = _db.Departments
                    .AsNoTracking()
                    .Any(d => d.DepartmentId == DepartmentId && d.EntityId == allowedEntityId && d.IsActive);
                if (!deptBelongs)
                {
                    return Forbid();
                }
            }

            if (hasAllowedDept && DepartmentId != allowedDeptId)
            {
                return Forbid();
            }

            HttpContext.Session.SetInt("ctxYear", BudgetYear);
            HttpContext.Session.SetInt("ctxEntityId", EntityId);
            HttpContext.Session.SetInt("ctxDeptId", DepartmentId);
            TempData["Success"] = "Budget context saved.";

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction("Entry", "BudgetLines", new { category = "REVENUE" });
        }
    }

    public class YearPickerVm
    {
        public string? ReturnUrl { get; set; }
        public List<YearChoiceVm> Choices { get; set; } = new();
        public bool AnyData => Choices.Any(c => c.HasData);
    }

    public class YearChoiceVm
    {
        public int Year { get; set; }
        public string Label { get; set; } = "";
        public bool IsCurrentSelection { get; set; }
        public decimal Revenue { get; set; }
        public decimal Expenditure { get; set; }
        public int Records { get; set; }
        public bool HasData => Records > 0;
    }
}
