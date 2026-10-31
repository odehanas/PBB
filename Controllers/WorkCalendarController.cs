using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GovBudget.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Controllers
{
    /// <summary>
    /// Maintains core.WorkCalendars - the working-time variables behind the
    /// employee cost-per-hour report.
    ///
    /// This screen writes to exactly one table, which nothing else in the system
    /// reads. Budget entry, HR import, allocation and reallocation are untouched
    /// by anything done here; the only visible effect is the rate shown on
    /// Reports - Employee Cost per Hour.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    public class WorkCalendarController : Controller
    {
        private readonly GovBudgetContext _db;

        public WorkCalendarController(GovBudgetContext db)
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

        // Only a global admin may touch the default (all-entity) calendar, since a
        // change there moves the rate for every entity at once.
        private bool IsGlobalAdmin()
        {
            if (User.IsInRole("SYSADMIN")) return true;
            if (!User.IsInRole("ADMIN")) return false;
            return !GetAdminScopedEntityId().HasValue;
        }

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
                list.Insert(0, new SelectListItem("All entities (default)", "", !selected.HasValue));
            }

            return list;
        }

        private async Task PopulateOptions(WorkCalendars model)
        {
            ViewBag.EntityOptions = await EntityOptions(model.EntityId);
            ViewBag.IsGlobalAdmin = IsGlobalAdmin();
        }

        private async Task WriteAudit(string action, WorkCalendars cal, string details)
        {
            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = User.Identity?.Name ?? "Unknown",
                Action = action,
                EntityName = "WorkCalendars",
                RecordId = cal.CalendarId.ToString(),
                Timestamp = DateTime.UtcNow,
                Details = details
            });
            await _db.SaveChangesAsync();
        }

        // A calendar whose deductions swallow the whole year would divide by zero (or
        // worse, go negative and invert every rate). Catch it before it is saved.
        private void ValidateHours(WorkCalendars model)
        {
            var contractedDays = model.WeeksPerYear * model.WorkDaysPerWeek;
            var absenceDays = model.PublicHolidayDays + model.AnnualLeaveDays + model.OtherPaidAbsenceDays;

            if (absenceDays >= contractedDays)
            {
                ModelState.AddModelError("", string.Format(
                    "Paid absence ({0:0.##} days) must be less than the {1:0.##} contracted days per year, " +
                    "otherwise there are no productive hours left to divide the annual cost by.",
                    absenceDays, contractedDays));
            }
        }

        // GET: WorkCalendar
        public async Task<IActionResult> Index(int? year = null)
        {
            var thisYear = DateTime.Now.Year;
            var selectedYear = year ?? thisYear;

            var years = new[] { thisYear - 1, thisYear, thisYear + 1, thisYear + 2 }
                .Select(y => new SelectListItem(y.ToString(), y.ToString(), y == selectedYear))
                .ToList();
            ViewBag.YearOptions = years;
            ViewBag.SelectedYear = selectedYear;
            ViewBag.IsGlobalAdmin = IsGlobalAdmin();

            var query = _db.WorkCalendars
                .Include(c => c.Entity)
                .AsNoTracking()
                .Where(c => c.BudgetYear == selectedYear);

            // An entity-scoped admin sees their own calendar and the default it falls
            // back to, but not other entities' rows.
            var myId = GetAdminScopedEntityId();
            if (!IsGlobalAdmin())
            {
                query = query.Where(c => c.EntityId == null || (myId.HasValue && c.EntityId == myId.Value));
            }

            var calendars = await query
                .OrderBy(c => c.EntityId == null ? 0 : 1)
                .ThenBy(c => c.Entity!.EntityCode)
                .ToListAsync();

            // How many employees each calendar actually drives, so an admin can see
            // the blast radius of an edit before making it.
            var coverage = await _db.HrEmployeeCosts.AsNoTracking()
                .Where(h => h.BudgetYear == selectedYear)
                .GroupBy(h => h.EntityId)
                .Select(g => new { EntityId = g.Key, Count = g.Count() })
                .ToListAsync();

            var entityIdsWithOwnCalendar = calendars
                .Where(c => c.EntityId.HasValue)
                .Select(c => c.EntityId!.Value)
                .ToHashSet();

            var map = new Dictionary<int, int>();
            var defaultCovered = 0;
            foreach (var c in coverage)
            {
                if (c.EntityId.HasValue && entityIdsWithOwnCalendar.Contains(c.EntityId.Value))
                {
                    map[c.EntityId.Value] = c.Count;
                }
                else
                {
                    defaultCovered += c.Count;
                }
            }

            ViewBag.CoverageByEntity = map;
            ViewBag.DefaultCoverage = defaultCovered;
            ViewBag.TotalEmployees = coverage.Sum(c => c.Count);

            return View(calendars);
        }

        // GET: WorkCalendar/Create
        public async Task<IActionResult> Create(int? year = null)
        {
            var model = new WorkCalendars
            {
                BudgetYear = year ?? DateTime.Now.Year,
                CalendarName = "Standard working calendar"
            };

            if (!IsGlobalAdmin())
            {
                model.EntityId = GetAdminScopedEntityId();
            }

            await PopulateOptions(model);
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(WorkCalendars model)
        {
            if (!IsGlobalAdmin())
            {
                // An entity admin can only ever create their own entity's calendar.
                var myId = GetAdminScopedEntityId();
                if (!myId.HasValue) return Forbid();
                model.EntityId = myId.Value;
            }

            ValidateHours(model);

            var clash = await _db.WorkCalendars.AsNoTracking().AnyAsync(c =>
                c.BudgetYear == model.BudgetYear && c.EntityId == model.EntityId);
            if (clash)
            {
                ModelState.AddModelError("", model.EntityId.HasValue
                    ? "That entity already has a calendar for this year. Edit the existing one instead."
                    : "A default calendar already exists for this year. Edit the existing one instead.");
            }

            if (!ModelState.IsValid)
            {
                await PopulateOptions(model);
                return View(model);
            }

            model.CreatedAt = DateTime.UtcNow;
            model.CreatedBy = User.Identity?.Name;
            _db.WorkCalendars.Add(model);
            await _db.SaveChangesAsync();

            await WriteAudit("CREATE", model,
                $"Created calendar '{model.CalendarName}' for {model.BudgetYear}, " +
                $"EntityId={(model.EntityId.HasValue ? model.EntityId.Value.ToString() : "ALL")}. " +
                $"Productive hours = {model.ProductiveHours:0.##}.");

            TempData["Success"] = "Calendar created. The cost per hour report reflects it immediately.";
            return RedirectToAction(nameof(Index), new { year = model.BudgetYear });
        }

        // GET: WorkCalendar/Edit/5
        public async Task<IActionResult> Edit(int id)
        {
            var cal = await _db.WorkCalendars.FindAsync(id);
            if (cal == null) return NotFound();

            if (!IsGlobalAdmin())
            {
                var myId = GetAdminScopedEntityId();
                if (!cal.EntityId.HasValue || !myId.HasValue || cal.EntityId.Value != myId.Value)
                {
                    return Forbid();
                }
            }

            await PopulateOptions(cal);
            return View(cal);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, WorkCalendars model)
        {
            if (id != model.CalendarId) return NotFound();

            var cal = await _db.WorkCalendars.FindAsync(id);
            if (cal == null) return NotFound();

            if (!IsGlobalAdmin())
            {
                var myId = GetAdminScopedEntityId();
                if (!cal.EntityId.HasValue || !myId.HasValue || cal.EntityId.Value != myId.Value)
                {
                    return Forbid();
                }

                // Scope is fixed for an entity admin - never take it from the form.
                model.EntityId = cal.EntityId;
            }

            ValidateHours(model);

            var clash = await _db.WorkCalendars.AsNoTracking().AnyAsync(c =>
                c.CalendarId != id && c.BudgetYear == model.BudgetYear && c.EntityId == model.EntityId);
            if (clash)
            {
                ModelState.AddModelError("", "Another calendar already covers that year and entity.");
            }

            if (!ModelState.IsValid)
            {
                await PopulateOptions(model);
                return View(model);
            }

            var before = $"{cal.ProductiveHours:0.##}h";

            cal.BudgetYear = model.BudgetYear;
            cal.EntityId = model.EntityId;
            cal.CalendarName = model.CalendarName;
            cal.HoursPerDay = model.HoursPerDay;
            cal.WorkDaysPerWeek = model.WorkDaysPerWeek;
            cal.WeeksPerYear = model.WeeksPerYear;
            cal.PublicHolidayDays = model.PublicHolidayDays;
            cal.AnnualLeaveDays = model.AnnualLeaveDays;
            cal.OtherPaidAbsenceDays = model.OtherPaidAbsenceDays;
            cal.UtilisationPct = model.UtilisationPct;
            cal.IsActive = model.IsActive;
            cal.UpdatedAt = DateTime.UtcNow;
            cal.UpdatedBy = User.Identity?.Name;

            await _db.SaveChangesAsync();

            await WriteAudit("UPDATE", cal,
                $"Updated calendar '{cal.CalendarName}' for {cal.BudgetYear}, " +
                $"EntityId={(cal.EntityId.HasValue ? cal.EntityId.Value.ToString() : "ALL")}. " +
                $"Productive hours {before} -> {cal.ProductiveHours:0.##}h.");

            TempData["Success"] = "Calendar updated. The cost per hour report reflects it immediately.";
            return RedirectToAction(nameof(Index), new { year = cal.BudgetYear });
        }

        // GET: WorkCalendar/Delete/5
        public async Task<IActionResult> Delete(int id)
        {
            var cal = await _db.WorkCalendars
                .Include(c => c.Entity)
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.CalendarId == id);
            if (cal == null) return NotFound();

            if (!IsGlobalAdmin())
            {
                var myId = GetAdminScopedEntityId();
                if (!cal.EntityId.HasValue || !myId.HasValue || cal.EntityId.Value != myId.Value)
                {
                    return Forbid();
                }
            }

            // Deleting the default row leaves every entity without a fallback, so say
            // how many people would lose their rate rather than letting it happen quietly.
            var affected = await _db.HrEmployeeCosts.AsNoTracking()
                .CountAsync(h => h.BudgetYear == cal.BudgetYear &&
                                 (cal.EntityId == null || h.EntityId == cal.EntityId));
            ViewBag.AffectedEmployees = affected;

            return View(cal);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var cal = await _db.WorkCalendars.FindAsync(id);
            if (cal == null) return NotFound();

            if (!IsGlobalAdmin())
            {
                var myId = GetAdminScopedEntityId();
                if (!cal.EntityId.HasValue || !myId.HasValue || cal.EntityId.Value != myId.Value)
                {
                    return Forbid();
                }
            }

            var year = cal.BudgetYear;
            var describe = $"'{cal.CalendarName}' for {year}, " +
                           $"EntityId={(cal.EntityId.HasValue ? cal.EntityId.Value.ToString() : "ALL")}";

            _db.WorkCalendars.Remove(cal);
            await _db.SaveChangesAsync();

            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = User.Identity?.Name ?? "Unknown",
                Action = "DELETE",
                EntityName = "WorkCalendars",
                RecordId = id.ToString(),
                Timestamp = DateTime.UtcNow,
                Details = $"Deleted calendar {describe}."
            });
            await _db.SaveChangesAsync();

            TempData["Success"] = "Calendar deleted.";
            return RedirectToAction(nameof(Index), new { year });
        }
    }
}
