namespace GovBudget.Models;

public class AllocationVarianceRow
{
    public int EmployeeCostId { get; set; }
    public string EmployeeId { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string EntityName { get; set; } = "";
    public string DepartmentName { get; set; } = "";
    public decimal AnnualCost { get; set; }
    public decimal Allocated { get; set; }
    public int AllocationCount { get; set; }

    public decimal Variance => AnnualCost - Allocated;

    // Positive => under-allocated (room remaining); Negative => over-allocated.
    public decimal AllocatedPct => AnnualCost <= 0m ? 0m : System.Math.Round((Allocated / AnnualCost) * 100m, 2);

    public string Status
    {
        get
        {
            if (Allocated <= 0m) return "Unallocated";
            if (Variance < 0m) return "Over-allocated";
            return "Under-allocated";
        }
    }
}
