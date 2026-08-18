using System;

namespace GovBudget.Models;

public partial class ActivityOutputs
{
    public long ActivityOutputId { get; set; }

    public int ActivityId { get; set; }

    public int BudgetYear { get; set; }

    public string OutputMeasure { get; set; } = null!;

    public decimal OutputVolume { get; set; }

    public bool IsPrimary { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public virtual Activities Activity { get; set; } = null!;
}
