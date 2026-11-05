using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GovBudget.Models;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Services
{
    // Mid-year actuals (stored under year - 1) stand in for an entity's actuals on the budget-vs-actual
    // screens until something real is imported for the budget year itself. An entity with any posted
    // actual or forecast-to-complete for that year never uses them, so the two are not added together.
    public static class MidYearActualsFallback
    {
        public sealed record Row(int EntityId, string GLCode, string GLType, decimal ActualH1, decimal? ForecastH2);

        // entityIds == null means every entity.
        public static async Task<List<Row>> LoadAsync(GovBudgetContext db, int year, IReadOnlyCollection<int>? entityIds)
        {
            var actualYear = year - 1;
            var q = db.MidYearGlActualForecasts.AsNoTracking().Where(m => m.BudgetYear == actualYear);
            if (entityIds != null) q = q.Where(m => entityIds.Contains(m.EntityId));

            return await q
                .Where(m => !db.ActualPostings.Any(p => p.BudgetYear == year && p.EntityId == m.EntityId)
                            && !db.HrActualPostings.Any(p => p.BudgetYear == year && p.EntityId == m.EntityId)
                            && !db.ActualForecasts.Any(f => f.BudgetYear == year && f.EntityId == m.EntityId))
                .Select(m => new Row(m.EntityId, m.GLCode, m.GLType, m.ActualH1Amount, m.ForecastH2Amount))
                .ToListAsync();
        }

        // True when something real has been imported for the year in this scope.
        public static async Task<bool> HasImportedAsync(GovBudgetContext db, int year, IReadOnlyCollection<int>? entityIds)
        {
            var postings = db.ActualPostings.AsNoTracking().Where(p => p.BudgetYear == year);
            var hr = db.HrActualPostings.AsNoTracking().Where(p => p.BudgetYear == year);
            var forecasts = db.ActualForecasts.AsNoTracking().Where(f => f.BudgetYear == year);
            if (entityIds != null)
            {
                postings = postings.Where(p => entityIds.Contains(p.EntityId));
                hr = hr.Where(p => entityIds.Contains(p.EntityId));
                forecasts = forecasts.Where(f => entityIds.Contains(f.EntityId));
            }
            return await postings.AnyAsync() || await hr.AnyAsync() || await forecasts.AnyAsync();
        }
    }
}
