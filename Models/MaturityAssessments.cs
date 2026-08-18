using System;

namespace GovBudget.Models;

public partial class MaturityAssessments
{
    public int MaturityAssessmentId { get; set; }

    public int EntityId { get; set; }

    public int BudgetYear { get; set; }

    public string Period { get; set; } = "MidYear";

    public decimal Stage { get; set; }

    public string? Form { get; set; }

    public string? StatusLabel { get; set; }

    public string? Notes { get; set; }

    public DateTime AssessedAt { get; set; }

    public string? AssessedBy { get; set; }

    public virtual Entities Entity { get; set; } = null!;
}
