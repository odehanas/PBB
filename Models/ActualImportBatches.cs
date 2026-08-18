using System;
using System.Collections.Generic;

namespace GovBudget.Models;

/// <summary>
/// One row per uploaded actuals file. Used for audit and for scoping the
/// two-step overwrite confirmation on re-upload.
/// </summary>
public partial class ActualImportBatches
{
    public long ActualImportBatchId { get; set; }

    public int BudgetYear { get; set; }

    public int EntityId { get; set; }

    /// <summary>SAP_GL | SAP_MM | HR</summary>
    public string Source { get; set; } = null!;

    public byte? PeriodFrom { get; set; }

    public byte? PeriodTo { get; set; }

    public int RowsImported { get; set; }

    public decimal TotalAmount { get; set; }

    public string? SourceFile { get; set; }

    public DateTime ImportedAt { get; set; }

    public string? ImportedBy { get; set; }

    public virtual Entities Entity { get; set; } = null!;

    public virtual ICollection<ActualPostings> ActualPostings { get; set; } = new List<ActualPostings>();
}
