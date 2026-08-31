-- ==========================================================================
-- GovBudget - Employee cost per hour: the rate view
-- --------------------------------------------------------------------------
-- WHY A VIEW AND NOT A TABLE
--   The rate is DERIVED from data you already maintain. A view is computed at
--   read time, so it can never go stale: re-import HR costs, change an annual
--   salary, edit a calendar, and the rate is correct on the next query. There
--   is no refresh job, no sync step, nothing to remember. The only rows you
--   maintain by hand are the handful in core.WorkCalendars plus any genuine
--   per-employee exceptions in core.HrEmployeeHoursOverride.
--
--   (If you later need to FREEZE rates against an approved budget for audit,
--   add a snapshot table then and populate it from this view. Don't do it
--   now - a live view is the zero-maintenance option.)
--
-- WHICH RATE TO USE FOR COSTING: StandardRatePerHour
--   Annual leave and public holidays are PAID, so their cost is already inside
--   AnnualCost. Dividing by contracted hours (2,080) would spread salary over
--   hours that include paid absence, so multiplying activity hours back by
--   that rate recovers only ~86% of salary - the paid leave becomes stranded
--   cost belonging to activities but appearing nowhere, and every activity is
--   under-costed by roughly 15%.
--
--   Dividing by PRODUCTIVE hours absorbs the paid leave into the rate, so an
--   hour of real work carries its true share of what you paid. This preserves
--   the invariant the system already enforces: allocations sum to 100% of
--   annual cost. It is standard fully-loaded labour rate practice.
--
--   NominalRatePerHour is published alongside it for transparency only - it is
--   the figure an HR officer expects to see, and showing both makes the
--   treatment auditable instead of hidden inside a formula. Do not cost with it.
--
-- IMPACT ON THE RUNNING SYSTEM: NONE. Nothing reads this view until you point
--   a report or screen at it. Dropping it leaves no trace.
--
-- HOW TO RUN
--   Run AddHrHourlyRate_Tables.sql first. Then paste this file on its own and
--   run it. It is a single statement, so it needs no GO and no EXEC wrapper.
-- ==========================================================================

CREATE OR ALTER VIEW core.vw_HrEmployeeHourlyRates
AS
SELECT
    h.EmployeeCostId,
    h.BudgetYear,
    h.EmployeeId,
    h.EmployeeName,
    h.Occupation,
    h.EntityId,
    h.EntityName,
    h.DepartmentId,
    h.DepartmentName,
    h.AnnualCost,

    -- Which calendar was applied (NULL = no calendar set up for that year)
    cal.CalendarId,
    cal.CalendarName,
    cal.HoursPerDay,
    cal.WorkDaysPerWeek,

    -- Hour build-up, fully transparent so the rate can be audited
    calc.GrossPaidHours,
    calc.HolidayHours,
    calc.LeaveHours,
    calc.OtherAbsenceHours,
    calc.ProductiveHours,
    ovr.ProductiveHoursPerYear      AS OverrideHours,
    eff.EffectiveHours,

    -- The costing rate: annual cost spread over hours actually available
    CAST(h.AnnualCost / NULLIF(eff.EffectiveHours, 0) AS decimal(18, 4))
        AS StandardRatePerHour,

    -- Reference only. Understates the cost of an hour of work.
    CAST(h.AnnualCost / NULLIF(calc.GrossPaidHours, 0) AS decimal(18, 4))
        AS NominalRatePerHour,

    -- Budgeted vacant posts carry a part-year cost, so their rate is
    -- meaningless. Filter these out of averages and occupation reporting.
    CASE
        WHEN h.EmployeeName LIKE N'%Vacan%' OR h.EmployeeId LIKE N'V.%'
        THEN CAST(1 AS bit) ELSE CAST(0 AS bit)
    END AS IsVacancy,

    -- 1 when the figure is trustworthy: a calendar resolved and hours > 0.
    CASE
        WHEN eff.EffectiveHours > 0 THEN CAST(1 AS bit) ELSE CAST(0 AS bit)
    END AS IsRateAvailable

FROM core.HrEmployeeCosts h

-- Entity-specific calendar wins; otherwise fall back to the year's default
-- row (EntityId IS NULL). TOP 1 with the ORDER BY makes that precedence explicit.
OUTER APPLY
(
    SELECT TOP 1
        c.CalendarId, c.CalendarName, c.HoursPerDay, c.WorkDaysPerWeek,
        c.WeeksPerYear, c.PublicHolidayDays, c.AnnualLeaveDays,
        c.OtherPaidAbsenceDays, c.UtilisationPct
    FROM core.WorkCalendars c
    WHERE c.BudgetYear = h.BudgetYear
      AND c.IsActive = 1
      AND (c.EntityId = h.EntityId OR c.EntityId IS NULL)
    ORDER BY CASE WHEN c.EntityId IS NULL THEN 1 ELSE 0 END
) cal

LEFT JOIN core.HrEmployeeHoursOverride ovr
    ON  ovr.BudgetYear = h.BudgetYear
    AND ovr.EmployeeId = h.EmployeeId

CROSS APPLY
(
    SELECT
        GrossPaidHours    = CAST(cal.WeeksPerYear * cal.WorkDaysPerWeek * cal.HoursPerDay AS decimal(9, 2)),
        HolidayHours      = CAST(cal.PublicHolidayDays    * cal.HoursPerDay AS decimal(9, 2)),
        LeaveHours        = CAST(cal.AnnualLeaveDays      * cal.HoursPerDay AS decimal(9, 2)),
        OtherAbsenceHours = CAST(cal.OtherPaidAbsenceDays * cal.HoursPerDay AS decimal(9, 2)),
        ProductiveHours   = CAST(
                                ((cal.WeeksPerYear * cal.WorkDaysPerWeek)
                                  - cal.PublicHolidayDays
                                  - cal.AnnualLeaveDays
                                  - cal.OtherPaidAbsenceDays)
                                * cal.HoursPerDay
                                * (cal.UtilisationPct / 100.0)
                            AS decimal(9, 2))
) calc

CROSS APPLY
(
    SELECT EffectiveHours = COALESCE(ovr.ProductiveHoursPerYear, calc.ProductiveHours)
) eff;
