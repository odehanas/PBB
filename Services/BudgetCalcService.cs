using System;
using GovBudget.Models;

namespace GovBudget.Services
{
    public static class BudgetCalcService
    {
        /// <summary>
        /// Split an amount equally into 12 months, rounding to cents.
        /// The last month gets the rounding remainder.
        /// </summary>
        public static (decimal m1, decimal m2, decimal m3, decimal m4, decimal m5, decimal m6,
                       decimal m7, decimal m8, decimal m9, decimal m10, decimal m11, decimal m12)
            Equal12(decimal amount)
        {
            var basePart = Math.Round(amount / 12m, 2, MidpointRounding.AwayFromZero);
            // 11 months equal, last month gets remainder
            decimal m1 = basePart, m2 = basePart, m3 = basePart, m4 = basePart, m5 = basePart, m6 = basePart;
            decimal m7 = basePart, m8 = basePart, m9 = basePart, m10 = basePart, m11 = basePart;
            decimal first11 = basePart * 11m;
            decimal m12 = Math.Round(amount - first11, 2, MidpointRounding.AwayFromZero);
            return (m1, m2, m3, m4, m5, m6, m7, m8, m9, m10, m11, m12);
        }

        /// <summary>
        /// If percent is provided and amount is zero, compute amount from baseAmount.
        /// Otherwise keep amount as entered.
        /// </summary>
        public static decimal ComputeForecast(decimal baseAmount, decimal percent, decimal amount)
        {
            if (amount > 0) return Math.Round(amount, 2, MidpointRounding.AwayFromZero);
            if (percent != 0) return Math.Round(baseAmount * (1m + (percent / 100m)), 2, MidpointRounding.AwayFromZero);
            return Math.Round(baseAmount, 2, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Sum of 12 monthly buckets (manual mode validation).
        /// </summary>
        public static decimal SumMonths(BudgetLines bl) =>
            (bl.M01 + bl.M02 + bl.M03 + bl.M04 + bl.M05 + bl.M06 +
             bl.M07 + bl.M08 + bl.M09 + bl.M10 + bl.M11 + bl.M12);

        /// <summary>
        /// For CAPEX: ensure defaults exist even if user forgets.
        /// </summary>
        public static void EnsureCapexDefaults(BudgetLines bl)
        {
            if (string.IsNullOrWhiteSpace(bl.Dep_Method))
                bl.Dep_Method = "STRAIGHT";
            if (bl.Dep_StartMonth < 1 || bl.Dep_StartMonth > 12)
                bl.Dep_StartMonth = 1;
            if (bl.Dep_LifeMonths < 0) bl.Dep_LifeMonths = 0;
        }
    }
}
