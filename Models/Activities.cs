using System;
using System.Collections.Generic;

namespace GovBudget.Models;

public partial class Activities
{
    public int ActivityId { get; set; }

    public int ProgramId { get; set; }

    public int DepartmentId { get; set; }

    public string ActivityCode { get; set; } = null!;

    public string ActivityName { get; set; } = null!;

    public bool IsActive { get; set; }

    public virtual ICollection<BudgetLines> BudgetLines { get; set; } = new List<BudgetLines>();

    public virtual Departments Department { get; set; } = null!;

    public virtual Programs Program { get; set; } = null!;
}
