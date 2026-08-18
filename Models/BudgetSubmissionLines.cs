using System;

namespace GovBudget.Models;

public partial class BudgetSubmissionLines
{
    public long SubmissionLineId { get; set; }

    public long SubmissionId { get; set; }

    public long SourceBudgetLineId { get; set; }

    public int BudgetYear { get; set; }

    public int EntityId { get; set; }

    public int DepartmentId { get; set; }

    public int CategoryId { get; set; }

    public int ItemId { get; set; }

    public int? ProgramId { get; set; }

    public int? ActivityId { get; set; }

    public int? ProjectId { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal Amount { get; set; }

    public string DistributionMode { get; set; } = null!;

    public decimal M01 { get; set; }

    public decimal M02 { get; set; }

    public decimal M03 { get; set; }

    public decimal M04 { get; set; }

    public decimal M05 { get; set; }

    public decimal M06 { get; set; }

    public decimal M07 { get; set; }

    public decimal M08 { get; set; }

    public decimal M09 { get; set; }

    public decimal M10 { get; set; }

    public decimal M11 { get; set; }

    public decimal M12 { get; set; }

    public decimal F1_Percent { get; set; }

    public decimal F1_Amount { get; set; }

    public decimal F2_Percent { get; set; }

    public decimal F2_Amount { get; set; }

    public string Dep_Method { get; set; } = null!;

    public int Dep_LifeMonths { get; set; }

    public byte Dep_StartMonth { get; set; }

    public string? CapexAssetType { get; set; }

    public string? Notes { get; set; }

    public string Description { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }

    public string? DocFileName { get; set; }

    public string? DocContentType { get; set; }

    public int? DocSizeBytes { get; set; }

    public byte[]? DocContent { get; set; }

    public DateTime? DocUploadedAt { get; set; }

    public string? DocUploadedBy { get; set; }

    public DateTime SnapshottedAt { get; set; }

    public string? SnapshottedBy { get; set; }

    public virtual BudgetSubmissions Submission { get; set; } = null!;
}
