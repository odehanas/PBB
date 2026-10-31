using System;

namespace GovBudget.Models;

/// <summary>
/// Header/snapshot for an allocation execution. Standard reports read the latest Posted run;
/// Scenario and Superseded runs are retained in full and can be compared in Management Review.
/// </summary>
public partial class AllocationRuns
{
    public int RunId { get; set; }

    public int BudgetYear { get; set; }

    public int? EntityId { get; set; }

    public string Period { get; set; } = "Annual";

    /// <summary>Draft | Posted | Superseded | Scenario</summary>
    public string Status { get; set; } = "Draft";

    /// <summary>Management label for the run, e.g. "Headcount basis". Optional.</summary>
    public string? ScenarioName { get; set; }

    /// <summary>StepDown | Reciprocal</summary>
    public string Method { get; set; } = "StepDown";

    public DateTime RunAt { get; set; }

    public string? RunBy { get; set; }

    public string? Notes { get; set; }

    public bool ReconciledOk { get; set; }
}
