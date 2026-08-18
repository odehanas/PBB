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

        [HttpGet]
        public async Task<IActionResult> Select(string? returnUrl = null)
        {
            int thisYear = System.DateTime.Now.Year;
            var years = new[] { thisYear - 1, thisYear, thisYear + 1, thisYear + 2 }
                        .Select(y => new { Id = y, Name = y.ToString() }).ToList();
            ViewBag.BudgetYear = new SelectList(years, "Id", "Name", thisYear);
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
}
