using System;
using System.Collections.Generic;

namespace GovBudget.Models;

public partial class Entities
{
    public int EntityId { get; set; }

    public string EntityCode { get; set; } = null!;

    public string EntityName { get; set; } = null!;

    public bool IsActive { get; set; }

    public virtual ICollection<BudgetLines> BudgetLines { get; set; } = new List<BudgetLines>();

    public virtual ICollection<Departments> Departments { get; set; } = new List<Departments>();

    public virtual ICollection<Programs> Programs { get; set; } = new List<Programs>();
}
