using System;

namespace GovBudget.Models;

/// <summary>
/// Read-only projection of core.vw_HrEmployeeHourlyRates: the standard (fully
/// loaded) hourly cost per employee, derived from HrEmployeeCosts.AnnualCost
/// and the work calendar in core.WorkCalendars.
///
/// Everything below the annual cost comes from the view's arithmetic, so this
/// type is never written to. The calendar columns are nullable because an
/// employee whose budget year has no calendar row still appears in the report -
/// with IsRateAvailable = false - rather than silently vanishing.
/// </summary>
public partial class vw_HrEmployeeHourlyRates
{
    public int EmployeeCostId { get; set; }

    public int BudgetYear { get; set; }

    public string EmployeeId { get; set; } = null!;

    public string EmployeeName { get; set; } = null!;

    public string? Occupation { get; set; }

    public int? EntityId { get; set; }

    public string EntityName { get; set; } = null!;

    public int? DepartmentId { get; set; }

    public string DepartmentName { get; set; } = null!;

    public decimal AnnualCost { get; set; }

    // Which calendar resolved for this employee (entity-specific, else the
    // year's default row). Null when no calendar exists for the year.
    public int? CalendarId { get; set; }

    public string? CalendarName { get; set; }

    public decimal? HoursPerDay { get; set; }

    public decimal? WorkDaysPerWeek { get; set; }

    // ---- Hour build-up, kept visible so the rate can be audited ----

    public decimal? GrossPaidHours { get; set; }

    public decimal? HolidayHours { get; set; }

    public decimal? LeaveHours { get; set; }

    public decimal? OtherAbsenceHours { get; set; }

    public decimal? ProductiveHours { get; set; }

    /// <summary>Per-employee override from core.HrEmployeeHoursOverride, when one exists.</summary>
    public decimal? OverrideHours { get; set; }

    /// <summary>Override if set, otherwise the calendar's productive hours. The rate divides by this.</summary>
    public decimal? EffectiveHours { get; set; }

    /// <summary>Costing rate: annual cost over hours actually available for activity work.</summary>
    public decimal? StandardRatePerHour { get; set; }

    /// <summary>Reference only: annual cost over contracted hours. Understates the cost of an hour worked.</summary>
    public decimal? NominalRatePerHour { get; set; }

    /// <summary>Budgeted vacant post - carries a part-year cost, so its rate is meaningless.</summary>
    public bool? IsVacancy { get; set; }

    /// <summary>False when no calendar resolved or hours came to zero.</summary>
    public bool? IsRateAvailable { get; set; }
}
