using System;
using System.Collections.Generic;

namespace GovBudget.Models;

public partial class Categories
{
    public int CategoryId { get; set; }

    public string CategoryCode { get; set; } = null!;

    public string CategoryName { get; set; } = null!;

    public virtual ICollection<BudgetLines> BudgetLines { get; set; } = new List<BudgetLines>();
}
