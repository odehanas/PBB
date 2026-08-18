using System;
using System.Collections.Generic;

namespace GovBudget.Models;

public partial class HrEmployeeCosts
{
    public int EmployeeCostId { get; set; }

    public int BudgetYear { get; set; }

    public string EmployeeId { get; set; } = null!;

    public string EmployeeName { get; set; } = null!;

    public string? Occupation { get; set; }

    public string GLCode { get; set; } = null!;

    public string GLKind { get; set; } = null!;

    public int? EntityId { get; set; }

    public string EntityName { get; set; } = null!;

    public int? DepartmentId { get; set; }

    public string DepartmentName { get; set; } = null!;

    public decimal AnnualCost { get; set; }

    public DateTime ImportedAt { get; set; }

    public string? ImportedBy { get; set; }

    public string? SourceFile { get; set; }

    public virtual Departments? Department { get; set; }

    public virtual Entities? Entity { get; set; }

    public virtual ICollection<HrEmployeeCostAllocations> HrEmployeeCostAllocations { get; set; } = new List<HrEmployeeCostAllocations>();
}
