using System;

namespace GovBudget.Models;

/// <summary>
/// Manual forecast-to-complete for the not-yet-actualised remainder of the year
/// (months AsOfMonth+1..12), one figure per GL/Entity/Year. Reuses the mid-year
/// forecast concept so full-year = actual YTD + ForecastRemaining.
/// </summary>
public partial class ActualForecasts
{
    public long ActualForecastId { get; set; }

    public int BudgetYear { get; set; }

    public int EntityId { get; set; }

    public string GLCode { get; set; } = null!;

    public string? GLType { get; set; }

    public byte AsOfMonth { get; set; }

    public decimal ForecastRemaining { get; set; }

    public string? Notes { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }

    public virtual Entities Entity { get; set; } = null!;
}
