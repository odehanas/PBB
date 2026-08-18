using System;

namespace GovBudget.Models;

/// <summary>Header/snapshot for an allocation execution. Reports read Posted runs only.</summary>
public partial class AllocationRuns
{
    public int RunId { get; set; }

    public int BudgetYear { get; set; }

    public int? EntityId { get; set; }

    public string Period { get; set; } = "Annual";

    /// <summary>Draft | Posted | Superseded</summary>
    public string Status { get; set; } = "Draft";

    /// <summary>StepDown | Reciprocal</summary>
    public string Method { get; set; } = "StepDown";

    public DateTime RunAt { get; set; }

    public string? RunBy { get; set; }

    public string? Notes { get; set; }

    public bool ReconciledOk { get; set; }
}
