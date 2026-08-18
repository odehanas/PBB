using System;

namespace GovBudget.Models;

/// <summary>
/// Cross-entity editorial narrative for the management review deck:
/// Headline Findings, Recommendations (executive decisions), and the 90-Day Plan.
/// Additive and isolated; not tied to a single entity.
/// </summary>
public partial class ReviewNarratives
{
    public int ReviewNarrativeId { get; set; }

    public int BudgetYear { get; set; }

    public string Period { get; set; } = "MidYear";

    /// <summary>Finding | Recommendation | Action</summary>
    public string Section { get; set; } = "Finding";

    public string? Title { get; set; }

    public string? Body { get; set; }

    public string? Owner { get; set; }

    public string? DueText { get; set; }

    public string? SuccessMeasure { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? CreatedBy { get; set; }
}
