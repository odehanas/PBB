using System;
using System.Collections.Generic;

namespace GovBudget.Models;

public partial class Items
{
    public int ItemId { get; set; }

    public string ItemCode { get; set; } = null!;

    public string ItemName { get; set; } = null!;

    public int GLAccountId { get; set; }

    public bool IsActive { get; set; }

    public virtual ICollection<BudgetLines> BudgetLines { get; set; } = new List<BudgetLines>();

    public virtual GLAccounts GLAccount { get; set; } = null!;
}
