using System;

namespace GovBudget.Models;

public partial class BudgetRevisionRequests
{
    public long RequestId { get; set; }

    public long SubmissionId { get; set; }

    public string ActionType { get; set; } = null!;

    public string? Note { get; set; }

    public DateTime RequestedAt { get; set; }

    public string? RequestedBy { get; set; }

    public virtual BudgetSubmissions Submission { get; set; } = null!;
}

