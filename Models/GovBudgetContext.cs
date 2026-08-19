using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Models;

public partial class GovBudgetContext : DbContext
{
    public GovBudgetContext()
    {
    }

    public GovBudgetContext(DbContextOptions<GovBudgetContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Activities> Activities { get; set; }

    public virtual DbSet<AppUsers> AppUsers { get; set; }

    public virtual DbSet<AppRoles> AppRoles { get; set; }

    public virtual DbSet<RolePermissions> RolePermissions { get; set; }

    public virtual DbSet<AuditLogs> AuditLogs { get; set; }

    public virtual DbSet<BudgetLines> BudgetLines { get; set; }

    public virtual DbSet<BudgetLineDocuments> BudgetLineDocuments { get; set; }

    public virtual DbSet<BudgetSubmissionLines> BudgetSubmissionLines { get; set; }

    public virtual DbSet<BudgetSubmissions> BudgetSubmissions { get; set; }

    public virtual DbSet<BudgetRevisionRequests> BudgetRevisionRequests { get; set; }

    public virtual DbSet<Categories> Categories { get; set; }

    public virtual DbSet<Departments> Departments { get; set; }

    public virtual DbSet<Entities> Entities { get; set; }

    public virtual DbSet<GLAccounts> GLAccounts { get; set; }

    public virtual DbSet<HrEmployeeCostAllocations> HrEmployeeCostAllocations { get; set; }

    public virtual DbSet<HrEmployeeCosts> HrEmployeeCosts { get; set; }

    public virtual DbSet<HistoricalGlActuals> HistoricalGlActuals { get; set; }

    public virtual DbSet<MidYearGlActualForecasts> MidYearGlActualForecasts { get; set; }

    public virtual DbSet<ActualPostings> ActualPostings { get; set; }

    public virtual DbSet<ActualImportBatches> ActualImportBatches { get; set; }

    public virtual DbSet<ActualForecasts> ActualForecasts { get; set; }

    public virtual DbSet<HrActualPostings> HrActualPostings { get; set; }

    public virtual DbSet<Items> Items { get; set; }

    public virtual DbSet<Programs> Programs { get; set; }

    public virtual DbSet<Projects> Projects { get; set; }

    public virtual DbSet<InternalMessages> InternalMessages { get; set; }

    public virtual DbSet<PasswordResetRequests> PasswordResetRequests { get; set; }

    public virtual DbSet<WhatIfScenarios> WhatIfScenarios { get; set; }

    public virtual DbSet<WhatIfScenarioDefaults> WhatIfScenarioDefaults { get; set; }

    public virtual DbSet<WhatIfScenarioProjectRates> WhatIfScenarioProjectRates { get; set; }

    public virtual DbSet<vw_GL_CashBasis> vw_GL_CashBasis { get; set; }

    public virtual DbSet<Kpis> Kpis { get; set; }

    public virtual DbSet<KpiCostLinks> KpiCostLinks { get; set; }

    public virtual DbSet<ActivityOutputs> ActivityOutputs { get; set; }

    public virtual DbSet<MaturityAssessments> MaturityAssessments { get; set; }

    public virtual DbSet<EntityReviewNotes> EntityReviewNotes { get; set; }

    public virtual DbSet<CostShapeMap> CostShapeMap { get; set; }

    public virtual DbSet<ReviewNarratives> ReviewNarratives { get; set; }

    public virtual DbSet<SavedReports> SavedReports { get; set; }

    public virtual DbSet<AllocationDrivers> AllocationDrivers { get; set; }

    public virtual DbSet<AllocationDriverValues> AllocationDriverValues { get; set; }

    public virtual DbSet<AllocationRules> AllocationRules { get; set; }

    public virtual DbSet<AllocationRuleTargets> AllocationRuleTargets { get; set; }

    public virtual DbSet<AllocationRuns> AllocationRuns { get; set; }

    public virtual DbSet<AllocationTransactions> AllocationTransactions { get; set; }

    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Activities>(entity =>
        {
            entity.HasKey(e => e.ActivityId).HasName("PK__Activiti__45F4A7917673C70C");

            entity.ToTable("Activities", "core");

            entity.HasIndex(e => new { e.ProgramId, e.ActivityCode }, "UQ_Activity").IsUnique();

            entity.Property(e => e.ActivityCode).HasMaxLength(30);
            entity.Property(e => e.ActivityName).HasMaxLength(200);
            entity.Property(e => e.IsActive).HasDefaultValue(true);

            entity.HasOne(d => d.Department).WithMany(p => p.Activities)
                .HasForeignKey(d => d.DepartmentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Activity_Department");

            entity.HasOne(d => d.Program).WithMany(p => p.Activities)
                .HasForeignKey(d => d.ProgramId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Activity_Program");
        });

        modelBuilder.Entity<AppUsers>(entity =>
        {
            entity.HasKey(e => e.UserId).HasName("PK__AppUsers__1788CC4C04635C33");

            entity.ToTable("AppUsers", "core");

            entity.HasIndex(e => e.UserName, "UQ__AppUsers__C9F28456706A2029").IsUnique();

            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Password).HasMaxLength(128);
            entity.Property(e => e.PasswordHash).HasMaxLength(200);
            entity.Property(e => e.MustChangePassword).HasDefaultValue(false);
            entity.Property(e => e.FailedLoginCount).HasDefaultValue(0);
            entity.Property(e => e.SecurityStamp).HasMaxLength(64);
            entity.Property(e => e.Role).HasMaxLength(20);
            entity.Property(e => e.UserName).HasMaxLength(100);

            entity.HasOne(d => d.Entity).WithMany()
                .HasForeignKey(d => d.EntityId)
                .HasConstraintName("FK_AppUser_Entity");

            entity.HasOne(d => d.Department).WithMany(p => p.AppUsers)
                .HasForeignKey(d => d.DepartmentId)
                .HasConstraintName("FK_AppUser_Department");
        });

        modelBuilder.Entity<AppRoles>(entity =>
        {
            entity.HasKey(e => e.RoleId);

            entity.ToTable("AppRoles", "core");

            entity.HasIndex(e => e.RoleCode, "UQ_AppRoles_RoleCode").IsUnique();

            entity.Property(e => e.RoleCode).HasMaxLength(20);
            entity.Property(e => e.RoleName).HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(300);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<RolePermissions>(entity =>
        {
            entity.HasKey(e => e.RolePermissionId);

            entity.ToTable("RolePermissions", "core");

            entity.HasIndex(e => new { e.RoleId, e.FormKey }, "UQ_RolePermissions_RoleForm").IsUnique();

            entity.Property(e => e.FormKey).HasMaxLength(50);
            entity.Property(e => e.UpdatedBy).HasMaxLength(100);

            entity.HasOne(d => d.Role).WithMany(p => p.RolePermissions)
                .HasForeignKey(d => d.RoleId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_RolePermissions_Role");
        });

        modelBuilder.Entity<AuditLogs>(entity =>
        {
            entity.HasKey(e => e.AuditLogId);
            entity.ToTable("AuditLogs", "core");
            entity.Property(e => e.Timestamp).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.UserName).HasMaxLength(100);
            entity.Property(e => e.Action).HasMaxLength(50);
            entity.Property(e => e.EntityName).HasMaxLength(100);
            entity.Property(e => e.RecordId).HasMaxLength(100);
        });

        modelBuilder.Entity<HrEmployeeCosts>(entity =>
        {
            entity.HasKey(e => e.EmployeeCostId);

            entity.ToTable("HrEmployeeCosts", "core");

            entity.HasIndex(e => new { e.BudgetYear, e.EmployeeId }, "UQ_HrEmployeeCosts_YearEmployee").IsUnique();

            entity.Property(e => e.EmployeeId).HasMaxLength(50);
            entity.Property(e => e.EmployeeName).HasMaxLength(200);
            entity.Property(e => e.Occupation).HasMaxLength(150);
            entity.Property(e => e.GLCode).HasMaxLength(30);
            entity.Property(e => e.GLKind).HasMaxLength(20);
            entity.Property(e => e.EntityName).HasMaxLength(200);
            entity.Property(e => e.DepartmentName).HasMaxLength(200);
            entity.Property(e => e.AnnualCost).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.ImportedAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.ImportedBy).HasMaxLength(100);
            entity.Property(e => e.SourceFile).HasMaxLength(260);

            entity.HasOne(d => d.Entity).WithMany()
                .HasForeignKey(d => d.EntityId)
                .HasConstraintName("FK_HrEmployeeCosts_Entity");

            entity.HasOne(d => d.Department).WithMany()
                .HasForeignKey(d => d.DepartmentId)
                .HasConstraintName("FK_HrEmployeeCosts_Department");
        });

        modelBuilder.Entity<HrEmployeeCostAllocations>(entity =>
        {
            entity.HasKey(e => e.AllocationId);

            entity.ToTable("HrEmployeeCostAllocations", "core");

            entity.Property(e => e.AllocatedAmount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CreatedBy).HasMaxLength(100);

            entity.HasOne(d => d.EmployeeCost).WithMany(p => p.HrEmployeeCostAllocations)
                .HasForeignKey(d => d.EmployeeCostId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_HrAlloc_EmployeeCost");

            entity.HasOne(d => d.Activity).WithMany()
                .HasForeignKey(d => d.ActivityId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_HrAlloc_Activity");

            entity.HasOne(d => d.Project).WithMany()
                .HasForeignKey(d => d.ProjectId)
                .HasConstraintName("FK_HrAlloc_Project");
        });

        modelBuilder.Entity<HistoricalGlActuals>(entity =>
        {
            entity.HasKey(e => e.HistoricalActualId);

            entity.ToTable("HistoricalGlActuals", "core");

            entity.HasIndex(e => new { e.BudgetYear, e.EntityId, e.DepartmentId, e.GLCode }, "UQ_HistoricalGlActuals_Scope").IsUnique();

            entity.Property(e => e.GLCode).HasMaxLength(30);
            entity.Property(e => e.GLType).HasMaxLength(20);
            entity.Property(e => e.Amount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CreatedBy).HasMaxLength(100);
            entity.Property(e => e.SourceFile).HasMaxLength(260);

            entity.HasOne(d => d.Entity).WithMany()
                .HasForeignKey(d => d.EntityId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_HistoricalGlActuals_Entity");

            entity.HasOne(d => d.Department).WithMany()
                .HasForeignKey(d => d.DepartmentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_HistoricalGlActuals_Department");
        });

        modelBuilder.Entity<MidYearGlActualForecasts>(entity =>
        {
            entity.HasKey(e => e.MidYearId);

            entity.ToTable("MidYearGlActualForecasts", "core");

            entity.HasIndex(e => new { e.BudgetYear, e.EntityId, e.GLCode }, "UQ_MidYearGlActualForecasts_Scope").IsUnique();

            entity.Property(e => e.GLCode).HasMaxLength(30);
            entity.Property(e => e.GLType).HasMaxLength(20);
            entity.Property(e => e.ActualH1Amount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.ForecastH2Amount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CreatedBy).HasMaxLength(100);
            entity.Property(e => e.ForecastUpdatedAt);
            entity.Property(e => e.ForecastUpdatedBy).HasMaxLength(100);
            entity.Property(e => e.SourceFile).HasMaxLength(260);

            entity.HasOne(d => d.Entity).WithMany()
                .HasForeignKey(d => d.EntityId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_MidYearGlActualForecasts_Entity");
        });

        modelBuilder.Entity<BudgetLines>(entity =>
        {
            entity.HasKey(e => e.BudgetLineId).HasName("PK__BudgetLi__321CF54220B92EEC");

            entity.ToTable("BudgetLines", "core");

            entity.Property(e => e.Amount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.CreatedAt)
                .HasPrecision(0)
                .HasDefaultValueSql("(sysdatetime())");
            entity.Property(e => e.CreatedBy).HasMaxLength(100);
            entity.Property(e => e.Dep_Method)
                .HasMaxLength(20)
                .HasDefaultValue("STRAIGHT");
            entity.Property(e => e.Dep_StartMonth).HasDefaultValue((byte)1);
            entity.Property(e => e.Description)
                .HasMaxLength(300)
                .HasDefaultValue("");
            entity.Property(e => e.DistributionMode)
                .HasMaxLength(10)
                .HasDefaultValue("EQUAL");
            entity.Property(e => e.F1_Amount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.F1_Percent).HasColumnType("decimal(9, 4)");
            entity.Property(e => e.F2_Amount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.F2_Percent).HasColumnType("decimal(9, 4)");
            entity.Property(e => e.M01).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M02).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M03).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M04).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M05).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M06).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M07).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M08).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M09).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M10).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M11).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M12).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Notes).HasMaxLength(500);
            entity.Property(e => e.CapexAssetType).HasMaxLength(20);
            entity.Property(e => e.Quantity).HasColumnType("decimal(18, 4)");
            entity.Property(e => e.UnitPrice).HasColumnType("decimal(18, 4)");
            entity.Property(e => e.UpdatedAt).HasPrecision(0);
            entity.Property(e => e.UpdatedBy).HasMaxLength(100);
            entity.Property(e => e.EntrySource).HasMaxLength(10);

            entity.HasOne(d => d.Activity).WithMany(p => p.BudgetLines)
                .HasForeignKey(d => d.ActivityId)
                .HasConstraintName("FK_BL_Act");

            entity.HasOne(d => d.Category).WithMany(p => p.BudgetLines)
                .HasForeignKey(d => d.CategoryId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_BL_Cat");

            entity.HasOne(d => d.Department).WithMany(p => p.BudgetLines)
                .HasForeignKey(d => d.DepartmentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_BL_Dep");

            entity.HasOne(d => d.Entity).WithMany(p => p.BudgetLines)
                .HasForeignKey(d => d.EntityId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_BL_Entity");

            entity.HasOne(d => d.Item).WithMany(p => p.BudgetLines)
                .HasForeignKey(d => d.ItemId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_BL_Item");

            entity.HasOne(d => d.Program).WithMany(p => p.BudgetLines)
                .HasForeignKey(d => d.ProgramId)
                .HasConstraintName("FK_BL_Prog");

            entity.HasOne(d => d.Project).WithMany(p => p.BudgetLines)
                .HasForeignKey(d => d.ProjectId)
                .HasConstraintName("FK_BL_Project");
        });

        modelBuilder.Entity<BudgetLineDocuments>(entity =>
        {
            entity.HasKey(e => e.BudgetLineId).HasName("PK_BudgetLineDocuments");

            entity.ToTable("BudgetLineDocuments", "core");

            entity.Property(e => e.FileName).HasMaxLength(260);
            entity.Property(e => e.ContentType).HasMaxLength(100);
            entity.Property(e => e.UploadedAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.UploadedBy).HasMaxLength(100);

            entity.HasOne(d => d.BudgetLine).WithOne(p => p.BudgetLineDocuments)
                .HasForeignKey<BudgetLineDocuments>(d => d.BudgetLineId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_BudgetLineDocuments_BudgetLines");
        });

        modelBuilder.Entity<BudgetSubmissionLines>(entity =>
        {
            entity.HasKey(e => e.SubmissionLineId);

            entity.ToTable("BudgetSubmissionLines", "core");

            entity.HasIndex(e => new { e.SubmissionId, e.SourceBudgetLineId }, "UQ_BudgetSubmissionLines").IsUnique();

            entity.Property(e => e.Amount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.CreatedBy).HasMaxLength(100);
            entity.Property(e => e.Dep_Method).HasMaxLength(20);
            entity.Property(e => e.Description).HasMaxLength(300);
            entity.Property(e => e.DistributionMode).HasMaxLength(10);
            entity.Property(e => e.DocContentType).HasMaxLength(100);
            entity.Property(e => e.DocFileName).HasMaxLength(260);
            entity.Property(e => e.DocUploadedBy).HasMaxLength(100);
            entity.Property(e => e.F1_Amount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.F1_Percent).HasColumnType("decimal(9, 4)");
            entity.Property(e => e.F2_Amount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.F2_Percent).HasColumnType("decimal(9, 4)");
            entity.Property(e => e.CapexAssetType).HasMaxLength(20);
            entity.Property(e => e.M01).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M02).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M03).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M04).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M05).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M06).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M07).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M08).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M09).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M10).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M11).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M12).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Notes).HasMaxLength(500);
            entity.Property(e => e.Quantity).HasColumnType("decimal(18, 4)");
            entity.Property(e => e.SnapshottedBy).HasMaxLength(100);
            entity.Property(e => e.UnitPrice).HasColumnType("decimal(18, 4)");
            entity.Property(e => e.UpdatedBy).HasMaxLength(100);

            entity.HasOne(d => d.Submission).WithMany()
                .HasForeignKey(d => d.SubmissionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_BudgetSubmissionLines_Submission");
        });

        modelBuilder.Entity<Categories>(entity =>
        {
            entity.HasKey(e => e.CategoryId).HasName("PK__Categori__19093A0B447281DA");

            entity.ToTable("Categories", "core");

            entity.HasIndex(e => e.CategoryCode, "UQ__Categori__371BA9557984A741").IsUnique();

            entity.Property(e => e.CategoryCode).HasMaxLength(10);
            entity.Property(e => e.CategoryName).HasMaxLength(50);
        });

        modelBuilder.Entity<Departments>(entity =>
        {
            entity.HasKey(e => e.DepartmentId).HasName("PK__Departme__B2079BEDFFE1792A");

            entity.ToTable("Departments", "core");

            entity.HasIndex(e => new { e.EntityId, e.DeptCode }, "UQ_Department").IsUnique();

            entity.Property(e => e.DeptCode).HasMaxLength(20);
            entity.Property(e => e.DeptName).HasMaxLength(200);
            entity.Property(e => e.IsActive).HasDefaultValue(true);

            entity.HasOne(d => d.Entity).WithMany(p => p.Departments)
                .HasForeignKey(d => d.EntityId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Department_Entity");
        });

        modelBuilder.Entity<Entities>(entity =>
        {
            entity.HasKey(e => e.EntityId).HasName("PK__Entities__9C892F9DF4AC6512");

            entity.ToTable("Entities", "core");

            entity.HasIndex(e => e.EntityCode, "UQ__Entities__D062AD0AE992EE64").IsUnique();

            entity.Property(e => e.EntityCode).HasMaxLength(20);
            entity.Property(e => e.EntityName).HasMaxLength(200);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<GLAccounts>(entity =>
        {
            entity.HasKey(e => e.GLAccountId).HasName("PK__GLAccoun__FECC546EFDFB69AB");

            entity.ToTable("GLAccounts", "core");

            entity.HasIndex(e => e.GLCode, "UQ__GLAccoun__70F04E65F98CC168").IsUnique();

            entity.Property(e => e.GLCode).HasMaxLength(30);
            entity.Property(e => e.GLName).HasMaxLength(200);
            entity.Property(e => e.GLType).HasMaxLength(20);
        });

        modelBuilder.Entity<Items>(entity =>
        {
            entity.HasKey(e => e.ItemId).HasName("PK__Items__727E838B0702D68E");

            entity.ToTable("Items", "core");

            entity.HasIndex(e => e.ItemCode, "UQ__Items__3ECC0FEA20E3E6CC").IsUnique();

            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.ItemCode).HasMaxLength(30);
            entity.Property(e => e.ItemName).HasMaxLength(200);

            entity.HasOne(d => d.GLAccount).WithMany(p => p.Items)
                .HasForeignKey(d => d.GLAccountId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Item_GL");
        });

        modelBuilder.Entity<Programs>(entity =>
        {
            entity.HasKey(e => e.ProgramId).HasName("PK__Programs__75256058052176E1");

            entity.ToTable("Programs", "core");

            entity.HasIndex(e => new { e.EntityId, e.ProgramCode }, "UQ_Program").IsUnique();

            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.ProgramCode).HasMaxLength(30);
            entity.Property(e => e.ProgramName).HasMaxLength(200);
            entity.Property(e => e.ProgramType).HasMaxLength(20).HasDefaultValue("Mandate");

            entity.HasOne(d => d.Entity).WithMany(p => p.Programs)
                .HasForeignKey(d => d.EntityId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Program_Entity");
        });

        modelBuilder.Entity<Projects>(entity =>
        {
            entity.HasKey(e => e.ProjectId).HasName("PK__Projects__761ABEF090A940D5");

            entity.ToTable("Projects", "core");

            entity.HasIndex(e => e.ProjectCode, "UQ__Projects__2F3A4948B16BBFD8").IsUnique();

            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.ProjectCode).HasMaxLength(30);
            entity.Property(e => e.ProjectName).HasMaxLength(200);

            entity.HasOne(d => d.OwningDepartment).WithMany(p => p.Projects)
                .HasForeignKey(d => d.OwningDepartmentId)
                .HasConstraintName("FK_Project_Department");
        });

        modelBuilder.Entity<BudgetSubmissions>(entity =>
        {
            entity.HasKey(e => e.SubmissionId);

            entity.ToTable("BudgetSubmissions", "core");

            entity.HasIndex(e => new { e.BudgetYear, e.EntityId, e.DepartmentId, e.CategoryId, e.VersionNo }, "UQ_BudgetSubmissions_ScopeVersion").IsUnique();

            entity.Property(e => e.Status).HasMaxLength(20).HasDefaultValue("Draft");
            entity.Property(e => e.SubmittedBy).HasMaxLength(100);
            entity.Property(e => e.ApprovedBy).HasMaxLength(100);
            entity.Property(e => e.SentToCentralBy).HasMaxLength(100);
            entity.Property(e => e.FinalizedBy).HasMaxLength(100);
            entity.Property(e => e.ApprovalNote).HasMaxLength(500);
            entity.Property(e => e.SysApprovedBy).HasMaxLength(100);
            entity.Property(e => e.SysApprovalNote).HasMaxLength(500);
            entity.Property(e => e.ReturnedBy).HasMaxLength(100);
            entity.Property(e => e.ReturnNote).HasMaxLength(500);

            entity.HasOne(d => d.Category).WithMany()
                .HasForeignKey(d => d.CategoryId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_BudgetSubmissions_Category");

            entity.HasOne(d => d.Department).WithMany()
                .HasForeignKey(d => d.DepartmentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_BudgetSubmissions_Department");

            entity.HasOne(d => d.Entity).WithMany()
                .HasForeignKey(d => d.EntityId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_BudgetSubmissions_Entity");
        });

        modelBuilder.Entity<BudgetRevisionRequests>(entity =>
        {
            entity.HasKey(e => e.RequestId);

            entity.ToTable("BudgetRevisionRequests", "core");

            entity.Property(e => e.ActionType).HasMaxLength(20);
            entity.Property(e => e.Note).HasMaxLength(500);
            entity.Property(e => e.RequestedAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.RequestedBy).HasMaxLength(100);

            entity.HasOne(d => d.Submission).WithMany()
                .HasForeignKey(d => d.SubmissionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_BudgetRevisionRequests_Submission");
        });

        modelBuilder.Entity<InternalMessages>(entity =>
        {
            entity.HasKey(e => e.MessageId);

            entity.ToTable("InternalMessages", "core");

            entity.Property(e => e.FromUser).HasMaxLength(100);
            entity.Property(e => e.FromEntityCode).HasMaxLength(20);
            entity.Property(e => e.FromDeptCode).HasMaxLength(20);
            entity.Property(e => e.Subject).HasMaxLength(200);
            entity.Property(e => e.Status).HasMaxLength(20).HasDefaultValue("Pending");
            entity.Property(e => e.ReadBy).HasMaxLength(100);
            entity.Property(e => e.RespondedBy).HasMaxLength(100);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
        });

        modelBuilder.Entity<PasswordResetRequests>(entity =>
        {
            entity.HasKey(e => e.ResetRequestId);

            entity.ToTable("PasswordResetRequests", "core");

            entity.HasIndex(e => e.Token);

            entity.Property(e => e.UserName).HasMaxLength(100);
            entity.Property(e => e.ContactInfo).HasMaxLength(200);
            entity.Property(e => e.Note).HasMaxLength(500);
            entity.Property(e => e.Status).HasMaxLength(20).HasDefaultValue("Pending");
            entity.Property(e => e.RequestSource).HasMaxLength(20);
            entity.Property(e => e.Token).HasMaxLength(128);
            entity.Property(e => e.IssuedBy).HasMaxLength(100);
            entity.Property(e => e.RejectedBy).HasMaxLength(100);
            entity.Property(e => e.AdminNote).HasMaxLength(500);
            entity.Property(e => e.RequestedAt).HasDefaultValueSql("(sysutcdatetime())");
        });

        modelBuilder.Entity<WhatIfScenarios>(entity =>
        {
            entity.HasKey(e => e.ScenarioId);

            entity.ToTable("WhatIfScenarios", "core");

            entity.HasIndex(e => new { e.BudgetYear, e.EntityId, e.DepartmentId, e.ScenarioName }, "UQ_WhatIfScenarios_ScopeName").IsUnique();

            entity.Property(e => e.ScenarioName).HasMaxLength(200);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CreatedBy).HasMaxLength(100);
            entity.Property(e => e.UpdatedBy).HasMaxLength(100);

            entity.HasOne(d => d.WhatIfScenarioDefaults).WithOne(p => p.Scenario)
                .HasForeignKey<WhatIfScenarioDefaults>(d => d.ScenarioId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_WhatIfScenarioDefaults_Scenario");
        });

        modelBuilder.Entity<WhatIfScenarioDefaults>(entity =>
        {
            entity.HasKey(e => e.ScenarioId);

            entity.ToTable("WhatIfScenarioDefaults", "core");

            entity.Property(e => e.CostInflationRate).HasColumnType("decimal(9, 4)").HasDefaultValue(0m);
            entity.Property(e => e.RevenueGrowthRate).HasColumnType("decimal(9, 4)").HasDefaultValue(0m);
        });

        modelBuilder.Entity<WhatIfScenarioProjectRates>(entity =>
        {
            entity.HasKey(e => e.ScenarioProjectRateId);

            entity.ToTable("WhatIfScenarioProjectRates", "core");

            entity.HasIndex(e => new { e.ScenarioId, e.ProjectId }, "UQ_WhatIfScenarioProjectRates").IsUnique();

            entity.Property(e => e.CostInflationRate).HasColumnType("decimal(9, 4)");
            entity.Property(e => e.RevenueGrowthRate).HasColumnType("decimal(9, 4)");

            entity.HasOne(d => d.Scenario).WithMany(p => p.WhatIfScenarioProjectRates)
                .HasForeignKey(d => d.ScenarioId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_WhatIfScenarioProjectRates_Scenario");

            entity.HasOne(d => d.Project).WithMany()
                .HasForeignKey(d => d.ProjectId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_WhatIfScenarioProjectRates_Project");
        });

        modelBuilder.Entity<vw_GL_CashBasis>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("vw_GL_CashBasis", "core");

            entity.Property(e => e.AnnualAmount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.CategoryCode).HasMaxLength(10);
            entity.Property(e => e.DeptCode).HasMaxLength(20);
            entity.Property(e => e.DeptName).HasMaxLength(200);
            entity.Property(e => e.DistributedAmount).HasColumnType("decimal(29, 2)");
            entity.Property(e => e.EntityCode).HasMaxLength(20);
            entity.Property(e => e.EntityName).HasMaxLength(200);
            entity.Property(e => e.GLCode).HasMaxLength(30);
            entity.Property(e => e.GLName).HasMaxLength(200);
            entity.Property(e => e.GLType).HasMaxLength(20);
            entity.Property(e => e.M01).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M02).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M03).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M04).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M05).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M06).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M07).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M08).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M09).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M10).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M11).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.M12).HasColumnType("decimal(18, 2)");
        });

        modelBuilder.Entity<Kpis>(entity =>
        {
            entity.HasKey(e => e.KpiId);
            entity.ToTable("Kpis", "core");

            entity.Property(e => e.Period).HasMaxLength(20).HasDefaultValue("MidYear");
            entity.Property(e => e.KpiName).HasMaxLength(300);
            entity.Property(e => e.Unit).HasMaxLength(50);
            entity.Property(e => e.KpiType).HasMaxLength(20);
            entity.Property(e => e.Dimension).HasMaxLength(20);
            entity.Property(e => e.ReadingType).HasMaxLength(20);
            entity.Property(e => e.Priority).HasMaxLength(20);
            entity.Property(e => e.KpiCode).HasMaxLength(50);
            entity.Property(e => e.ProgramOwner).HasMaxLength(200);
            entity.Property(e => e.StrategicTarget2029).HasColumnType("decimal(18, 4)");
            entity.Property(e => e.CostWeight).HasColumnType("decimal(18, 4)");
            entity.Property(e => e.Direction).HasMaxLength(10).HasDefaultValue("UP");
            entity.Property(e => e.Baseline).HasColumnType("decimal(18, 4)");
            entity.Property(e => e.Target).HasColumnType("decimal(18, 4)");
            entity.Property(e => e.ActualValue).HasColumnType("decimal(18, 4)");
            entity.Property(e => e.Status).HasMaxLength(20);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CreatedBy).HasMaxLength(100);

            entity.HasOne(d => d.Entity).WithMany()
                .HasForeignKey(d => d.EntityId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Kpis_Entity");

            entity.HasOne(d => d.Program).WithMany()
                .HasForeignKey(d => d.ProgramId)
                .HasConstraintName("FK_Kpis_Program");

            entity.HasOne(d => d.Activity).WithMany()
                .HasForeignKey(d => d.ActivityId)
                .HasConstraintName("FK_Kpis_Activity");
        });

        modelBuilder.Entity<KpiCostLinks>(entity =>
        {
            entity.HasKey(e => e.KpiCostLinkId);
            entity.ToTable("KpiCostLinks", "core");

            entity.Property(e => e.WeightPct).HasColumnType("decimal(9, 4)").HasDefaultValue(100m);

            entity.HasOne(d => d.Kpi).WithMany(p => p.KpiCostLinks)
                .HasForeignKey(d => d.KpiId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_KpiCostLinks_Kpi");

            entity.HasOne(d => d.Activity).WithMany()
                .HasForeignKey(d => d.ActivityId)
                .HasConstraintName("FK_KpiCostLinks_Activity");

            entity.HasOne(d => d.Program).WithMany()
                .HasForeignKey(d => d.ProgramId)
                .HasConstraintName("FK_KpiCostLinks_Program");
        });

        modelBuilder.Entity<ActivityOutputs>(entity =>
        {
            entity.HasKey(e => e.ActivityOutputId);
            entity.ToTable("ActivityOutputs", "core");

            entity.HasIndex(e => new { e.ActivityId, e.BudgetYear, e.OutputMeasure }, "UQ_ActivityOutputs").IsUnique();

            entity.Property(e => e.OutputMeasure).HasMaxLength(200);
            entity.Property(e => e.OutputVolume).HasColumnType("decimal(18, 4)");
            entity.Property(e => e.IsPrimary).HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CreatedBy).HasMaxLength(100);

            entity.HasOne(d => d.Activity).WithMany()
                .HasForeignKey(d => d.ActivityId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ActivityOutputs_Activity");
        });

        modelBuilder.Entity<MaturityAssessments>(entity =>
        {
            entity.HasKey(e => e.MaturityAssessmentId);
            entity.ToTable("MaturityAssessments", "core");

            entity.HasIndex(e => new { e.EntityId, e.BudgetYear, e.Period }, "UQ_MaturityAssessments").IsUnique();

            entity.Property(e => e.Period).HasMaxLength(20).HasDefaultValue("MidYear");
            entity.Property(e => e.Stage).HasColumnType("decimal(3, 1)").HasDefaultValue(1.0m);
            entity.Property(e => e.Form).HasMaxLength(40);
            entity.Property(e => e.StatusLabel).HasMaxLength(20);
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.Property(e => e.AssessedAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.AssessedBy).HasMaxLength(100);

            entity.HasOne(d => d.Entity).WithMany()
                .HasForeignKey(d => d.EntityId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_MaturityAssessments_Entity");
        });

        modelBuilder.Entity<EntityReviewNotes>(entity =>
        {
            entity.HasKey(e => e.EntityReviewNoteId);
            entity.ToTable("EntityReviewNotes", "core");

            entity.Property(e => e.Period).HasMaxLength(20).HasDefaultValue("MidYear");
            entity.Property(e => e.NoteType).HasMaxLength(30).HasDefaultValue("Outcome");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CreatedBy).HasMaxLength(100);

            entity.HasOne(d => d.Entity).WithMany()
                .HasForeignKey(d => d.EntityId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_EntityReviewNotes_Entity");
        });

        modelBuilder.Entity<CostShapeMap>(entity =>
        {
            entity.HasKey(e => e.CostShapeMapId);
            entity.ToTable("CostShapeMap", "core");

            entity.Property(e => e.GLCode).HasMaxLength(30);
            entity.Property(e => e.MatchKeyword).HasMaxLength(100);
            entity.Property(e => e.Bucket).HasMaxLength(30);
            entity.Property(e => e.Priority).HasDefaultValue(100);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<ReviewNarratives>(entity =>
        {
            entity.HasKey(e => e.ReviewNarrativeId);
            entity.ToTable("ReviewNarratives", "core");

            entity.Property(e => e.Period).HasMaxLength(20).HasDefaultValue("MidYear");
            entity.Property(e => e.Section).HasMaxLength(30).HasDefaultValue("Finding");
            entity.Property(e => e.Title).HasMaxLength(300);
            entity.Property(e => e.Owner).HasMaxLength(200);
            entity.Property(e => e.DueText).HasMaxLength(100);
            entity.Property(e => e.SuccessMeasure).HasMaxLength(500);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CreatedBy).HasMaxLength(100);
        });

        modelBuilder.Entity<SavedReports>(entity =>
        {
            entity.HasKey(e => e.SavedReportId);
            entity.ToTable("SavedReports", "core");

            entity.Property(e => e.OwnerUser).HasMaxLength(256);
            entity.Property(e => e.Name).HasMaxLength(150);
            entity.Property(e => e.RowDim).HasMaxLength(30);
            entity.Property(e => e.ColDim).HasMaxLength(30);
            entity.Property(e => e.Measure).HasMaxLength(30);
            entity.Property(e => e.Category).HasMaxLength(50);
            entity.Property(e => e.ChartType).HasMaxLength(20).HasDefaultValue("table");
            entity.Property(e => e.CategoryMode).HasMaxLength(10).HasDefaultValue("Include");
            entity.Property(e => e.CategoriesCsv).HasMaxLength(400);
            entity.Property(e => e.ProgramTypeFilter).HasMaxLength(20);
            entity.Property(e => e.CostBasis).HasMaxLength(20).HasDefaultValue("Direct");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
        });

        modelBuilder.Entity<AllocationDrivers>(entity =>
        {
            entity.HasKey(e => e.DriverId);
            entity.ToTable("AllocationDrivers", "core");
            entity.Property(e => e.DriverCode).HasMaxLength(40);
            entity.Property(e => e.DriverName).HasMaxLength(120);
            entity.Property(e => e.Unit).HasMaxLength(40);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<AllocationDriverValues>(entity =>
        {
            entity.HasKey(e => e.DriverValueId);
            entity.ToTable("AllocationDriverValues", "core");
            entity.Property(e => e.Value).HasColumnType("decimal(18,4)");
        });

        modelBuilder.Entity<AllocationRules>(entity =>
        {
            entity.HasKey(e => e.RuleId);
            entity.ToTable("AllocationRules", "core");
            entity.Property(e => e.Method).HasMaxLength(20);
            entity.Property(e => e.CategoryScopeCsv).HasMaxLength(200).HasDefaultValue("OPEX,HR");
            entity.Property(e => e.TargetScope).HasMaxLength(20).HasDefaultValue("AllMandate");
            entity.Property(e => e.SourcePercent).HasColumnType("decimal(9,4)").HasDefaultValue(100m);
            entity.Property(e => e.Sequence).HasDefaultValue(100);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CreatedBy).HasMaxLength(100);
        });

        modelBuilder.Entity<AllocationRuleTargets>(entity =>
        {
            entity.HasKey(e => e.RuleTargetId);
            entity.ToTable("AllocationRuleTargets", "core");
            entity.Property(e => e.Weight).HasColumnType("decimal(9,4)");
            entity.HasOne(d => d.Rule).WithMany(p => p.Targets)
                .HasForeignKey(d => d.RuleId);
        });

        modelBuilder.Entity<AllocationRuns>(entity =>
        {
            entity.HasKey(e => e.RunId);
            entity.ToTable("AllocationRuns", "core");
            entity.Property(e => e.Period).HasMaxLength(20).HasDefaultValue("Annual");
            entity.Property(e => e.Status).HasMaxLength(20).HasDefaultValue("Draft");
            entity.Property(e => e.ScenarioName).HasMaxLength(120);
            entity.Property(e => e.Method).HasMaxLength(20).HasDefaultValue("StepDown");
            entity.Property(e => e.RunAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.RunBy).HasMaxLength(100);
            entity.Property(e => e.Notes).HasMaxLength(500);
        });

        modelBuilder.Entity<AllocationTransactions>(entity =>
        {
            entity.HasKey(e => e.TxnId);
            entity.ToTable("AllocationTransactions", "core");
            entity.Property(e => e.Period).HasMaxLength(20).HasDefaultValue("Annual");
            entity.Property(e => e.SourceCategoryCode).HasMaxLength(50);
            entity.Property(e => e.BasisValue).HasColumnType("decimal(18,4)");
            entity.Property(e => e.BasisTotal).HasColumnType("decimal(18,4)");
            entity.Property(e => e.AllocationPct).HasColumnType("decimal(9,6)");
            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
        });

        modelBuilder.Entity<ActualImportBatches>(entity =>
        {
            entity.HasKey(e => e.ActualImportBatchId);
            entity.ToTable("ActualImportBatches", "core");
            entity.Property(e => e.Source).HasMaxLength(10);
            entity.Property(e => e.TotalAmount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.SourceFile).HasMaxLength(260);
            entity.Property(e => e.ImportedAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.ImportedBy).HasMaxLength(100);

            entity.HasOne(d => d.Entity).WithMany()
                .HasForeignKey(d => d.EntityId)
                .HasConstraintName("FK_ActualImportBatches_Entity");
        });

        modelBuilder.Entity<ActualPostings>(entity =>
        {
            entity.HasKey(e => e.ActualPostingId);
            entity.ToTable("ActualPostings", "core");
            entity.HasIndex(e => new { e.BudgetYear, e.EntityId, e.GLCode, e.PeriodMonth }, "IX_ActualPostings_Scope");
            entity.HasIndex(e => new { e.BudgetYear, e.EntityId, e.ItemId }, "IX_ActualPostings_Item");
            entity.Property(e => e.GLCode).HasMaxLength(30);
            entity.Property(e => e.GLType).HasMaxLength(20);
            entity.Property(e => e.ItemCode).HasMaxLength(50);
            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Source).HasMaxLength(10);
            entity.Property(e => e.SourceFile).HasMaxLength(260);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CreatedBy).HasMaxLength(100);

            entity.HasOne(d => d.Entity).WithMany()
                .HasForeignKey(d => d.EntityId)
                .HasConstraintName("FK_ActualPostings_Entity");

            entity.HasOne(d => d.Item).WithMany()
                .HasForeignKey(d => d.ItemId)
                .HasConstraintName("FK_ActualPostings_Item");

            entity.HasOne(d => d.ImportBatch).WithMany(p => p.ActualPostings)
                .HasForeignKey(d => d.ImportBatchId)
                .HasConstraintName("FK_ActualPostings_Batch");
        });

        modelBuilder.Entity<ActualForecasts>(entity =>
        {
            entity.HasKey(e => e.ActualForecastId);
            entity.ToTable("ActualForecasts", "core");
            entity.HasIndex(e => new { e.BudgetYear, e.EntityId, e.GLCode }, "UQ_ActualForecasts").IsUnique();
            entity.Property(e => e.GLCode).HasMaxLength(30);
            entity.Property(e => e.GLType).HasMaxLength(20);
            entity.Property(e => e.ForecastRemaining).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Notes).HasMaxLength(400);
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.UpdatedBy).HasMaxLength(100);

            entity.HasOne(d => d.Entity).WithMany()
                .HasForeignKey(d => d.EntityId)
                .HasConstraintName("FK_ActualForecasts_Entity");
        });

        modelBuilder.Entity<HrActualPostings>(entity =>
        {
            entity.HasKey(e => e.HrActualPostingId);
            entity.ToTable("HrActualPostings", "core");
            entity.HasIndex(e => new { e.BudgetYear, e.EntityId, e.EmployeeCostId }, "IX_HrActualPostings_Scope");
            entity.Property(e => e.EmployeeCode).HasMaxLength(50);
            entity.Property(e => e.GLCode).HasMaxLength(30);
            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Source).HasMaxLength(10).HasDefaultValue("HR_EMP");
            entity.Property(e => e.SourceFile).HasMaxLength(260);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CreatedBy).HasMaxLength(100);

            entity.HasOne(d => d.Entity).WithMany()
                .HasForeignKey(d => d.EntityId)
                .HasConstraintName("FK_HrActualPostings_Entity");

            entity.HasOne(d => d.ImportBatch).WithMany()
                .HasForeignKey(d => d.ImportBatchId)
                .HasConstraintName("FK_HrActualPostings_Batch");
        });

        // Security hardening: only the digest of a reset token is persisted.
        modelBuilder.Entity<PasswordResetRequests>(entity =>
        {
            entity.Property(e => e.TokenHash).HasMaxLength(100);
            entity.HasIndex(e => e.TokenHash, "IX_PasswordResetRequests_TokenHash");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
