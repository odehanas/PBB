using System;
using System.Collections.Generic;

namespace GovBudget.Models;

public partial class BudgetSubmissions
{
    public long SubmissionId { get; set; }

    public int BudgetYear { get; set; }

    public int EntityId { get; set; }

    public int DepartmentId { get; set; }

    public int CategoryId { get; set; }

    public int VersionNo { get; set; }

    public long? ParentSubmissionId { get; set; }

    public string Status { get; set; } = null!;

    public DateTime? SubmittedAt { get; set; }

    public string? SubmittedBy { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public string? ApprovedBy { get; set; }

    public string? ApprovalNote { get; set; }

    public DateTime? ReturnedAt { get; set; }

    public string? ReturnedBy { get; set; }

    public string? ReturnNote { get; set; }

    public DateTime? SentToCentralAt { get; set; }

    public string? SentToCentralBy { get; set; }

    public DateTime? FinalizedAt { get; set; }

    public string? FinalizedBy { get; set; }

    public DateTime? SysApprovedAt { get; set; }

    public string? SysApprovedBy { get; set; }

    public string? SysApprovalNote { get; set; }

    public virtual Categories Category { get; set; } = null!;

    public virtual Departments Department { get; set; } = null!;

    public virtual Entities Entity { get; set; } = null!;
}
