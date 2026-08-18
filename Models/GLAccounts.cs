using System;
using System.Collections.Generic;

namespace GovBudget.Models;

public partial class GLAccounts
{
    public int GLAccountId { get; set; }

    public string GLCode { get; set; } = null!;

    public string GLName { get; set; } = null!;

    public string GLType { get; set; } = null!;

    public virtual ICollection<Items> Items { get; set; } = new List<Items>();
}
