using System;
using System.Collections.Generic;

namespace GovBudget.Models;

public partial class Projects
{
    public int ProjectId { get; set; }

    public string ProjectCode { get; set; } = null!;

    public string ProjectName { get; set; } = null!;

    public int? OwningDepartmentId { get; set; }

    public bool IsActive { get; set; }

    public virtual ICollection<BudgetLines> BudgetLines { get; set; } = new List<BudgetLines>();

    public virtual Departments? OwningDepartment { get; set; }
}
