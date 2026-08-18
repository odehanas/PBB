using System;

namespace GovBudget.Models;

/// <summary>Explicit target (and weight) for an allocation rule using the Percentage method.</summary>
public partial class AllocationRuleTargets
{
    public int RuleTargetId { get; set; }

    public int RuleId { get; set; }

    public int TargetProgramId { get; set; }

    public int? TargetActivityId { get; set; }

    public decimal Weight { get; set; }

    public virtual AllocationRules? Rule { get; set; }
}
