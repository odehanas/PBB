using System.Threading.RateLimiting;
using GovBudget.Models;
using GovBudget.Utils;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// MVC. The form-permission filter enforces the Roles & Rights matrix on every request,
// so a view-only role cannot add, edit or delete even by posting directly to an action.
// AutoValidateAntiforgeryToken makes CSRF validation the default for every unsafe verb,
// so a forgotten [ValidateAntiForgeryToken] can no longer leave a hole.
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add<GovBudget.Utils.FormPermissionFilter>();
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
});

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = ".GovBudget.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
});

// Data-protection keys back the auth cookie and antiforgery tokens. Persisting them keeps
// sessions valid across restarts and across instances instead of regenerating on each boot.
try
{
    var keyRing = new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys"));
    keyRing.Create();
    builder.Services.AddDataProtection()
        .SetApplicationName("GovBudget")
        .PersistKeysToFileSystem(keyRing);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Data protection key persistence unavailable: {ex.Message}");
}

// Brute-force / credential-stuffing brake on the sign-in and reset endpoints, keyed by
// client IP. Account lockout in AccountController handles the per-user case.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

// DbContext from appsettings.json
var cs = builder.Configuration.GetConnectionString("DefaultConnection")
         ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<GovBudgetContext>(options =>
    options.UseSqlServer(cs, sql =>
    {
        // Retry transient connection/timeout faults (common against the remote host).
        sql.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorNumbersToAdd: null);
        // Reports/templates read master data; allow more than the default 30s.
        sql.CommandTimeout(120);
    }));

// Auth + Roles
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(opt =>
    {
        opt.LoginPath = "/Account/Login";
        opt.LogoutPath = "/Account/Logout";
        opt.AccessDeniedPath = "/Account/Denied";

        // Session lifetime: 60 minutes of inactivity, refreshed while the user works.
        opt.ExpireTimeSpan = TimeSpan.FromMinutes(60);
        opt.SlidingExpiration = true;

        opt.Cookie.Name = ".GovBudget.Auth";
        opt.Cookie.HttpOnly = true;                              // not readable from script
        opt.Cookie.SecurePolicy = CookieSecurePolicy.Always;     // HTTPS only
        opt.Cookie.SameSite = SameSiteMode.Lax;                  // cross-site POST blocked
        opt.Cookie.IsEssential = true;

        // Deactivations, role changes and password resets take effect within ~2 minutes.
        opt.Events = new CookieAuthenticationEvents
        {
            OnValidatePrincipal = GovBudget.Services.CookieSecurityValidator.ValidateAsync
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("ADMIN", "SYSADMIN"));
    options.AddPolicy("SysAdminOnly", policy => policy.RequireRole("SYSADMIN"));
});

// Form-level rights (Roles & Permissions screen). Cached in memory and invalidated
// whenever an administrator saves the matrix.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<GovBudget.Services.IPermissionService, GovBudget.Services.PermissionService>();

// Session for holding the selected Year/Entity/Department
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(60);
    options.Cookie.Name = ".GovBudget.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.IsEssential = true;
});

// In-app assistant. The API key comes from user secrets, the platform configuration or the
// OPENAI_API_KEY environment variable; without one the widget stays visible but explains
// that it is not configured.
builder.Services.Configure<GovBudget.Services.Assistant.AssistantOptions>(
    builder.Configuration.GetSection(GovBudget.Services.Assistant.AssistantOptions.SectionName));
builder.Services.PostConfigure<GovBudget.Services.Assistant.AssistantOptions>(opt =>
{
    if (string.IsNullOrWhiteSpace(opt.ApiKey))
    {
        opt.ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
    }
});

var assistantTimeout = TimeSpan.FromSeconds(
    builder.Configuration.GetValue<int?>("Assistant:TimeoutSeconds") ?? 90);
builder.Services.AddHttpClient(GovBudget.Services.Assistant.OpenAIChatAssistantService.HttpClientName,
    c => c.Timeout = assistantTimeout);
builder.Services.AddHttpClient(GovBudget.Services.Assistant.OecdKnowledgeToolProvider.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("GovBudget-Assistant/1.0");
});

builder.Services.AddScoped<GovBudget.Services.Assistant.IAssistantToolProvider, GovBudget.Services.Assistant.BudgetDataToolProvider>();
builder.Services.AddScoped<GovBudget.Services.Assistant.IAssistantToolProvider, GovBudget.Services.Assistant.OecdKnowledgeToolProvider>();
builder.Services.AddScoped<GovBudget.Services.Assistant.IChatAssistantService, GovBudget.Services.Assistant.OpenAIChatAssistantService>();

// Password reset delivery. NoOp for now (admin shares the link manually);
// swap for an SMTP implementation later without touching callers.
builder.Services.AddScoped<GovBudget.Services.IPasswordResetNotifier, GovBudget.Services.NoOpPasswordResetNotifier>();

var app = builder.Build();

// Credential hardening: add the hash/lockout columns when missing and convert any
// remaining clear-text password into a PBKDF2 hash. Runs before anything else touches
// AppUsers so the new columns are always present.
try
{
    using var securityScope = app.Services.CreateScope();
    var securityDb = securityScope.ServiceProvider.GetRequiredService<GovBudgetContext>();
    if (securityDb.Database.CanConnect())
    {
        GovBudget.Services.SecurityUpgrade.Run(securityDb, app.Logger);
    }
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Security upgrade failed at startup.");
}

// Ensure the core budget categories always exist (Budget Entry relies on these codes).
try
{
    using var seedScope = app.Services.CreateScope();
    var seedDb = seedScope.ServiceProvider.GetRequiredService<GovBudgetContext>();
    if (seedDb.Database.CanConnect())
    {
        var required = new (string Code, string Name)[]
        {
            ("REVENUE", "Revenue"),
            ("OPEX", "Operating Expenditure"),
            ("CAPEX", "Capital Expenditure")
        };

        var existingCodes = seedDb.Categories
            .Select(c => c.CategoryCode)
            .ToList()
            .Select(c => c.Trim().ToUpperInvariant())
            .ToHashSet();

        var toAdd = required
            .Where(r => !existingCodes.Contains(r.Code))
            .Select(r => new GovBudget.Models.Categories { CategoryCode = r.Code, CategoryName = r.Name })
            .ToList();

        if (toAdd.Count > 0)
        {
            seedDb.Categories.AddRange(toAdd);
            seedDb.SaveChanges();
            app.Logger.LogInformation("Seeded {Count} missing budget categories.", toAdd.Count);
        }
    }
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Category seed/guard failed at startup.");
}

// Roles & form permissions: create the tables when missing and seed defaults that match
// the access rules in force before permissions became configurable.
try
{
    using var permScope = app.Services.CreateScope();
    var permDb = permScope.ServiceProvider.GetRequiredService<GovBudgetContext>();
    if (permDb.Database.CanConnect())
    {
        GovBudget.Services.PermissionSeeder.Run(permDb);
    }
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Role/permission seed/guard failed at startup.");
}

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<GovBudgetContext>();

    try
    {
        db.Database.ExecuteSqlRaw("""
IF OBJECT_ID(N'core.AppUsers', N'U') IS NOT NULL
AND COL_LENGTH('core.AppUsers', 'EntityId') IS NULL
BEGIN
    ALTER TABLE core.AppUsers ADD EntityId INT NULL;
END;

IF OBJECT_ID(N'core.AppUsers', N'U') IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_AppUser_Entity'
      AND parent_object_id = OBJECT_ID(N'core.AppUsers')
)
BEGIN
    ALTER TABLE core.AppUsers
    ADD CONSTRAINT FK_AppUser_Entity
    FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId);
END;

IF OBJECT_ID(N'core.BudgetSubmissions', N'U') IS NOT NULL
AND COL_LENGTH('core.BudgetSubmissions', 'VersionNo') IS NULL
BEGIN
    ALTER TABLE core.BudgetSubmissions ADD VersionNo INT NOT NULL CONSTRAINT DF_BudgetSubmissions_VersionNo DEFAULT(1);
END;

IF OBJECT_ID(N'core.BudgetSubmissions', N'U') IS NOT NULL
AND COL_LENGTH('core.BudgetSubmissions', 'ParentSubmissionId') IS NULL
BEGIN
    ALTER TABLE core.BudgetSubmissions ADD ParentSubmissionId BIGINT NULL;
END;

IF OBJECT_ID(N'core.BudgetSubmissions', N'U') IS NOT NULL
AND COL_LENGTH('core.BudgetSubmissions', 'ReturnedAt') IS NULL
BEGIN
    ALTER TABLE core.BudgetSubmissions ADD ReturnedAt DATETIME2 NULL;
END;

IF OBJECT_ID(N'core.BudgetSubmissions', N'U') IS NOT NULL
AND COL_LENGTH('core.BudgetSubmissions', 'ReturnedBy') IS NULL
BEGIN
    ALTER TABLE core.BudgetSubmissions ADD ReturnedBy NVARCHAR(100) NULL;
END;

IF OBJECT_ID(N'core.BudgetSubmissions', N'U') IS NOT NULL
AND COL_LENGTH('core.BudgetSubmissions', 'ReturnNote') IS NULL
BEGIN
    ALTER TABLE core.BudgetSubmissions ADD ReturnNote NVARCHAR(500) NULL;
END;

IF OBJECT_ID(N'core.BudgetSubmissions', N'U') IS NOT NULL
AND COL_LENGTH('core.BudgetSubmissions', 'SysApprovedAt') IS NULL
BEGIN
    ALTER TABLE core.BudgetSubmissions ADD SysApprovedAt DATETIME2 NULL;
END;

IF OBJECT_ID(N'core.BudgetSubmissions', N'U') IS NOT NULL
AND COL_LENGTH('core.BudgetSubmissions', 'SysApprovedBy') IS NULL
BEGIN
    ALTER TABLE core.BudgetSubmissions ADD SysApprovedBy NVARCHAR(100) NULL;
END;

IF OBJECT_ID(N'core.BudgetSubmissions', N'U') IS NOT NULL
AND COL_LENGTH('core.BudgetSubmissions', 'SysApprovalNote') IS NULL
BEGIN
    ALTER TABLE core.BudgetSubmissions ADD SysApprovalNote NVARCHAR(500) NULL;
END;

IF OBJECT_ID(N'core.BudgetSubmissions', N'U') IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_BudgetSubmissions_Parent'
      AND parent_object_id = OBJECT_ID(N'core.BudgetSubmissions')
)
BEGIN
    ALTER TABLE core.BudgetSubmissions
    ADD CONSTRAINT FK_BudgetSubmissions_Parent FOREIGN KEY (ParentSubmissionId) REFERENCES core.BudgetSubmissions(SubmissionId);
END;

IF OBJECT_ID(N'core.BudgetSubmissions', N'U') IS NOT NULL
AND EXISTS (
    SELECT 1
    FROM sys.key_constraints
    WHERE name = N'UQ_BudgetSubmissions_Scope'
      AND parent_object_id = OBJECT_ID(N'core.BudgetSubmissions')
)
BEGIN
    ALTER TABLE core.BudgetSubmissions DROP CONSTRAINT UQ_BudgetSubmissions_Scope;
END;

IF OBJECT_ID(N'core.BudgetSubmissions', N'U') IS NOT NULL
AND EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UQ_BudgetSubmissions_Scope'
      AND object_id = OBJECT_ID(N'core.BudgetSubmissions')
)
BEGIN
    DROP INDEX UQ_BudgetSubmissions_Scope ON core.BudgetSubmissions;
END;

IF OBJECT_ID(N'core.BudgetSubmissions', N'U') IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM sys.key_constraints
    WHERE name = N'UQ_BudgetSubmissions_ScopeVersion'
      AND parent_object_id = OBJECT_ID(N'core.BudgetSubmissions')
)
BEGIN
    ALTER TABLE core.BudgetSubmissions
    ADD CONSTRAINT UQ_BudgetSubmissions_ScopeVersion UNIQUE (BudgetYear, EntityId, DepartmentId, CategoryId, VersionNo);
END;

IF OBJECT_ID(N'core.BudgetSubmissionLines', N'U') IS NULL
BEGIN
    CREATE TABLE core.BudgetSubmissionLines (
        SubmissionLineId BIGINT IDENTITY(1,1) PRIMARY KEY,
        SubmissionId BIGINT NOT NULL,
        SourceBudgetLineId BIGINT NOT NULL,
        BudgetYear INT NOT NULL,
        EntityId INT NOT NULL,
        DepartmentId INT NOT NULL,
        CategoryId INT NOT NULL,
        ItemId INT NOT NULL,
        ProgramId INT NULL,
        ActivityId INT NULL,
        ProjectId INT NULL,
        Quantity DECIMAL(18,4) NOT NULL,
        UnitPrice DECIMAL(18,4) NOT NULL,
        Amount DECIMAL(18,2) NOT NULL,
        DistributionMode NVARCHAR(10) NOT NULL,
        M01 DECIMAL(18,2) NOT NULL,
        M02 DECIMAL(18,2) NOT NULL,
        M03 DECIMAL(18,2) NOT NULL,
        M04 DECIMAL(18,2) NOT NULL,
        M05 DECIMAL(18,2) NOT NULL,
        M06 DECIMAL(18,2) NOT NULL,
        M07 DECIMAL(18,2) NOT NULL,
        M08 DECIMAL(18,2) NOT NULL,
        M09 DECIMAL(18,2) NOT NULL,
        M10 DECIMAL(18,2) NOT NULL,
        M11 DECIMAL(18,2) NOT NULL,
        M12 DECIMAL(18,2) NOT NULL,
        F1_Percent DECIMAL(9,4) NOT NULL,
        F1_Amount DECIMAL(18,2) NOT NULL,
        F2_Percent DECIMAL(9,4) NOT NULL,
        F2_Amount DECIMAL(18,2) NOT NULL,
        Dep_Method NVARCHAR(20) NOT NULL,
        Dep_LifeMonths INT NOT NULL,
        Dep_StartMonth TINYINT NOT NULL,
        CapexAssetType NVARCHAR(20) NULL,
        Notes NVARCHAR(500) NULL,
        Description NVARCHAR(300) NOT NULL,
        CreatedAt DATETIME2 NOT NULL,
        CreatedBy NVARCHAR(100) NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy NVARCHAR(100) NULL,
        DocFileName NVARCHAR(260) NULL,
        DocContentType NVARCHAR(100) NULL,
        DocSizeBytes INT NULL,
        DocContent VARBINARY(MAX) NULL,
        DocUploadedAt DATETIME2 NULL,
        DocUploadedBy NVARCHAR(100) NULL,
        SnapshottedAt DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
        SnapshottedBy NVARCHAR(100) NULL,
        CONSTRAINT UQ_BudgetSubmissionLines UNIQUE (SubmissionId, SourceBudgetLineId),
        CONSTRAINT FK_BudgetSubmissionLines_Submission FOREIGN KEY (SubmissionId) REFERENCES core.BudgetSubmissions(SubmissionId)
    );
END;

IF OBJECT_ID(N'core.BudgetLines', N'U') IS NOT NULL
AND COL_LENGTH('core.BudgetLines', 'CapexAssetType') IS NULL
BEGIN
    ALTER TABLE core.BudgetLines ADD CapexAssetType NVARCHAR(20) NULL;
END;

IF OBJECT_ID(N'core.BudgetSubmissionLines', N'U') IS NOT NULL
AND COL_LENGTH('core.BudgetSubmissionLines', 'CapexAssetType') IS NULL
BEGIN
    ALTER TABLE core.BudgetSubmissionLines ADD CapexAssetType NVARCHAR(20) NULL;
END;

IF OBJECT_ID(N'core.DOF_CombindBudget_Final', N'U') IS NOT NULL
AND COL_LENGTH('core.DOF_CombindBudget_Final', 'CapexAssetType') IS NULL
BEGIN
    ALTER TABLE core.DOF_CombindBudget_Final ADD CapexAssetType NVARCHAR(20) NULL;
END;

IF OBJECT_ID(N'core.BudgetRevisionRequests', N'U') IS NULL
BEGIN
    CREATE TABLE core.BudgetRevisionRequests (
        RequestId BIGINT IDENTITY(1,1) PRIMARY KEY,
        SubmissionId BIGINT NOT NULL,
        ActionType NVARCHAR(20) NOT NULL,
        Note NVARCHAR(500) NULL,
        RequestedAt DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
        RequestedBy NVARCHAR(100) NULL,
        CONSTRAINT FK_BudgetRevisionRequests_Submission FOREIGN KEY (SubmissionId) REFERENCES core.BudgetSubmissions(SubmissionId)
    );
END;

IF OBJECT_ID(N'core.HistoricalGlActuals', N'U') IS NULL
BEGIN
    CREATE TABLE core.HistoricalGlActuals (
        HistoricalActualId BIGINT IDENTITY(1,1) PRIMARY KEY,
        BudgetYear INT NOT NULL,
        EntityId INT NOT NULL,
        DepartmentId INT NOT NULL,
        GLCode NVARCHAR(30) NOT NULL,
        GLType NVARCHAR(20) NULL,
        Amount DECIMAL(18,2) NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
        CreatedBy NVARCHAR(100) NULL,
        SourceFile NVARCHAR(260) NULL,
        CONSTRAINT UQ_HistoricalGlActuals_Scope UNIQUE (BudgetYear, EntityId, DepartmentId, GLCode),
        CONSTRAINT FK_HistoricalGlActuals_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId),
        CONSTRAINT FK_HistoricalGlActuals_Department FOREIGN KEY (DepartmentId) REFERENCES core.Departments(DepartmentId)
    );
END;

IF OBJECT_ID(N'core.HistoricalGlActuals', N'U') IS NOT NULL
AND COL_LENGTH('core.HistoricalGlActuals', 'GLType') IS NULL
BEGIN
    ALTER TABLE core.HistoricalGlActuals ADD GLType NVARCHAR(20) NULL;
END;

IF OBJECT_ID(N'core.MidYearGlActualForecasts', N'U') IS NULL
BEGIN
    CREATE TABLE core.MidYearGlActualForecasts (
        MidYearId BIGINT IDENTITY(1,1) PRIMARY KEY,
        BudgetYear INT NOT NULL,
        EntityId INT NOT NULL,
        GLCode NVARCHAR(30) NOT NULL,
        GLType NVARCHAR(20) NOT NULL,
        ActualH1Amount DECIMAL(18,2) NOT NULL,
        ForecastH2Amount DECIMAL(18,2) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
        CreatedBy NVARCHAR(100) NULL,
        ForecastUpdatedAt DATETIME2 NULL,
        ForecastUpdatedBy NVARCHAR(100) NULL,
        SourceFile NVARCHAR(260) NULL,
        CONSTRAINT UQ_MidYearGlActualForecasts_Scope UNIQUE (BudgetYear, EntityId, GLCode),
        CONSTRAINT FK_MidYearGlActualForecasts_Entity FOREIGN KEY (EntityId) REFERENCES core.Entities(EntityId)
    );
END;
IF OBJECT_ID(N'core.PasswordResetRequests', N'U') IS NULL
BEGIN
    CREATE TABLE core.PasswordResetRequests (
        ResetRequestId BIGINT IDENTITY(1,1) PRIMARY KEY,
        UserName NVARCHAR(100) NOT NULL,
        UserId INT NULL,
        EntityId INT NULL,
        ContactInfo NVARCHAR(200) NULL,
        Note NVARCHAR(500) NULL,
        Status NVARCHAR(20) NOT NULL DEFAULT('Pending'),
        RequestSource NVARCHAR(20) NULL,
        RequestedAt DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
        Token NVARCHAR(128) NULL,
        TokenExpiresAt DATETIME2 NULL,
        TokenUsedAt DATETIME2 NULL,
        IssuedAt DATETIME2 NULL,
        IssuedBy NVARCHAR(100) NULL,
        CompletedAt DATETIME2 NULL,
        RejectedAt DATETIME2 NULL,
        RejectedBy NVARCHAR(100) NULL,
        AdminNote NVARCHAR(500) NULL
    );
    CREATE INDEX IX_PasswordResetRequests_Token ON core.PasswordResetRequests(Token);
END;
""");
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Development schema check failed.");
    }
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseSecurityHeaders(app.Configuration["Security:ContentSecurityPolicy"]);
app.UseStaticFiles();

app.UseRouting();
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();
app.UseSession();

// An administrator-issued password can only be used to choose a new one.
app.UseForcePasswordChange();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
