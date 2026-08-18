using System;

namespace GovBudget.Models;

/// <summary>
/// A saved Report Builder configuration owned by a user. Stores the chosen
/// dimensions, measure and filters so the report can be reloaded later.
/// Additive and isolated to the Reports module.
/// </summary>
public partial class SavedReports
{
    public int SavedReportId { get; set; }

    public string OwnerUser { get; set; } = "";

    public string Name { get; set; } = "";

    public int? BudgetYear { get; set; }

    public int? EntityId { get; set; }

    public string RowDim { get; set; } = "entity";

    public string? ColDim { get; set; }

    public string Measure { get; set; } = "amount";

    public string? Category { get; set; }

    public bool IncludeHr { get; set; }

    public string ChartType { get; set; } = "table";

    /// <summary>Include = show only selected categories; Exclude = show all except selected.</summary>
    public string CategoryMode { get; set; } = "Include";

    /// <summary>Comma-separated category codes for the include/exclude filter.</summary>
    public string? CategoriesCsv { get; set; }

    /// <summary>Optional Program Type filter: Mandate | Support (null = all).</summary>
    public string? ProgramTypeFilter { get; set; }

    /// <summary>Direct | Total (fully loaded with allocated support cost).</summary>
    public string CostBasis { get; set; } = "Direct";

    public DateTime CreatedAt { get; set; }
}
