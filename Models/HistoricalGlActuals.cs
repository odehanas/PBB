using System;

namespace GovBudget.Models;

public partial class HistoricalGlActuals
{
    public long HistoricalActualId { get; set; }

    public int BudgetYear { get; set; }

    public int EntityId { get; set; }

    public int DepartmentId { get; set; }

    public string GLCode { get; set; } = null!;

    public string? GLType { get; set; }

    public decimal Amount { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public string? SourceFile { get; set; }

    public virtual Entities Entity { get; set; } = null!;

    public virtual Departments Department { get; set; } = null!;
}
