using System;

namespace GovBudget.Models;

/// <summary>
/// Immutable result of an allocation run: a single posting moving cost from a Support
/// program to a Mandate program, with the basis used (for full traceability).
/// </summary>
public partial class AllocationTransactions
{
    public long TxnId { get; set; }

    public int RunId { get; set; }

    public int BudgetYear { get; set; }

    public string Period { get; set; } = "Annual";

    public int EntityId { get; set; }

    public int SourceProgramId { get; set; }

    public int? SourceActivityId { get; set; }

    public string? SourceCategoryCode { get; set; }

    public int TargetProgramId { get; set; }

    public int? TargetActivityId { get; set; }

    public int? DriverId { get; set; }

    public decimal BasisValue { get; set; }

    public decimal BasisTotal { get; set; }

    public decimal AllocationPct { get; set; }

    public decimal Amount { get; set; }

    public DateTime CreatedAt { get; set; }
}
