using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    [Authorize]
    public class HrCostsController : Controller
    {
        private readonly GovBudgetContext _db;

        // Tolerance (in currency units) to avoid false "over annual cost" rejections
        // caused by rounding when allocation percentages sum to exactly 100%.
        private const decimal AllocationTolerance = 0.01m;

        public HrCostsController(GovBudgetContext db)
        {
            _db = db;
        }

        private int? GetAdminScopedEntityId()
        {
            var entityClaim = User.Claims.FirstOrDefault(c => c.Type == "EntityId")?.Value;
            if (!int.TryParse(entityClaim, out var entityId) || entityId <= 0)
            {
                return null;
            }

            return entityId;
        }

        // ---- entity-scope helpers (same model as ActualsController / PerformanceController) ----

        // A global admin may browse every entity; an entity-scoped admin never can.
        private bool IsGlobalAdmin()
        {
            if (User.IsInRole("SYSADMIN")) return true;
            if (!User.IsInRole("ADMIN")) return false;
            return !GetAdminScopedEntityId().HasValue;
        }

        // Global admin: honour the requested entity (null = all entities).
        // Entity admin: always forced to their own claim (-1 when they have none, i.e. no access).
        private int? EffectiveEntityId(int? requested)
        {
            if (IsGlobalAdmin())
            {
                return (requested.HasValue && requested.Value > 0) ? requested : (int?)null;
            }

            return GetAdminScopedEntityId() ?? -1;
        }

        // Dropdown of selectable entities. Only a global admin gets the "All entities" option.
        private async Task<List<SelectListItem>> EntityOptions(int? selected)
        {
            var q = _db.Entities.AsNoTracking().Where(e => e.IsActive).AsQueryable();

            var global = IsGlobalAdmin();
            if (!global)
            {
                var myId = GetAdminScopedEntityId();
                q = q.Where(e => myId.HasValue && e.EntityId == myId.Value);
            }

            var list = await q.OrderBy(e => e.EntityCode)
                .Select(e => new SelectListItem(
                    e.EntityCode + " - " + e.EntityName,
                    e.EntityId.ToString(),
                    selected.HasValue && e.EntityId == selected.Value))
                .ToListAsync();

            if (global)
            {
                list.Insert(0, new SelectListItem("All entities", "", !selected.HasValue));
            }

            return list;
        }

        [HttpGet]
        [Authorize(Policy = "AdminOnly")]
        public async Task<IActionResult> Index(int? year = null, string? q = null, int? entityId = null)
        {
            var thisYear = DateTime.Now.Year;
            var selectedYear = year ?? thisYear;
            ViewBag.SelectedYear = selectedYear;

            var years = new[] { thisYear - 1, thisYear, thisYear + 1, thisYear + 2 }
                .Select(y => new { Id = y, Name = y.ToString() }).ToList();
            ViewBag.BudgetYear = new SelectList(years, "Id", "Name", selectedYear);
            ViewBag.Query = q ?? "";
            ViewBag.IsBudget = false;
            ViewBag.EntityOptions = await EntityOptions(entityId);
            ViewBag.SelectedEntityId = entityId;
            ViewBag.IsGlobalAdmin = IsGlobalAdmin();

            var scope = EffectiveEntityId(entityId);

            // Entity admin with no entity assigned: show nothing rather than everything.
            if (scope.HasValue && scope.Value <= 0)
            {
                return View(new List<HrEmployeeCosts>());
            }

            var query = _db.HrEmployeeCosts
                .Include(x => x.HrEmployeeCostAllocations)
                    .ThenInclude(a => a.Activity)
                        .ThenInclude(a => a.Program)
                .Include(x => x.HrEmployeeCostAllocations)
                    .ThenInclude(a => a.Project)
                .AsNoTracking()
                .Where(x => x.BudgetYear == selectedYear);

            if (scope.HasValue)
            {
                query = query.Where(x => x.EntityId == scope.Value);
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(x =>
                    x.EmployeeId.Contains(term) ||
                    x.EmployeeName.Contains(term) ||
                    (x.Occupation != null && x.Occupation.Contains(term)) ||
                    x.EntityName.Contains(term) ||
                    x.DepartmentName.Contains(term));
            }

            var rows = await query
                .OrderBy(x => x.EntityName)
                .ThenBy(x => x.DepartmentName)
                .ThenBy(x => x.EmployeeName)
                .Take(2000)
                .ToListAsync();

            return View(rows);
        }

        [HttpGet]
        [Authorize(Policy = "AdminOnly")]
        public async Task<IActionResult> AllocationVariances(int? year = null, string? q = null, int? entityId = null)
        {
            var thisYear = DateTime.Now.Year;
            var selectedYear = year ?? thisYear;
            ViewBag.SelectedYear = selectedYear;

            var years = new[] { thisYear - 1, thisYear, thisYear + 1, thisYear + 2 }
                .Select(y => new { Id = y, Name = y.ToString() }).ToList();
            ViewBag.BudgetYear = new SelectList(years, "Id", "Name", selectedYear);
            ViewBag.Query = q ?? "";
            ViewBag.EntityOptions = await EntityOptions(entityId);
            ViewBag.SelectedEntityId = entityId;
            ViewBag.IsGlobalAdmin = IsGlobalAdmin();

            var scope = EffectiveEntityId(entityId);
            if (scope.HasValue && scope.Value <= 0)
            {
                return View(new List<AllocationVarianceRow>());
            }

            var query = _db.HrEmployeeCosts
                .Include(x => x.HrEmployeeCostAllocations)
                .AsNoTracking()
                .Where(x => x.BudgetYear == selectedYear);

            if (scope.HasValue)
            {
                query = query.Where(x => x.EntityId == scope.Value);
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(x =>
                    x.EmployeeId.Contains(term) ||
                    x.EmployeeName.Contains(term) ||
                    (x.Occupation != null && x.Occupation.Contains(term)) ||
                    x.EntityName.Contains(term) ||
                    x.DepartmentName.Contains(term));
            }

            var employees = await query.ToListAsync();

            var rows = employees
                .Select(x => new AllocationVarianceRow
                {
                    EmployeeCostId = x.EmployeeCostId,
                    EmployeeId = x.EmployeeId,
                    EmployeeName = x.EmployeeName,
                    EntityName = x.EntityName,
                    DepartmentName = x.DepartmentName,
                    AnnualCost = x.AnnualCost,
                    Allocated = x.HrEmployeeCostAllocations.Sum(a => a.AllocatedAmount),
                    AllocationCount = x.HrEmployeeCostAllocations.Count
                })
                .Where(r => Math.Abs(r.Variance) > AllocationTolerance)
                .OrderByDescending(r => Math.Abs(r.Variance))
                .ToList();

            return View(rows);
        }

        [HttpGet]
        public async Task<IActionResult> Budget(int? year = null, string? q = null)
        {
            var thisYear = DateTime.Now.Year;
            var selectedYear = year ?? HttpContext.Session.GetInt("ctxYear") ?? thisYear;
            ViewBag.SelectedYear = selectedYear;

            var years = new[] { thisYear - 1, thisYear, thisYear + 1, thisYear + 2 }
                .Select(y => new { Id = y, Name = y.ToString() }).ToList();
            ViewBag.BudgetYear = new SelectList(years, "Id", "Name", selectedYear);
            ViewBag.Query = q ?? "";
            ViewBag.IsBudget = true;

            var entityId = HttpContext.Session.GetInt("ctxEntityId");
            var deptId = HttpContext.Session.GetInt("ctxDeptId");
            if (!(entityId.HasValue && deptId.HasValue))
            {
                return RedirectToAction("Select", "Context");
            }

            var query = _db.HrEmployeeCosts
                .Include(x => x.HrEmployeeCostAllocations)
                    .ThenInclude(a => a.Activity)
                        .ThenInclude(a => a.Program)
                .Include(x => x.HrEmployeeCostAllocations)
                    .ThenInclude(a => a.Project)
                .AsNoTracking()
                .Where(x => x.BudgetYear == selectedYear && x.EntityId == entityId.Value && x.DepartmentId == deptId.Value);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(x =>
                    x.EmployeeId.Contains(term) ||
                    x.EmployeeName.Contains(term) ||
                    (x.Occupation != null && x.Occupation.Contains(term)) ||
                    x.EntityName.Contains(term) ||
                    x.DepartmentName.Contains(term));
            }

            var rows = await query
                .OrderBy(x => x.EntityName)
                .ThenBy(x => x.DepartmentName)
                .ThenBy(x => x.EmployeeName)
                .Take(2000)
                .ToListAsync();

            return View("Index", rows);
        }

        [HttpGet]
        [Authorize(Policy = "AdminOnly")]
        public IActionResult Import()
        {
            var adminEntityId = GetAdminScopedEntityId();
            var thisYear = DateTime.Now.Year;
            var years = new[] { thisYear - 1, thisYear, thisYear + 1, thisYear + 2 }
                .Select(y => new { Id = y, Name = y.ToString() }).ToList();
            ViewBag.BudgetYear = new SelectList(years, "Id", "Name", thisYear);
            ViewBag.IsBudget = false;
            return View();
        }

        [HttpGet]
        public IActionResult BudgetImport()
        {
            var entityId = HttpContext.Session.GetInt("ctxEntityId");
            var deptId = HttpContext.Session.GetInt("ctxDeptId");
            if (!(entityId.HasValue && deptId.HasValue))
            {
                return RedirectToAction("Select", "Context");
            }

            var thisYear = DateTime.Now.Year;
            var selectedYear = HttpContext.Session.GetInt("ctxYear") ?? thisYear;
            var years = new[] { thisYear - 1, thisYear, thisYear + 1, thisYear + 2 }
                .Select(y => new { Id = y, Name = y.ToString() }).ToList();
            ViewBag.BudgetYear = new SelectList(years, "Id", "Name", selectedYear);
            ViewBag.IsBudget = true;
            return View("Import");
        }

        [HttpGet]
        [Authorize(Policy = "AdminOnly")]
        public IActionResult Template()
        {
            var adminEntityId = GetAdminScopedEntityId();
            var bytes = BuildHrImportTemplateBytes();

            var fileName = "HR_Costs_Import_Template.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        [HttpGet]
        public IActionResult BudgetTemplate()
        {
            var bytes = BuildHrImportTemplateBytes();
            var fileName = "HR_Costs_Import_Template.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        [HttpGet]
        [Authorize(Policy = "AdminOnly")]
        public async Task<IActionResult> Export(int year, string? q = null, int? entityId = null)
        {
            var query = _db.HrEmployeeCosts
                .AsNoTracking()
                .Where(x => x.BudgetYear == year);

            var scope = EffectiveEntityId(entityId);
            if (scope.HasValue && scope.Value <= 0)
            {
                return Forbid();
            }

            if (scope.HasValue)
            {
                query = query.Where(x => x.EntityId == scope.Value);
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(x =>
                    x.EmployeeId.Contains(term) ||
                    x.EmployeeName.Contains(term) ||
                    (x.Occupation != null && x.Occupation.Contains(term)) ||
                    x.EntityName.Contains(term) ||
                    x.DepartmentName.Contains(term));
            }

            var rows = await query
                .OrderBy(x => x.EntityName)
                .ThenBy(x => x.DepartmentName)
                .ThenBy(x => x.EmployeeName)
                .ToListAsync();

            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("HR Costs");

            ws.Cell(1, 1).Value = "EmployeeID";
            ws.Cell(1, 2).Value = "Employee Name";
            ws.Cell(1, 3).Value = "Entity Name";
            ws.Cell(1, 4).Value = "Department Name";
            ws.Cell(1, 5).Value = "Annual Cost";
            ws.Cell(1, 6).Value = "GL Account Code";
            ws.Cell(1, 7).Value = "GL Kind";
            ws.Cell(1, 8).Value = "Occupation";

            ws.Range(1, 1, 1, 8).Style.Font.Bold = true;
            ws.Range(1, 1, 1, 8).Style.Fill.BackgroundColor = XLColor.LightGray;
            ws.Column(5).Style.NumberFormat.Format = "#,##0.00";

            var r = 2;
            foreach (var row in rows)
            {
                ws.Cell(r, 1).Value = row.EmployeeId;
                ws.Cell(r, 2).Value = row.EmployeeName;
                ws.Cell(r, 3).Value = row.EntityName;
                ws.Cell(r, 4).Value = row.DepartmentName;
                ws.Cell(r, 5).Value = row.AnnualCost;
                ws.Cell(r, 6).Value = row.GLCode;
                ws.Cell(r, 7).Value = row.GLKind;
                ws.Cell(r, 8).Value = row.Occupation ?? "";
                r++;
            }

            ws.Columns(1, 8).AdjustToContents();

            var ids = rows.Select(x => x.EmployeeCostId).ToList();
            var allocations = await (
                from a in _db.HrEmployeeCostAllocations.AsNoTracking()
                join e in _db.HrEmployeeCosts.AsNoTracking() on a.EmployeeCostId equals e.EmployeeCostId
                join act in _db.Activities.AsNoTracking() on a.ActivityId equals act.ActivityId
                join proj in _db.Projects.AsNoTracking() on a.ProjectId equals proj.ProjectId into projJoin
                from proj in projJoin.DefaultIfEmpty()
                where ids.Contains(a.EmployeeCostId)
                orderby e.EmployeeId, act.ActivityCode
                select new
                {
                    e.EmployeeId,
                    ActivityCode = act.ActivityCode,
                    ProjectCode = proj != null ? proj.ProjectCode : "",
                    a.AllocatedAmount,
                    e.AnnualCost
                }
            ).ToListAsync();

            var allocWs = wb.AddWorksheet("HR Allocations");
            allocWs.Cell(1, 1).Value = "EmployeeID";
            allocWs.Cell(1, 2).Value = "Activity Code";
            allocWs.Cell(1, 3).Value = "Project Code";
            allocWs.Cell(1, 4).Value = "Allocated Percent";
            allocWs.Cell(1, 5).Value = "Allocated Amount";

            allocWs.Range(1, 1, 1, 5).Style.Font.Bold = true;
            allocWs.Range(1, 1, 1, 5).Style.Fill.BackgroundColor = XLColor.LightGray;
            allocWs.Column(4).Style.NumberFormat.Format = "0.00";
            allocWs.Column(5).Style.NumberFormat.Format = "#,##0.00";

            var ar = 2;
            foreach (var a in allocations)
            {
                var pct = a.AnnualCost <= 0m ? 0m : Math.Round((a.AllocatedAmount / a.AnnualCost) * 100m, 2, MidpointRounding.AwayFromZero);
                allocWs.Cell(ar, 1).Value = a.EmployeeId;
                allocWs.Cell(ar, 2).Value = a.ActivityCode;
                allocWs.Cell(ar, 3).Value = a.ProjectCode;
                allocWs.Cell(ar, 4).Value = pct;
                allocWs.Cell(ar, 5).Value = a.AllocatedAmount;
                ar++;
            }

            allocWs.Columns(1, 5).AdjustToContents();

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            var bytes = ms.ToArray();

            var fileName = $"HR_Costs_{year}_Export.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        [HttpGet]
        public async Task<IActionResult> BudgetExport(int year, string? q = null)
        {
            var entityId = HttpContext.Session.GetInt("ctxEntityId");
            var deptId = HttpContext.Session.GetInt("ctxDeptId");
            if (!(entityId.HasValue && deptId.HasValue))
            {
                return RedirectToAction("Select", "Context");
            }

            var query = _db.HrEmployeeCosts
                .AsNoTracking()
                .Where(x => x.BudgetYear == year && x.EntityId == entityId.Value && x.DepartmentId == deptId.Value);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(x =>
                    x.EmployeeId.Contains(term) ||
                    x.EmployeeName.Contains(term) ||
                    (x.Occupation != null && x.Occupation.Contains(term)) ||
                    x.EntityName.Contains(term) ||
                    x.DepartmentName.Contains(term));
            }

            var rows = await query
                .OrderBy(x => x.EntityName)
                .ThenBy(x => x.DepartmentName)
                .ThenBy(x => x.EmployeeName)
                .ToListAsync();

            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("HR Costs");

            ws.Cell(1, 1).Value = "EmployeeID";
            ws.Cell(1, 2).Value = "Employee Name";
            ws.Cell(1, 3).Value = "Entity Name";
            ws.Cell(1, 4).Value = "Department Name";
            ws.Cell(1, 5).Value = "Annual Cost";
            ws.Cell(1, 6).Value = "GL Account Code";
            ws.Cell(1, 7).Value = "GL Kind";
            ws.Cell(1, 8).Value = "Occupation";

            ws.Range(1, 1, 1, 8).Style.Font.Bold = true;
            ws.Range(1, 1, 1, 8).Style.Fill.BackgroundColor = XLColor.LightGray;
            ws.Column(5).Style.NumberFormat.Format = "#,##0.00";

            var r = 2;
            foreach (var row in rows)
            {
                ws.Cell(r, 1).Value = row.EmployeeId;
                ws.Cell(r, 2).Value = row.EmployeeName;
                ws.Cell(r, 3).Value = row.EntityName;
                ws.Cell(r, 4).Value = row.DepartmentName;
                ws.Cell(r, 5).Value = row.AnnualCost;
                ws.Cell(r, 6).Value = row.GLCode;
                ws.Cell(r, 7).Value = row.GLKind;
                ws.Cell(r, 8).Value = row.Occupation ?? "";
                r++;
            }

            ws.Columns(1, 8).AdjustToContents();

            var ids = rows.Select(x => x.EmployeeCostId).ToList();
            var allocations = await (
                from a in _db.HrEmployeeCostAllocations.AsNoTracking()
                join e in _db.HrEmployeeCosts.AsNoTracking() on a.EmployeeCostId equals e.EmployeeCostId
                join act in _db.Activities.AsNoTracking() on a.ActivityId equals act.ActivityId
                join proj in _db.Projects.AsNoTracking() on a.ProjectId equals proj.ProjectId into projJoin
                from proj in projJoin.DefaultIfEmpty()
                where ids.Contains(a.EmployeeCostId)
                orderby e.EmployeeId, act.ActivityCode
                select new
                {
                    e.EmployeeId,
                    ActivityCode = act.ActivityCode,
                    ProjectCode = proj != null ? proj.ProjectCode : "",
                    a.AllocatedAmount,
                    e.AnnualCost
                }
            ).ToListAsync();

            var allocWs = wb.AddWorksheet("HR Allocations");
            allocWs.Cell(1, 1).Value = "EmployeeID";
            allocWs.Cell(1, 2).Value = "Activity Code";
            allocWs.Cell(1, 3).Value = "Project Code";
            allocWs.Cell(1, 4).Value = "Allocated Percent";
            allocWs.Cell(1, 5).Value = "Allocated Amount";

            allocWs.Range(1, 1, 1, 5).Style.Font.Bold = true;
            allocWs.Range(1, 1, 1, 5).Style.Fill.BackgroundColor = XLColor.LightGray;
            allocWs.Column(4).Style.NumberFormat.Format = "0.00";
            allocWs.Column(5).Style.NumberFormat.Format = "#,##0.00";

            var ar = 2;
            foreach (var a in allocations)
            {
                var pct = a.AnnualCost <= 0m ? 0m : Math.Round((a.AllocatedAmount / a.AnnualCost) * 100m, 2, MidpointRounding.AwayFromZero);
                allocWs.Cell(ar, 1).Value = a.EmployeeId;
                allocWs.Cell(ar, 2).Value = a.ActivityCode;
                allocWs.Cell(ar, 3).Value = a.ProjectCode;
                allocWs.Cell(ar, 4).Value = pct;
                allocWs.Cell(ar, 5).Value = a.AllocatedAmount;
                ar++;
            }

            allocWs.Columns(1, 5).AdjustToContents();

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            var bytes = ms.ToArray();

            var fileName = $"HR_Costs_{year}_Export.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "AdminOnly")]
        public async Task<IActionResult> Import(IFormFile file, int budgetYear)
        {
            var adminEntityId = GetAdminScopedEntityId();
            ViewBag.IsBudget = false;
            return await HandleImport(file, budgetYear, adminEntityId, null, null, true);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BudgetImport(IFormFile file, int budgetYear)
        {
            var entityId = HttpContext.Session.GetInt("ctxEntityId");
            var deptId = HttpContext.Session.GetInt("ctxDeptId");
            if (!(entityId.HasValue && deptId.HasValue))
            {
                return RedirectToAction("Select", "Context");
            }

            ViewBag.IsBudget = true;
            return await HandleImport(file, budgetYear, null, entityId.Value, deptId.Value, false);
        }

        [HttpGet]
        public async Task<IActionResult> Allocate(int id, bool budget = false)
        {
            var employeeCost = await _db.HrEmployeeCosts
                .Include(x => x.HrEmployeeCostAllocations)
                    .ThenInclude(a => a.Activity)
                        .ThenInclude(a => a.Program)
                .Include(x => x.HrEmployeeCostAllocations)
                    .ThenInclude(a => a.Project)
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.EmployeeCostId == id);

            if (employeeCost == null)
            {
                return NotFound();
            }

            ViewBag.IsBudget = budget;

            var allocated = employeeCost.HrEmployeeCostAllocations.Sum(x => x.AllocatedAmount);
            var remaining = employeeCost.AnnualCost - allocated;
            var allocatedPct = employeeCost.AnnualCost <= 0 ? 0 : Math.Min(100m, Math.Round((allocated / employeeCost.AnnualCost) * 100m, 2));
            var remainingPct = employeeCost.AnnualCost <= 0 ? 0 : Math.Max(0m, Math.Round((remaining / employeeCost.AnnualCost) * 100m, 2));

            ViewBag.Allocated = allocated;
            ViewBag.Remaining = remaining;
            ViewBag.AllocatedPct = allocatedPct;
            ViewBag.RemainingPct = remainingPct;

            var activitiesQuery = _db.Activities.AsNoTracking().Where(a => a.IsActive);
            if (employeeCost.DepartmentId.HasValue)
            {
                activitiesQuery = activitiesQuery.Where(a => a.DepartmentId == employeeCost.DepartmentId.Value);
            }
            var activities = await activitiesQuery
                .OrderBy(a => a.ActivityCode)
                .Select(a => new { a.ActivityId, Display = a.ActivityCode + " - " + a.ActivityName })
                .ToListAsync();

            ViewBag.Activities = new SelectList(activities, "ActivityId", "Display");
            ViewBag.ActivityItems = activities
                .Select(a => new SelectListItem { Value = a.ActivityId.ToString(), Text = a.Display })
                .ToList();

            var projectsQuery = _db.Projects.AsNoTracking().Where(p => p.IsActive);
            if (employeeCost.DepartmentId.HasValue)
            {
                projectsQuery = projectsQuery.Where(p => p.OwningDepartmentId == null || p.OwningDepartmentId == employeeCost.DepartmentId.Value);
            }
            var projects = await projectsQuery
                .OrderBy(p => p.ProjectCode)
                .Select(p => new { p.ProjectId, Display = p.ProjectCode + " - " + p.ProjectName })
                .ToListAsync();

            ViewBag.Projects = new SelectList(projects, "ProjectId", "Display");
            ViewBag.ProjectItems = projects
                .Select(p => new SelectListItem { Value = p.ProjectId.ToString(), Text = p.Display })
                .ToList();

            return View(employeeCost);
        }

        [HttpGet]
        public async Task<IActionResult> ViewAllocations(int id)
        {
            var employeeCost = await _db.HrEmployeeCosts
                .Include(x => x.HrEmployeeCostAllocations)
                    .ThenInclude(a => a.Activity)
                        .ThenInclude(a => a.Program)
                .Include(x => x.HrEmployeeCostAllocations)
                    .ThenInclude(a => a.Project)
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.EmployeeCostId == id);

            if (employeeCost == null)
            {
                return NotFound();
            }

            return PartialView("_EmployeeAllocations", employeeCost);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddAllocation(int employeeCostId, int activityId, int? projectId, string? allocationMode, decimal? allocatedAmount, decimal? allocatedPercent, bool budget = false)
        {
            var employee = await _db.HrEmployeeCosts.FirstOrDefaultAsync(x => x.EmployeeCostId == employeeCostId);
            if (employee == null)
            {
                return NotFound();
            }

            var mode = (allocationMode ?? "amount").Trim().ToLowerInvariant();
            decimal amountToAllocate;
            decimal? percentUsed = null;

            if (mode == "percent")
            {
                if (!allocatedPercent.HasValue || allocatedPercent.Value <= 0m)
                {
                    TempData["Error"] = "Allocated percent must be greater than zero.";
                    return RedirectToAction(nameof(Allocate), new { id = employeeCostId, budget });
                }

                if (allocatedPercent.Value > 100m)
                {
                    TempData["Error"] = "Allocated percent cannot be more than 100%.";
                    return RedirectToAction(nameof(Allocate), new { id = employeeCostId, budget });
                }

                if (employee.AnnualCost <= 0m)
                {
                    TempData["Error"] = "Cannot allocate by percent when annual cost is zero.";
                    return RedirectToAction(nameof(Allocate), new { id = employeeCostId, budget });
                }

                percentUsed = allocatedPercent.Value;
                amountToAllocate = Math.Round((employee.AnnualCost * allocatedPercent.Value) / 100m, 2, MidpointRounding.AwayFromZero);
                if (amountToAllocate <= 0m)
                {
                    TempData["Error"] = "Allocated amount must be greater than zero.";
                    return RedirectToAction(nameof(Allocate), new { id = employeeCostId, budget });
                }
            }
            else
            {
                if (!allocatedAmount.HasValue || allocatedAmount.Value <= 0m)
                {
                    TempData["Error"] = "Allocated amount must be greater than zero.";
                    return RedirectToAction(nameof(Allocate), new { id = employeeCostId, budget });
                }

                amountToAllocate = allocatedAmount.Value;
                if (employee.AnnualCost > 0m)
                {
                    percentUsed = Math.Round((amountToAllocate / employee.AnnualCost) * 100m, 2, MidpointRounding.AwayFromZero);
                }
            }

            var currentAllocated = await _db.HrEmployeeCostAllocations
                .Where(x => x.EmployeeCostId == employeeCostId)
                .SumAsync(x => (decimal?)x.AllocatedAmount) ?? 0m;

            var newTotal = currentAllocated + amountToAllocate;
            if (newTotal > employee.AnnualCost + AllocationTolerance)
            {
                TempData["Error"] = "Cost allocated is more than annual cost.";
                return RedirectToAction(nameof(Allocate), new { id = employeeCostId, budget });
            }

            var createdBy = User.Identity?.Name ?? "Unknown";
            _db.HrEmployeeCostAllocations.Add(new HrEmployeeCostAllocations
            {
                EmployeeCostId = employeeCostId,
                ActivityId = activityId,
                ProjectId = projectId,
                AllocatedAmount = amountToAllocate,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = createdBy
            });
            await _db.SaveChangesAsync();

            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = createdBy,
                Action = "ALLOCATE",
                EntityName = "HrEmployeeCostAllocations",
                RecordId = employeeCostId.ToString(),
                Timestamp = DateTime.UtcNow,
                Details = $"Allocated {amountToAllocate} ({(percentUsed.HasValue ? percentUsed.Value.ToString("0.##") + "%" : "N/A")}) to ActivityId={activityId}, ProjectId={(projectId.HasValue ? projectId.Value.ToString() : "NULL")}."
            });
            await _db.SaveChangesAsync();

            TempData["Success"] = "Allocation saved.";
            return RedirectToAction(nameof(Allocate), new { id = employeeCostId, budget });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditAllocation(long allocationId, int employeeCostId, int activityId, int? projectId, string? allocationMode, decimal? allocatedAmount, decimal? allocatedPercent, bool budget = false)
        {
            var alloc = await _db.HrEmployeeCostAllocations
                .FirstOrDefaultAsync(x => x.AllocationId == allocationId && x.EmployeeCostId == employeeCostId);
            if (alloc == null)
            {
                return NotFound();
            }

            var employee = await _db.HrEmployeeCosts.FirstOrDefaultAsync(x => x.EmployeeCostId == employeeCostId);
            if (employee == null)
            {
                return NotFound();
            }

            var mode = (allocationMode ?? "amount").Trim().ToLowerInvariant();
            decimal amountToAllocate;
            decimal? percentUsed = null;

            if (mode == "percent")
            {
                if (!allocatedPercent.HasValue || allocatedPercent.Value <= 0m)
                {
                    TempData["Error"] = "Allocated percent must be greater than zero.";
                    return RedirectToAction(nameof(Allocate), new { id = employeeCostId, budget });
                }

                if (allocatedPercent.Value > 100m)
                {
                    TempData["Error"] = "Allocated percent cannot be more than 100%.";
                    return RedirectToAction(nameof(Allocate), new { id = employeeCostId, budget });
                }

                if (employee.AnnualCost <= 0m)
                {
                    TempData["Error"] = "Cannot allocate by percent when annual cost is zero.";
                    return RedirectToAction(nameof(Allocate), new { id = employeeCostId, budget });
                }

                percentUsed = allocatedPercent.Value;
                amountToAllocate = Math.Round((employee.AnnualCost * allocatedPercent.Value) / 100m, 2, MidpointRounding.AwayFromZero);
            }
            else
            {
                if (!allocatedAmount.HasValue || allocatedAmount.Value <= 0m)
                {
                    TempData["Error"] = "Allocated amount must be greater than zero.";
                    return RedirectToAction(nameof(Allocate), new { id = employeeCostId, budget });
                }

                amountToAllocate = allocatedAmount.Value;
                if (employee.AnnualCost > 0m)
                {
                    percentUsed = Math.Round((amountToAllocate / employee.AnnualCost) * 100m, 2, MidpointRounding.AwayFromZero);
                }
            }

            if (amountToAllocate <= 0m)
            {
                TempData["Error"] = "Allocated amount must be greater than zero.";
                return RedirectToAction(nameof(Allocate), new { id = employeeCostId, budget });
            }

            var otherAllocated = await _db.HrEmployeeCostAllocations
                .Where(x => x.EmployeeCostId == employeeCostId && x.AllocationId != allocationId)
                .SumAsync(x => (decimal?)x.AllocatedAmount) ?? 0m;

            if (otherAllocated + amountToAllocate > employee.AnnualCost + AllocationTolerance)
            {
                TempData["Error"] = "Cost allocated is more than annual cost.";
                return RedirectToAction(nameof(Allocate), new { id = employeeCostId, budget });
            }

            alloc.ActivityId = activityId;
            alloc.ProjectId = projectId;
            alloc.AllocatedAmount = amountToAllocate;
            await _db.SaveChangesAsync();

            var editedBy = User.Identity?.Name ?? "Unknown";
            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = editedBy,
                Action = "UPDATE",
                EntityName = "HrEmployeeCostAllocations",
                RecordId = allocationId.ToString(),
                Timestamp = DateTime.UtcNow,
                Details = $"Edited allocation for EmployeeCostId={employeeCostId}. Amount={amountToAllocate} ({(percentUsed.HasValue ? percentUsed.Value.ToString("0.##") + "%" : "N/A")}), ActivityId={activityId}, ProjectId={(projectId.HasValue ? projectId.Value.ToString() : "NULL")}."
            });
            await _db.SaveChangesAsync();

            TempData["Success"] = "Allocation updated.";
            return RedirectToAction(nameof(Allocate), new { id = employeeCostId, budget });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteAllocation(long allocationId, int employeeCostId, bool budget = false)
        {
            var alloc = await _db.HrEmployeeCostAllocations.FirstOrDefaultAsync(x => x.AllocationId == allocationId);
            if (alloc != null)
            {
                _db.HrEmployeeCostAllocations.Remove(alloc);
                await _db.SaveChangesAsync();

                var userName = User.Identity?.Name ?? "Unknown";
                _db.AuditLogs.Add(new AuditLogs
                {
                    UserName = userName,
                    Action = "DELETE",
                    EntityName = "HrEmployeeCostAllocations",
                    RecordId = allocationId.ToString(),
                    Timestamp = DateTime.UtcNow,
                    Details = $"Deleted allocation for EmployeeCostId={employeeCostId}."
                });
                await _db.SaveChangesAsync();
            }

            return RedirectToAction(nameof(Allocate), new { id = employeeCostId, budget });
        }

        private static byte[] BuildHrImportTemplateBytes()
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("HR Costs");

            ws.Cell(1, 1).Value = "EmployeeID";
            ws.Cell(1, 2).Value = "Employee Name";
            ws.Cell(1, 3).Value = "Entity Name";
            ws.Cell(1, 4).Value = "Department Name";
            ws.Cell(1, 5).Value = "Annual Cost";
            ws.Cell(1, 6).Value = "GL Account Code";
            ws.Cell(1, 7).Value = "GL Kind";
            ws.Cell(1, 8).Value = "Occupation";

            ws.Range(1, 1, 1, 8).Style.Font.Bold = true;
            ws.Range(1, 1, 1, 8).Style.Fill.BackgroundColor = XLColor.LightGray;
            ws.Column(5).Style.NumberFormat.Format = "#,##0.00";

            ws.Cell(2, 1).Value = "E0001";
            ws.Cell(2, 2).Value = "Sample Employee";
            ws.Cell(2, 3).Value = "Sample Entity";
            ws.Cell(2, 4).Value = "Sample Cost Center";
            ws.Cell(2, 5).Value = 120000;
            ws.Cell(2, 6).Value = "6000";
            ws.Cell(2, 7).Value = "HR";
            ws.Cell(2, 8).Value = "Accountant";

            ws.Columns().AdjustToContents();

            var allocWs = wb.AddWorksheet("HR Allocations");
            allocWs.Cell(1, 1).Value = "EmployeeID";
            allocWs.Cell(1, 2).Value = "Activity Code";
            allocWs.Cell(1, 3).Value = "Project Code";
            allocWs.Cell(1, 4).Value = "Allocated Percent";
            allocWs.Cell(1, 5).Value = "Allocated Amount";

            allocWs.Range(1, 1, 1, 5).Style.Font.Bold = true;
            allocWs.Range(1, 1, 1, 5).Style.Fill.BackgroundColor = XLColor.LightGray;
            allocWs.Column(4).Style.NumberFormat.Format = "0.00";
            allocWs.Column(5).Style.NumberFormat.Format = "#,##0.00";

            allocWs.Cell(2, 1).Value = "E0001";
            allocWs.Cell(2, 2).Value = "ACT001";
            allocWs.Cell(2, 3).Value = "PRJ001";
            allocWs.Cell(2, 4).Value = 100;
            allocWs.Cell(2, 5).Value = "";

            allocWs.Cell(4, 1).Value =
                "Note: 'Allocated Percent' must be a plain number where 35 means 35% (do NOT format the cell as a percentage). " +
                "Use EITHER Allocated Percent OR Allocated Amount per row. Total allocations per employee cannot exceed the Annual Cost.";
            allocWs.Cell(4, 1).Style.Font.Italic = true;
            allocWs.Cell(4, 1).Style.Font.FontColor = XLColor.Gray;

            allocWs.Columns().AdjustToContents();

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }

        // Retries are enabled on the SQL Server provider, so a manual transaction has to be
        // opened inside the execution strategy or EF throws
        // "SqlServerRetryingExecutionStrategy does not support user-initiated transactions".
        private Task<IActionResult> HandleImport(
            IFormFile file,
            int budgetYear,
            int? adminEntityId,
            int? forcedEntityId,
            int? forcedDeptId,
            bool redirectToAdminIndex)
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            return strategy.ExecuteAsync(() => HandleImportCore(
                file, budgetYear, adminEntityId, forcedEntityId, forcedDeptId, redirectToAdminIndex));
        }

        private async Task<IActionResult> HandleImportCore(
            IFormFile file,
            int budgetYear,
            int? adminEntityId,
            int? forcedEntityId,
            int? forcedDeptId,
            bool redirectToAdminIndex)
        {
            void PopulateYearOptions(int selected)
            {
                var thisYear = DateTime.Now.Year;
                var years = new[] { thisYear - 1, thisYear, thisYear + 1, thisYear + 2 }
                    .Select(y => new { Id = y, Name = y.ToString() }).ToList();
                ViewBag.BudgetYear = new SelectList(years, "Id", "Name", selected);
            }

            IActionResult ReturnImportView()
            {
                PopulateYearOptions(budgetYear);
                return View("Import");
            }

            if (file == null || file.Length == 0)
            {
                ModelState.AddModelError("", "Please choose an Excel file.");
                return ReturnImportView();
            }

            var ext = Path.GetExtension(file.FileName);
            if (!string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError("", "Only .xlsx files are supported.");
                return ReturnImportView();
            }

            var errors = new List<string>();
            var importedBy = User.Identity?.Name;
            var inserted = 0;
            var updated = 0;
            var allocationsInserted = 0;

            // A retry replays this whole method, so drop anything tracked by a failed attempt.
            _db.ChangeTracker.Clear();

            await using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                using var stream = file.OpenReadStream();
                using var wb = new XLWorkbook(stream);
                var ws = wb.Worksheets.First();

                var headerRowNumber = 1;
                var headerRow = ws.Row(headerRowNumber);
                var colMap = BuildHeaderMap(headerRow);

                var hasGlCodeCol =
                    colMap.TryGetValue("glaccountcode", out var glCodeCol) ||
                    colMap.TryGetValue("glcode", out glCodeCol) ||
                    colMap.TryGetValue("gl", out glCodeCol);

                var hasGlKindCol =
                    colMap.TryGetValue("glkind", out var glKindCol) ||
                    colMap.TryGetValue("gltype", out glKindCol);

                // Optional: employee occupation / job title.
                var hasOccupationCol =
                    colMap.TryGetValue("occupation", out var occupationCol) ||
                    colMap.TryGetValue("jobtitle", out occupationCol) ||
                    colMap.TryGetValue("title", out occupationCol) ||
                    colMap.TryGetValue("designation", out occupationCol);

                if (!colMap.TryGetValue("employeeid", out var employeeIdCol) ||
                    !colMap.TryGetValue("employeename", out var employeeNameCol) ||
                    !colMap.TryGetValue("entityname", out var entityNameCol) ||
                    !colMap.TryGetValue("departmentname", out var departmentNameCol) ||
                    !colMap.TryGetValue("annualcost", out var annualCostCol) ||
                    !hasGlCodeCol ||
                    !hasGlKindCol)
                {
                    ModelState.AddModelError("", "Missing required columns. Required: EmployeeID, Employee Name, Entity Name, Department Name, Annual Cost, GL Account Code, GL Kind.");
                    return ReturnImportView();
                }

                var validGlCodes = await _db.GLAccounts
                    .AsNoTracking()
                    .Select(x => x.GLCode)
                    .ToListAsync();
                var validGlSet = new HashSet<string>(validGlCodes, StringComparer.OrdinalIgnoreCase);

                var fixedEntity = forcedEntityId.HasValue
                    ? await _db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.EntityId == forcedEntityId.Value)
                    : null;
                var fixedDept = (forcedEntityId.HasValue && forcedDeptId.HasValue)
                    ? await _db.Departments.AsNoTracking()
                        .FirstOrDefaultAsync(d => d.DepartmentId == forcedDeptId.Value && d.EntityId == forcedEntityId.Value)
                    : null;

                if (forcedEntityId.HasValue && fixedEntity == null)
                {
                    ModelState.AddModelError("", "Context entity not found.");
                    return ReturnImportView();
                }

                if (forcedDeptId.HasValue && fixedDept == null)
                {
                    ModelState.AddModelError("", "Context department not found.");
                    return ReturnImportView();
                }

                var lastRow = ws.LastRowUsed()?.RowNumber() ?? headerRowNumber;
                for (var r = headerRowNumber + 1; r <= lastRow; r++)
                {
                    var row = ws.Row(r);

                    var employeeId = row.Cell(employeeIdCol).GetString().Trim();
                    if (string.IsNullOrWhiteSpace(employeeId))
                    {
                        continue;
                    }

                    var employeeName = row.Cell(employeeNameCol).GetString().Trim();
                    var entityName = row.Cell(entityNameCol).GetString().Trim();
                    var departmentName = row.Cell(departmentNameCol).GetString().Trim();
                    var annualCostRaw = row.Cell(annualCostCol).GetValue<string>().Trim();
                    var glCode = row.Cell(glCodeCol).GetString().Trim();
                    var glKindRaw = row.Cell(glKindCol).GetString().Trim();
                    var occupationRaw = hasOccupationCol ? row.Cell(occupationCol).GetString().Trim() : null;
                    var occupation = string.IsNullOrWhiteSpace(occupationRaw) ? null : occupationRaw;

                    if (string.IsNullOrWhiteSpace(employeeName) ||
                        string.IsNullOrWhiteSpace(entityName) ||
                        string.IsNullOrWhiteSpace(departmentName) ||
                        string.IsNullOrWhiteSpace(annualCostRaw) ||
                        string.IsNullOrWhiteSpace(glCode) ||
                        string.IsNullOrWhiteSpace(glKindRaw))
                    {
                        errors.Add($"Row {r}: Missing required data.");
                        continue;
                    }

                    if (!TryParseDecimal(annualCostRaw, out var annualCost) || annualCost <= 0)
                    {
                        errors.Add($"Row {r}: Invalid Annual Cost '{annualCostRaw}'.");
                        continue;
                    }

                    if (!validGlSet.Contains(glCode))
                    {
                        errors.Add($"Row {r}: GL '{glCode}' not found.");
                        continue;
                    }

                    var glKind = glKindRaw.Trim().ToUpperInvariant();
                    if (!ValidGlKinds.Contains(glKind))
                    {
                        errors.Add($"Row {r}: GL Kind '{glKindRaw}' is invalid. Allowed: {string.Join(", ", ValidGlKinds.OrderBy(x => x))}.");
                        continue;
                    }

                    Entities? entity;
                    if (fixedEntity != null)
                    {
                        var ok = string.Equals(entityName, fixedEntity.EntityName, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(entityName, fixedEntity.EntityCode, StringComparison.OrdinalIgnoreCase);
                        if (!ok)
                        {
                            errors.Add($"Row {r}: Entity '{entityName}' does not match your current context.");
                            continue;
                        }
                        entity = fixedEntity;
                    }
                    else
                    {
                        var entityQuery = _db.Entities.AsNoTracking().AsQueryable();
                        if (adminEntityId.HasValue)
                        {
                            entityQuery = entityQuery.Where(e => e.EntityId == adminEntityId.Value);
                        }

                        entity = await entityQuery.FirstOrDefaultAsync(e =>
                            e.EntityName.ToLower() == entityName.ToLower()
                            || e.EntityCode.ToLower() == entityName.ToLower());

                        if (entity == null)
                        {
                            errors.Add($"Row {r}: Entity '{entityName}' not found.");
                            continue;
                        }

                        if (adminEntityId.HasValue && entity.EntityId != adminEntityId.Value)
                        {
                            errors.Add($"Row {r}: You can only import HR costs for your entity.");
                            continue;
                        }
                    }

                    Departments? dept;
                    if (fixedDept != null)
                    {
                        var ok = string.Equals(departmentName, fixedDept.DeptName, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(departmentName, fixedDept.DeptCode, StringComparison.OrdinalIgnoreCase);
                        if (!ok)
                        {
                            errors.Add($"Row {r}: Cost Center '{departmentName}' does not match your current context.");
                            continue;
                        }
                        dept = fixedDept;
                    }
                    else
                    {
                        dept = await _db.Departments
                            .AsNoTracking()
                            .FirstOrDefaultAsync(d =>
                                d.EntityId == entity.EntityId &&
                                (d.DeptName.ToLower() == departmentName.ToLower() || d.DeptCode.ToLower() == departmentName.ToLower()));

                        if (dept == null)
                        {
                            errors.Add($"Row {r}: Cost Center '{departmentName}' not found under Entity '{entityName}'.");
                            continue;
                        }
                    }

                    var existing = await _db.HrEmployeeCosts
                        .FirstOrDefaultAsync(x => x.BudgetYear == budgetYear
                                                  && x.EmployeeId == employeeId
                                                  && (!forcedEntityId.HasValue || x.EntityId == forcedEntityId.Value)
                                                  && (!forcedDeptId.HasValue || x.DepartmentId == forcedDeptId.Value));

                    if (adminEntityId.HasValue && existing != null && (!existing.EntityId.HasValue || existing.EntityId.Value != adminEntityId.Value))
                    {
                        errors.Add($"Row {r}: Employee '{employeeId}' exists under another entity.");
                        continue;
                    }

                    if ((forcedEntityId.HasValue || forcedDeptId.HasValue) && existing == null)
                    {
                        var existsOther = await _db.HrEmployeeCosts.AsNoTracking()
                            .AnyAsync(x => x.BudgetYear == budgetYear && x.EmployeeId == employeeId);
                        if (existsOther)
                        {
                            errors.Add($"Row {r}: Employee '{employeeId}' exists under another scope.");
                            continue;
                        }
                    }

                    if (existing == null)
                    {
                        var rec = new HrEmployeeCosts
                        {
                            BudgetYear = budgetYear,
                            EmployeeId = employeeId,
                            EmployeeName = employeeName,
                            Occupation = occupation,
                            GLCode = glCode,
                            GLKind = glKind,
                            EntityId = entity.EntityId,
                            EntityName = entityName,
                            DepartmentId = dept.DepartmentId,
                            DepartmentName = departmentName,
                            AnnualCost = annualCost,
                            ImportedAt = DateTime.UtcNow,
                            ImportedBy = importedBy,
                            SourceFile = file.FileName
                        };

                        _db.HrEmployeeCosts.Add(rec);
                        inserted++;
                    }
                    else
                    {
                        existing.EmployeeName = employeeName;
                        if (hasOccupationCol) existing.Occupation = occupation;
                        existing.GLCode = glCode;
                        existing.GLKind = glKind;
                        existing.EntityId = entity.EntityId;
                        existing.EntityName = entityName;
                        existing.DepartmentId = dept.DepartmentId;
                        existing.DepartmentName = departmentName;
                        existing.AnnualCost = annualCost;
                        existing.ImportedAt = DateTime.UtcNow;
                        existing.ImportedBy = importedBy;
                        existing.SourceFile = file.FileName;
                        updated++;
                    }
                }

                await _db.SaveChangesAsync();

                var allocWs = wb.Worksheets.FirstOrDefault(x =>
                    string.Equals(x.Name, "HR Allocations", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.Name, "Allocations", StringComparison.OrdinalIgnoreCase));

                if (allocWs != null)
                {
                    var allocHeaderRowNumber = 1;
                    var allocHeaderRow = allocWs.Row(allocHeaderRowNumber);
                    var allocColMap = BuildHeaderMap(allocHeaderRow);

                    var hasActivityCodeCol =
                        allocColMap.TryGetValue("activitycode", out var activityCodeCol) ||
                        allocColMap.TryGetValue("activity", out activityCodeCol);
                    var hasActivityIdCol =
                        allocColMap.TryGetValue("activityid", out var activityIdCol);

                    var hasProjectCodeCol =
                        allocColMap.TryGetValue("projectcode", out var projectCodeCol) ||
                        allocColMap.TryGetValue("project", out projectCodeCol);
                    var hasProjectIdCol =
                        allocColMap.TryGetValue("projectid", out var projectIdCol);

                    var hasAllocatedPercentCol =
                        allocColMap.TryGetValue("allocatedpercent", out var allocatedPercentCol) ||
                        allocColMap.TryGetValue("allocationpercent", out allocatedPercentCol) ||
                        allocColMap.TryGetValue("allocation", out allocatedPercentCol) ||
                        allocColMap.TryGetValue("percent", out allocatedPercentCol);

                    var hasAllocatedAmountCol =
                        allocColMap.TryGetValue("allocatedamount", out var allocatedAmountCol) ||
                        allocColMap.TryGetValue("allocationamount", out allocatedAmountCol) ||
                        allocColMap.TryGetValue("amount", out allocatedAmountCol);

                    if (!allocColMap.TryGetValue("employeeid", out var allocEmployeeIdCol) ||
                        (!hasActivityCodeCol && !hasActivityIdCol) ||
                        (!hasAllocatedPercentCol && !hasAllocatedAmountCol))
                    {
                        errors.Add("Allocations sheet found but required columns are missing. Required: EmployeeID, Activity Code (or ActivityId), and Allocated Percent (or Allocated Amount).");
                    }
                    else
                    {
                        var employeeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var allocLastRow = allocWs.LastRowUsed()?.RowNumber() ?? allocHeaderRowNumber;
                        for (var r = allocHeaderRowNumber + 1; r <= allocLastRow; r++)
                        {
                            var employeeId = allocWs.Row(r).Cell(allocEmployeeIdCol).GetString().Trim();
                            if (!string.IsNullOrWhiteSpace(employeeId))
                            {
                                employeeIds.Add(employeeId);
                            }
                        }

                        var empQuery = _db.HrEmployeeCosts
                            .Where(x => x.BudgetYear == budgetYear && employeeIds.Contains(x.EmployeeId));

                        if (adminEntityId.HasValue)
                        {
                            empQuery = empQuery.Where(x => x.EntityId == adminEntityId.Value);
                        }

                        if (forcedEntityId.HasValue)
                        {
                            empQuery = empQuery.Where(x => x.EntityId == forcedEntityId.Value);
                        }

                        if (forcedDeptId.HasValue)
                        {
                            empQuery = empQuery.Where(x => x.DepartmentId == forcedDeptId.Value);
                        }

                        var employees = await empQuery.ToListAsync();
                        var empById = employees.ToDictionary(x => x.EmployeeId, StringComparer.OrdinalIgnoreCase);

                        var acts = await _db.Activities.AsNoTracking()
                            .Where(a => a.IsActive)
                            .Select(a => new { a.ActivityId, a.ActivityCode, a.DepartmentId })
                            .ToListAsync();
                        var actById = acts.ToDictionary(a => a.ActivityId);
                        var actByCode = acts
                            .Where(a => !string.IsNullOrWhiteSpace(a.ActivityCode))
                            .ToDictionary(a => a.ActivityCode, a => a, StringComparer.OrdinalIgnoreCase);

                        var projs = await _db.Projects.AsNoTracking()
                            .Where(p => p.IsActive)
                            .Select(p => new { p.ProjectId, p.ProjectCode, p.OwningDepartmentId })
                            .ToListAsync();
                        var projById = projs.ToDictionary(p => p.ProjectId);
                        var projByCode = projs
                            .Where(p => !string.IsNullOrWhiteSpace(p.ProjectCode))
                            .ToDictionary(p => p.ProjectCode, p => p, StringComparer.OrdinalIgnoreCase);

                        var deletedAllocForEmployee = new HashSet<int>();
                        var allocatedSumByEmployee = new Dictionary<int, decimal>();

                        for (var r = allocHeaderRowNumber + 1; r <= allocLastRow; r++)
                        {
                            var row = allocWs.Row(r);
                            var employeeId = row.Cell(allocEmployeeIdCol).GetString().Trim();
                            if (string.IsNullOrWhiteSpace(employeeId))
                            {
                                continue;
                            }

                            if (!empById.TryGetValue(employeeId, out var employee))
                            {
                                errors.Add($"Allocations Row {r}: Employee '{employeeId}' not found for year {budgetYear}.");
                                continue;
                            }

                            int activityId;
                            if (hasActivityIdCol)
                            {
                                var raw = row.Cell(activityIdCol).GetValue<string>().Trim();
                                if (!int.TryParse(raw, out activityId) || activityId <= 0)
                                {
                                    errors.Add($"Allocations Row {r}: Invalid ActivityId '{raw}'.");
                                    continue;
                                }
                            }
                            else
                            {
                                var code = row.Cell(activityCodeCol).GetString().Trim();
                                if (string.IsNullOrWhiteSpace(code) || !actByCode.TryGetValue(code, out var act))
                                {
                                    errors.Add($"Allocations Row {r}: Activity '{row.Cell(activityCodeCol).GetString()}' not found.");
                                    continue;
                                }
                                activityId = act.ActivityId;
                            }

                            if (!actById.TryGetValue(activityId, out var activityInfo))
                            {
                                errors.Add($"Allocations Row {r}: ActivityId '{activityId}' not found.");
                                continue;
                            }

                            if (employee.DepartmentId.HasValue && activityInfo.DepartmentId != employee.DepartmentId.Value)
                            {
                                errors.Add($"Allocations Row {r}: Activity '{activityInfo.ActivityCode}' is not under the employee's department.");
                                continue;
                            }

                            int? projectId = null;
                            if (hasProjectIdCol)
                            {
                                var raw = row.Cell(projectIdCol).GetValue<string>().Trim();
                                if (!string.IsNullOrWhiteSpace(raw))
                                {
                                    if (!int.TryParse(raw, out var parsed) || parsed <= 0)
                                    {
                                        errors.Add($"Allocations Row {r}: Invalid ProjectId '{raw}'.");
                                        continue;
                                    }
                                    projectId = parsed;
                                }
                            }
                            else if (hasProjectCodeCol)
                            {
                                var code = row.Cell(projectCodeCol).GetString().Trim();
                                if (!string.IsNullOrWhiteSpace(code))
                                {
                                    if (!projByCode.TryGetValue(code, out var proj))
                                    {
                                        errors.Add($"Allocations Row {r}: Project '{code}' not found.");
                                        continue;
                                    }
                                    projectId = proj.ProjectId;
                                }
                            }

                            if (projectId.HasValue)
                            {
                                if (!projById.TryGetValue(projectId.Value, out var projInfo))
                                {
                                    errors.Add($"Allocations Row {r}: ProjectId '{projectId.Value}' not found.");
                                    continue;
                                }

                                if (employee.DepartmentId.HasValue && projInfo.OwningDepartmentId.HasValue &&
                                    projInfo.OwningDepartmentId.Value != employee.DepartmentId.Value)
                                {
                                    errors.Add($"Allocations Row {r}: Project '{projInfo.ProjectCode}' is not allowed for the employee's department.");
                                    continue;
                                }
                            }

                            decimal percent = 0m;
                            decimal amount = 0m;

                            if (hasAllocatedPercentCol)
                            {
                                if (TryReadPercent(row.Cell(allocatedPercentCol), out var p))
                                {
                                    percent = p;
                                }
                            }

                            if (hasAllocatedAmountCol)
                            {
                                var raw = row.Cell(allocatedAmountCol).GetValue<string>().Trim();
                                if (!string.IsNullOrWhiteSpace(raw) && TryParseDecimal(raw, out var a))
                                {
                                    amount = a;
                                }
                            }

                            decimal amountToAllocate;
                            if (percent > 0m)
                            {
                                if (percent > 100m)
                                {
                                    errors.Add($"Allocations Row {r}: Allocated Percent cannot be more than 100%.");
                                    continue;
                                }

                                if (employee.AnnualCost <= 0m)
                                {
                                    errors.Add($"Allocations Row {r}: Cannot allocate by percent when annual cost is zero.");
                                    continue;
                                }

                                amountToAllocate = Math.Round((employee.AnnualCost * percent) / 100m, 2, MidpointRounding.AwayFromZero);
                            }
                            else if (amount > 0m)
                            {
                                amountToAllocate = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
                            }
                            else
                            {
                                errors.Add($"Allocations Row {r}: Provide Allocated Percent or Allocated Amount.");
                                continue;
                            }

                            if (amountToAllocate <= 0m)
                            {
                                errors.Add($"Allocations Row {r}: Allocated amount must be greater than zero.");
                                continue;
                            }

                            if (!allocatedSumByEmployee.TryGetValue(employee.EmployeeCostId, out var currentSum))
                            {
                                currentSum = 0m;
                            }
                            var newSum = currentSum + amountToAllocate;
                            if (newSum > employee.AnnualCost + AllocationTolerance)
                            {
                                errors.Add($"Allocations Row {r}: Total allocations ({newSum:N2}) exceed annual cost ({employee.AnnualCost:N2}) for Employee '{employeeId}'.");
                                continue;
                            }
                            allocatedSumByEmployee[employee.EmployeeCostId] = newSum;

                            if (!deletedAllocForEmployee.Contains(employee.EmployeeCostId))
                            {
                                await _db.HrEmployeeCostAllocations
                                    .Where(x => x.EmployeeCostId == employee.EmployeeCostId)
                                    .ExecuteDeleteAsync();
                                deletedAllocForEmployee.Add(employee.EmployeeCostId);
                            }

                            _db.HrEmployeeCostAllocations.Add(new HrEmployeeCostAllocations
                            {
                                EmployeeCostId = employee.EmployeeCostId,
                                ActivityId = activityId,
                                ProjectId = projectId,
                                AllocatedAmount = amountToAllocate,
                                CreatedAt = DateTime.UtcNow,
                                CreatedBy = importedBy ?? "Unknown"
                            });
                            allocationsInserted++;
                        }

                        await _db.SaveChangesAsync();
                    }
                }

                _db.AuditLogs.Add(new AuditLogs
                {
                    UserName = importedBy ?? "Unknown",
                    Action = "IMPORT",
                    EntityName = "HrEmployeeCosts",
                    Timestamp = DateTime.UtcNow,
                    Details = $"Imported HR costs for year {budgetYear}. Inserted: {inserted}, Updated: {updated}, AllocationsInserted: {allocationsInserted}, Errors: {errors.Count}. File: {file.FileName}"
                });
                await _db.SaveChangesAsync();

                await tx.CommitAsync();
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                ModelState.AddModelError("", $"Import failed: {ex.Message}");
                return ReturnImportView();
            }

            TempData["Success"] = $"Import completed. Inserted: {inserted}, Updated: {updated}, Allocations: {allocationsInserted}, Errors: {errors.Count}.";
            if (errors.Count > 0)
            {
                TempData["ImportErrors"] = string.Join("\n", errors.Take(50));
            }

            return redirectToAdminIndex
                ? RedirectToAction(nameof(Index), new { year = budgetYear })
                : RedirectToAction(nameof(Budget), new { year = budgetYear });
        }

        private static Dictionary<string, int> BuildHeaderMap(IXLRow headerRow)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in headerRow.CellsUsed())
            {
                var key = NormalizeHeader(cell.GetString());
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }
                map[key] = cell.Address.ColumnNumber;
            }
            return map;
        }

        private static string NormalizeHeader(string value)
        {
            var cleaned = new string(value.Trim().ToLowerInvariant().Where(c => char.IsLetterOrDigit(c)).ToArray());
            return cleaned;
        }

        private static bool TryParseDecimal(string raw, out decimal value)
        {
            return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value) ||
                   decimal.TryParse(raw, NumberStyles.Any, CultureInfo.CurrentCulture, out value);
        }

        // Reads an "Allocated Percent" cell tolerantly and always returns a whole-number percent.
        //  - Plain number 35                      => 35   (template convention: number meaning 35%)
        //  - Text "35" or "35%"                    => 35
        //  - Percent-formatted cell showing 35%    => 35   (Excel stores 0.35; we scale x100)
        private static bool TryReadPercent(IXLCell cell, out decimal percent)
        {
            percent = 0m;
            if (cell == null)
            {
                return false;
            }

            if (cell.DataType == XLDataType.Number)
            {
                var num = (decimal)cell.GetDouble();
                var fmt = cell.Style?.NumberFormat?.Format ?? string.Empty;
                var fmtId = cell.Style?.NumberFormat?.NumberFormatId ?? -1;
                var isPercentFormat = fmt.Contains("%") || fmtId == 9 || fmtId == 10;
                percent = isPercentFormat ? num * 100m : num;
                return true;
            }

            var raw = cell.GetString().Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var hadPercentSign = raw.Contains("%");
            raw = raw.Replace("%", "").Trim();
            if (!TryParseDecimal(raw, out var parsed))
            {
                return false;
            }

            // A text value entered as "35%" where Excel kept it as the fraction 0.35.
            if (hadPercentSign && parsed > 0m && parsed < 1m)
            {
                parsed *= 100m;
            }

            percent = parsed;
            return true;
        }

        private static readonly HashSet<string> ValidGlKinds = new(StringComparer.OrdinalIgnoreCase)
        {
            "HR",
            "OPEX",
            "CAPEX",
            "REVENUE",
            "TRANSFER",
            "TRANSFERS"
        };
    }
}
