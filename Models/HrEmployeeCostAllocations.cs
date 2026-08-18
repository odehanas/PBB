using System;

namespace GovBudget.Models;

public partial class HrEmployeeCostAllocations
{
    public long AllocationId { get; set; }

    public int EmployeeCostId { get; set; }

    public int ActivityId { get; set; }

    public int? ProjectId { get; set; }

    public decimal AllocatedAmount { get; set; }

    public DateTime CreatedAt { get; set; }

    public string CreatedBy { get; set; } = null!;

    public virtual Activities Activity { get; set; } = null!;

    public virtual HrEmployeeCosts EmployeeCost { get; set; } = null!;

    public virtual Projects? Project { get; set; }
}

