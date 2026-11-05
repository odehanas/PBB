using System;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Models;

/// <summary>
/// One row per Excel upload that changed data (mid-year actuals, historical actuals,
/// budget lines, items). Gives every upload an owner, a time, a file and a scope, and is
/// the anchor for undoing the upload.
/// </summary>
public partial class ImportBatches
{
    public long ImportBatchId { get; set; }

    /// <summary>MIDYEAR | HISTORICAL | BUDGETLINES | ITEMS</summary>
    public string ImportType { get; set; } = null!;

    /// <summary>Set when every row in the file belongs to the same year.</summary>
    public int? BudgetYear { get; set; }

    /// <summary>Set when every row in the file belongs to the same entity.</summary>
    public int? EntityId { get; set; }

    /// <summary>Free-text scope shown in the history list, e.g. "OPEX (replace)".</summary>
    public string? Scope { get; set; }

    public string? SourceFile { get; set; }

    public string? ImportedBy { get; set; }

    public DateTime ImportedAt { get; set; }

    public int RowsCreated { get; set; }

    public int RowsUpdated { get; set; }

    public int RowsDeleted { get; set; }

    /// <summary>APPLIED | UNDONE</summary>
    public string Status { get; set; } = "APPLIED";

    public DateTime? UndoneAt { get; set; }

    public string? UndoneBy { get; set; }
}

/// <summary>
/// One row per record an upload touched. BeforeJson holds the record as it was before the
/// upload (null for CREATED), which is what an undo restores.
/// </summary>
public partial class ImportBatchChanges
{
    public long ImportChangeId { get; set; }

    public long ImportBatchId { get; set; }

    /// <summary>MidYearGlActualForecasts | HistoricalGlActuals | BudgetLines | Items</summary>
    public string TableName { get; set; } = null!;

    /// <summary>CREATED | UPDATED | DELETED</summary>
    public string ChangeType { get; set; } = null!;

    public long RecordId { get; set; }

    public string? BeforeJson { get; set; }
}

public partial class GovBudgetContext
{
    public virtual DbSet<ImportBatches> ImportBatches { get; set; } = null!;

    public virtual DbSet<ImportBatchChanges> ImportBatchChanges { get; set; } = null!;

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ImportBatches>(entity =>
        {
            entity.HasKey(e => e.ImportBatchId);
            entity.ToTable("ImportBatches", "core");
            entity.Property(e => e.ImportType).HasMaxLength(30);
            entity.Property(e => e.Scope).HasMaxLength(200);
            entity.Property(e => e.SourceFile).HasMaxLength(260);
            entity.Property(e => e.ImportedBy).HasMaxLength(100);
            entity.Property(e => e.ImportedAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Status).HasMaxLength(20);
            entity.Property(e => e.UndoneBy).HasMaxLength(100);
        });

        modelBuilder.Entity<ImportBatchChanges>(entity =>
        {
            entity.HasKey(e => e.ImportChangeId);
            entity.ToTable("ImportBatchChanges", "core");
            entity.Property(e => e.TableName).HasMaxLength(60);
            entity.Property(e => e.ChangeType).HasMaxLength(10);
            entity.HasIndex(e => e.ImportBatchId, "IX_ImportBatchChanges_Batch");
            entity.HasIndex(e => new { e.TableName, e.RecordId }, "IX_ImportBatchChanges_Record");
        });
    }
}
