using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GovBudget.Models;

/// <summary>
/// Working-time variables used to turn an employee's annual cost into a cost
/// per hour. One row per (BudgetYear, EntityId); EntityId null is the default
/// that applies to every entity without a row of its own.
///
/// Nothing in budget entry, HR import or cost allocation reads this - it feeds
/// core.vw_HrEmployeeHourlyRates and nothing else.
/// </summary>
public partial class WorkCalendars
{
    public int CalendarId { get; set; }

    [Display(Name = "Budget Year")]
    [Range(2000, 2100, ErrorMessage = "Enter a four-digit year between 2000 and 2100.")]
    public int BudgetYear { get; set; }

    /// <summary>Null = the default calendar for every entity in that year.</summary>
    [Display(Name = "Entity")]
    public int? EntityId { get; set; }

    [Required(ErrorMessage = "Give the calendar a name.")]
    [StringLength(100)]
    [Display(Name = "Calendar Name")]
    public string CalendarName { get; set; } = null!;

    [Display(Name = "Hours per Day")]
    [Range(0.5, 24, ErrorMessage = "Hours per day must be between 0.5 and 24.")]
    public decimal HoursPerDay { get; set; } = 8.00m;

    [Display(Name = "Work Days per Week")]
    [Range(1, 7, ErrorMessage = "Work days per week must be between 1 and 7.")]
    public decimal WorkDaysPerWeek { get; set; } = 5.00m;

    [Display(Name = "Weeks per Year")]
    [Range(1, 53, ErrorMessage = "Weeks per year must be between 1 and 53.")]
    public decimal WeeksPerYear { get; set; } = 52.00m;

    [Display(Name = "Public Holidays (days)")]
    [Range(0, 200, ErrorMessage = "Public holidays must be between 0 and 200 days.")]
    public decimal PublicHolidayDays { get; set; } = 14.00m;

    [Display(Name = "Annual Leave (days)")]
    [Range(0, 200, ErrorMessage = "Annual leave must be between 0 and 200 days.")]
    public decimal AnnualLeaveDays { get; set; } = 22.00m;

    [Display(Name = "Other Paid Absence (days)")]
    [Range(0, 200, ErrorMessage = "Other paid absence must be between 0 and 200 days.")]
    public decimal OtherPaidAbsenceDays { get; set; } = 0.00m;

    [Display(Name = "Utilisation %")]
    [Range(1, 100, ErrorMessage = "Utilisation must be between 1 and 100 percent.")]
    public decimal UtilisationPct { get; set; } = 100.00m;

    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }

    public virtual Entities? Entity { get; set; }

    // ---- Derived figures, mirroring the arithmetic in core.vw_HrEmployeeHourlyRates.
    // Shown on the admin screen so the effect of a change is visible before saving.

    /// <summary>Contracted hours in the year, before any deduction.</summary>
    [NotMapped]
    [Display(Name = "Gross Paid Hours")]
    public decimal GrossPaidHours => Math.Round(WeeksPerYear * WorkDaysPerWeek * HoursPerDay, 2);

    /// <summary>
    /// Hours actually available for activity work. Paid holidays and leave come
    /// out because their cost is already inside the annual salary - leaving them
    /// in would spread salary across hours nobody works.
    /// </summary>
    [NotMapped]
    [Display(Name = "Productive Hours")]
    public decimal ProductiveHours => Math.Round(
        ((WeeksPerYear * WorkDaysPerWeek) - PublicHolidayDays - AnnualLeaveDays - OtherPaidAbsenceDays)
        * HoursPerDay * (UtilisationPct / 100m), 2);

    /// <summary>Working days in the year after paid absence is removed.</summary>
    [NotMapped]
    [Display(Name = "Productive Days")]
    public decimal ProductiveDays => Math.Round(
        (WeeksPerYear * WorkDaysPerWeek) - PublicHolidayDays - AnnualLeaveDays - OtherPaidAbsenceDays, 2);
}
