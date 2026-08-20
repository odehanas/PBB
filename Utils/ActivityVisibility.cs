using System;
using System.Linq;
using System.Linq.Expressions;
using GovBudget.Models;

namespace GovBudget.Utils
{
    /// <summary>
    /// One rule for whether an activity is shown outside the activity master screen.
    ///
    /// An activity marked inactive is hidden everywhere - reports, hierarchy trees and pick
    /// lists - but ONLY once nothing points at it any more. An inactive activity that still
    /// carries budget lines, HR allocations, KPIs, outputs, submitted lines or allocation
    /// postings keeps showing, because dropping it would silently remove its cost from the
    /// totals and the reports would stop reconciling to the budget.
    ///
    /// Activities/InactiveLinks is the worklist for clearing those links; the moment an
    /// activity is clean, this rule hides it with no further action.
    /// </summary>
    public static class ActivityVisibility
    {
        public static Expression<Func<Activities, bool>> IsVisible(GovBudgetContext db) => a =>
            a.IsActive
            || db.BudgetLines.Any(b => b.ActivityId == a.ActivityId)
            || db.HrEmployeeCostAllocations.Any(h => h.ActivityId == a.ActivityId)
            || db.BudgetSubmissionLines.Any(s => s.ActivityId == a.ActivityId)
            || db.Kpis.Any(k => k.ActivityId == a.ActivityId)
            || db.ActivityOutputs.Any(o => o.ActivityId == a.ActivityId)
            || db.AllocationTransactions.Any(t => t.SourceActivityId == a.ActivityId || t.TargetActivityId == a.ActivityId)
            || db.AllocationRules.Any(r => r.SourceActivityId == a.ActivityId)
            || db.AllocationRuleTargets.Any(rt => rt.TargetActivityId == a.ActivityId);

        /// <summary>Activities that may appear in reports, trees and pick lists.</summary>
        public static IQueryable<Activities> VisibleActivities(this GovBudgetContext db)
            => db.Activities.Where(IsVisible(db));
    }
}
