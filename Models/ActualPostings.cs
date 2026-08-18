using System;

namespace GovBudget.Models;

/// <summary>
/// Current-year actual postings imported from SAP (GL view), monthly grain.
/// Reliable at GL / Category / Item; Activity/Project/Department are derived
/// in the reporting layer (except HR, which is exact via allocation rate).
/// </summary>
public partial class ActualPostings
{
    public long ActualPostingId { get; set; }

    public int BudgetYear { get; set; }

    public byte PeriodMonth { get; set; }

    public int EntityId { get; set; }

    public string GLCode { get; set; } = null!;

    public string? GLType { get; set; }

    public int? ItemId { get; set; }

    public string? ItemCode { get; set; }

    public decimal Amount { get; set; }

    public string Source { get; set; } = null!;

    public long? ImportBatchId { get; set; }

    public string? SourceFile { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public virtual Entities Entity { get; set; } = null!;

    public virtual Items? Item { get; set; }

    public virtual ActualImportBatches? ImportBatch { get; set; }
}
