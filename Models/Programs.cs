using System;
using System.Collections.Generic;

namespace GovBudget.Models;

public partial class Programs
{
    public int ProgramId { get; set; }

    public int EntityId { get; set; }

    public string ProgramCode { get; set; } = null!;

    public string ProgramName { get; set; } = null!;

    public bool IsActive { get; set; }

    /// <summary>Mandate (core delivery) or Support (admin/back-office). Default Mandate.</summary>
    public string ProgramType { get; set; } = "Mandate";

    /// <summary>Step-down processing order for Support programs (lower runs first).</summary>
    public int? AllocationSequence { get; set; }

    public virtual ICollection<Activities> Activities { get; set; } = new List<Activities>();

    public virtual ICollection<BudgetLines> BudgetLines { get; set; } = new List<BudgetLines>();

    public virtual Entities Entity { get; set; } = null!;
}
