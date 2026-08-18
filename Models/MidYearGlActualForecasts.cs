using System;

namespace GovBudget.Models;

public partial class MidYearGlActualForecasts
{
    public long MidYearId { get; set; }

    public int BudgetYear { get; set; }

    public int EntityId { get; set; }

    public string GLCode { get; set; } = null!;

    public string GLType { get; set; } = null!;

    public decimal ActualH1Amount { get; set; }

    public decimal? ForecastH2Amount { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime? ForecastUpdatedAt { get; set; }

    public string? ForecastUpdatedBy { get; set; }

    public string? SourceFile { get; set; }

    public virtual Entities Entity { get; set; } = null!;
}
