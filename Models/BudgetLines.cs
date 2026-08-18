using System;
using System.Collections.Generic;

namespace GovBudget.Models;

public partial class BudgetLines
{
    public long BudgetLineId { get; set; }

    public int BudgetYear { get; set; }

    public int EntityId { get; set; }

    public int DepartmentId { get; set; }

    public int CategoryId { get; set; }

    public int ItemId { get; set; }

    public int? ProgramId { get; set; }

    public int? ActivityId { get; set; }

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

    public DateTime CreatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }

    /// <summary>
    /// How the line was created: "MANUAL" (data entry) or "UPLOAD" (Excel import).
    /// NULL is treated as manual/legacy and protected from bulk-upload deletion.
    /// </summary>
    public string? EntrySource { get; set; }

    public string Description { get; set; } = null!;

    public int? ProjectId { get; set; }

    public virtual Activities? Activity { get; set; }

    public virtual Categories Category { get; set; } = null!;

    public virtual Departments Department { get; set; } = null!;

    public virtual Entities Entity { get; set; } = null!;

    public virtual Items Item { get; set; } = null!;

    public virtual Programs? Program { get; set; }

    public virtual Projects? Project { get; set; }

    public virtual BudgetLineDocuments? BudgetLineDocuments { get; set; }
}

public partial class BudgetLineDocuments
{
    public long BudgetLineId { get; set; }

    public string FileName { get; set; } = null!;

    public string ContentType { get; set; } = null!;

    public int SizeBytes { get; set; }

    public byte[] Content { get; set; } = null!;

    public DateTime UploadedAt { get; set; }

    public string? UploadedBy { get; set; }

    public virtual BudgetLines BudgetLine { get; set; } = null!;
}
