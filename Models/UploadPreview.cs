using System.Collections.Generic;

namespace GovBudget.Models;

/// <summary>
/// What an upload is about to change, shown before anything is saved
/// (Views/Shared/UploadPreview.cshtml). Values: A = main amount, B = optional second amount
/// (e.g. H1 actual and H2 forecast).
/// </summary>
public class UploadPreviewVm
{
    public string Title { get; set; } = "Review upload";
    public string Subtitle { get; set; } = "";
    public string? FileName { get; set; }

    public string ConfirmController { get; set; } = "";
    public string ConfirmAction { get; set; } = "";
    public string CancelAction { get; set; } = "";

    public string KeyHeader { get; set; } = "GL";
    public string? Key2Header { get; set; }
    public string ValueAHeader { get; set; } = "Amount";
    public string? ValueBHeader { get; set; }

    public int NewCount { get; set; }
    public int ChangedCount { get; set; }
    public int UnchangedCount { get; set; }

    /// <summary>Rows already stored in the same scope that are not in the file (left untouched).</summary>
    public int KeptCount { get; set; }

    public decimal TotalABefore { get; set; }
    public decimal TotalAAfter { get; set; }
    public decimal? TotalBBefore { get; set; }
    public decimal? TotalBAfter { get; set; }

    public List<UploadPreviewRow> Rows { get; set; } = new();
}

public class UploadPreviewRow
{
    public string Key { get; set; } = "";
    public string? Key2 { get; set; }

    /// <summary>New | Changed | Unchanged</summary>
    public string Status { get; set; } = "";

    public decimal? ABefore { get; set; }
    public decimal AAfter { get; set; }
    public decimal? BBefore { get; set; }
    public decimal? BAfter { get; set; }
}
