using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GovBudget.Models;
using GovBudget.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Controllers
{
    [Authorize]
    public class MidYearController : Controller
    {
        private readonly GovBudgetContext _db;

        public MidYearController(GovBudgetContext db)
        {
            _db = db;
        }

        [HttpGet]
        public async Task<IActionResult> Index(int? year = null)
        {
            var selectedYear = year ?? HttpContext.Session.GetInt("ctxYear") ?? DateTime.Now.Year;
            var entityId = HttpContext.Session.GetInt("ctxEntityId");
            if (!entityId.HasValue)
            {
                var returnUrl = Url.Action(nameof(Index), "MidYear", new { year = selectedYear });
                return RedirectToAction("Select", "Context", new { returnUrl });
            }

            var entity = await _db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.EntityId == entityId.Value);
            var entityLabel = entity == null ? $"Entity {entityId.Value}" : $"{entity.EntityCode} - {entity.EntityName}";

            var rows = await _db.MidYearGlActualForecasts.AsNoTracking()
                .Where(x => x.BudgetYear == selectedYear && x.EntityId == entityId.Value)
                .OrderBy(x => x.GLType)
                .ThenBy(x => x.GLCode)
                .Select(x => new MidYearRowVm
                {
                    MidYearId = x.MidYearId,
                    GLCode = x.GLCode,
                    GLType = x.GLType,
                    ActualH1Amount = x.ActualH1Amount,
                    ForecastH2Amount = x.ForecastH2Amount
                })
                .ToListAsync();

            var vm = new MidYearIndexVm
            {
                Year = selectedYear,
                EntityLabel = entityLabel,
                Rows = rows
            };

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(MidYearSaveVm model)
        {
            var selectedYear = HttpContext.Session.GetInt("ctxYear") ?? model.Year;
            var entityId = HttpContext.Session.GetInt("ctxEntityId");
            if (!entityId.HasValue)
            {
                var returnUrl = Url.Action(nameof(Index), "MidYear", new { year = selectedYear });
                return RedirectToAction("Select", "Context", new { returnUrl });
            }

            var userName = User.Identity?.Name ?? "Unknown";
            var ids = (model.Rows ?? new List<MidYearSaveRowVm>()).Select(r => r.MidYearId).Distinct().ToList();
            if (ids.Count == 0)
            {
                TempData["Error"] = "No rows submitted.";
                return RedirectToAction(nameof(Index), new { year = selectedYear });
            }

            var dbRows = await _db.MidYearGlActualForecasts
                .Where(x => x.BudgetYear == selectedYear && x.EntityId == entityId.Value && ids.Contains(x.MidYearId))
                .ToListAsync();

            var byId = dbRows.ToDictionary(x => x.MidYearId, x => x);
            var updated = 0;

            foreach (var row in model.Rows ?? new List<MidYearSaveRowVm>())
            {
                if (!byId.TryGetValue(row.MidYearId, out var ex))
                {
                    continue;
                }

                if (row.ForecastH2Amount.HasValue && row.ForecastH2Amount.Value < 0)
                {
                    continue;
                }

                ex.ForecastH2Amount = row.ForecastH2Amount;
                ex.ForecastUpdatedAt = DateTime.UtcNow;
                ex.ForecastUpdatedBy = userName;
                updated++;
            }

            await _db.SaveChangesAsync();
            TempData["Success"] = $"Saved {updated} forecast values.";

            return RedirectToAction(nameof(Index), new { year = selectedYear });
        }

        public class MidYearIndexVm
        {
            public int Year { get; set; }
            public string EntityLabel { get; set; } = "";
            public List<MidYearRowVm> Rows { get; set; } = new List<MidYearRowVm>();
        }

        public class MidYearRowVm
        {
            public long MidYearId { get; set; }
            public string GLCode { get; set; } = "";
            public string GLType { get; set; } = "";
            public decimal ActualH1Amount { get; set; }
            public decimal? ForecastH2Amount { get; set; }
        }

        public class MidYearSaveVm
        {
            public int Year { get; set; }
            public List<MidYearSaveRowVm>? Rows { get; set; }
        }

        public class MidYearSaveRowVm
        {
            public long MidYearId { get; set; }
            public decimal? ForecastH2Amount { get; set; }
        }
    }
}
