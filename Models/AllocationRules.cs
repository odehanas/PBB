using System;
using System.Collections.Generic;

namespace GovBudget.Models;

/// <summary>
/// Configuration of how a Support program's cost pool is reallocated to Mandate programs.
/// </summary>
public partial class AllocationRules
{
    public int RuleId { get; set; }

    public int BudgetYear { get; set; }

    public int EntityId { get; set; }

    public int SourceProgramId { get; set; }

    public int? SourceActivityId { get; set; }

    /// <summary>Percentage | Headcount | Driver | Equal</summary>
    public string Method { get; set; } = "Equal";

    public int? DriverId { get; set; }

    /// <summary>Comma-separated category codes to reallocate (default OPEX,HR).</summary>
    public string CategoryScopeCsv { get; set; } = "OPEX,HR";

    /// <summary>AllMandate | Explicit</summary>
    public string TargetScope { get; set; } = "AllMandate";

    /// <summary>Share of the source pool to push out (default 100%).</summary>
    public decimal SourcePercent { get; set; } = 100m;

    public int Sequence { get; set; } = 100;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public virtual ICollection<AllocationRuleTargets> Targets { get; set; } = new List<AllocationRuleTargets>();
}
