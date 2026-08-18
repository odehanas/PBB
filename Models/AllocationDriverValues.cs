using System;

namespace GovBudget.Models;

/// <summary>Driver measurement per target program/activity for a budget year (e.g. headcount = 20).</summary>
public partial class AllocationDriverValues
{
    public int DriverValueId { get; set; }

    public int DriverId { get; set; }

    public int BudgetYear { get; set; }

    public int TargetProgramId { get; set; }

    public int? TargetActivityId { get; set; }

    public decimal Value { get; set; }
}
