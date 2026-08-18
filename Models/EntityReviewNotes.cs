using System;

namespace GovBudget.Models;

public partial class EntityReviewNotes
{
    public int EntityReviewNoteId { get; set; }

    public int EntityId { get; set; }

    public int BudgetYear { get; set; }

    public string Period { get; set; } = "MidYear";

    public string NoteType { get; set; } = "Outcome";

    public string? Body { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public virtual Entities Entity { get; set; } = null!;
}
