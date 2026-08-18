namespace GovBudget.Models;

public class SubmissionVarianceRow
{
    public string Item { get; set; } = "";

    public int? ProgramId { get; set; }

    public string Activity { get; set; } = "";

    public string Project { get; set; } = "";

    public string Description { get; set; } = "";

    public decimal OldAmount { get; set; }

    public decimal NewAmount { get; set; }

    public decimal Delta => NewAmount - OldAmount;
}

