using System;

namespace GovBudget.Models;

/// <summary>
/// Per-employee HR actual postings (monthly grain). Enables EXACT activity/project
/// attribution: employee actual x budgeted allocation rate (from HrEmployeeCostAllocations).
/// EmployeeCostId is a soft link to the budgeted employee, resolved at import from the HR code.
/// </summary>
public partial class HrActualPostings
{
    public long HrActualPostingId { get; set; }

    public int BudgetYear { get; set; }

    public byte PeriodMonth { get; set; }

    public int EntityId { get; set; }

    public string EmployeeCode { get; set; } = null!;

    public int? EmployeeCostId { get; set; }

    public string? GLCode { get; set; }

    public decimal Amount { get; set; }

    public string Source { get; set; } = "HR_EMP";

    public long? ImportBatchId { get; set; }

    public string? SourceFile { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public virtual Entities Entity { get; set; } = null!;

    public virtual ActualImportBatches? ImportBatch { get; set; }
}
