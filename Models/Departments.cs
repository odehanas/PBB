using System;
using System.Collections.Generic;

namespace GovBudget.Models;

public partial class Departments
{
    public int DepartmentId { get; set; }

    public int EntityId { get; set; }

    public string DeptCode { get; set; } = null!;

    public string DeptName { get; set; } = null!;

    public bool IsActive { get; set; }

    public virtual ICollection<Activities> Activities { get; set; } = new List<Activities>();

    public virtual ICollection<AppUsers> AppUsers { get; set; } = new List<AppUsers>();

    public virtual ICollection<BudgetLines> BudgetLines { get; set; } = new List<BudgetLines>();

    public virtual Entities Entity { get; set; } = null!;

    public virtual ICollection<Projects> Projects { get; set; } = new List<Projects>();
}
