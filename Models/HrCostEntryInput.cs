namespace GovBudget.Models;

/// <summary>Form values for typing one employee's annual HR cost by hand (instead of the Excel import).</summary>
public class HrCostEntryInput
{
    public int BudgetYear { get; set; }

    public string? EmployeeId { get; set; }

    public string? EmployeeName { get; set; }

    public string? Occupation { get; set; }

    // Ignored on the budget (cost-centre) screen, where the working context decides both.
    public int? EntityId { get; set; }

    public int? DepartmentId { get; set; }

    public decimal? AnnualCost { get; set; }

    public string? GLCode { get; set; }

    public string? GLKind { get; set; } = "HR";
}

/// <summary>A cost centre on the manual-entry form, tagged with its entity so the list can be narrowed.</summary>
public class HrCostDepartmentOption
{
    public int DepartmentId { get; set; }

    public int EntityId { get; set; }

    public string Label { get; set; } = "";
}
