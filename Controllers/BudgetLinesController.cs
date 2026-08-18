using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ClosedXML.Excel;
using GovBudget.Models;
using GovBudget.Services;
using GovBudget.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Controllers
{
    [Authorize]
    public class BudgetLinesController : Controller
    {
        private readonly GovBudgetContext _db;
        private const int CapexAttachmentMaxBytes = 5 * 1024 * 1024;

        // Session key holding the parsed bulk-upload rows awaiting overwrite confirmation.
        private const string PendingBulkKey = "PendingBulkBudgetUpload";

        // How a budget line was created. Used so bulk uploads only replace uploaded data
        // and leave manually-entered lines untouched unless the user confirms otherwise.
        private const string EntrySourceManual = "MANUAL";
        private const string EntrySourceUpload = "UPLOAD";
        // Set when an existing line is changed by hand, so the grid can distinguish a
        // freshly hand-entered line (MANUAL) from one that was later edited (EDITED).
        // Both are treated as "manual" (protected) by the bulk-upload overwrite logic.
        private const string EntrySourceEdited = "EDITED";

        public BudgetLinesController(GovBudgetContext db) { _db = db; }

        // ---------- GET ----------
        public async Task<IActionResult> Entry(string category = "OPEX", long? editId = null)
        {
            category = (category ?? "OPEX").ToUpperInvariant();
            ViewBag.CategoryCode = category;

            // Read context (force the user to pick it once)
            var year = HttpContext.Session.GetInt("ctxYear") ?? DateTime.Now.Year;
            var entityId = HttpContext.Session.GetInt("ctxEntityId");
            var deptId = HttpContext.Session.GetInt("ctxDeptId");
            if (!(entityId.HasValue && deptId.HasValue))
                return RedirectToAction("Select", "Context");

            var dep = await _db.Departments.Include(d => d.Entity).FirstOrDefaultAsync(d => d.DepartmentId == deptId.Value);
            if (dep == null)
            {
                TempData["Error"] = "Your selected cost center no longer exists. Please pick your budget context again.";
                HttpContext.Session.Remove("ctxDeptId");
                return RedirectToAction("Select", "Context");
            }
            var entityCode = dep.Entity?.EntityCode ?? "?";
            ViewBag.ContextLabel = $"{year} — {entityCode}/{dep.DeptCode} {dep.DeptName}";

            var cat = await _db.Categories.FirstOrDefaultAsync(c => c.CategoryCode == category);
            if (cat == null)
            {
                TempData["Error"] = $"Budget category '{category}' is not configured. Please contact your administrator to add it under Categories.";
                return RedirectToAction("Index", "Home");
            }

            var submission = await _db.BudgetSubmissions.AsNoTracking()
                .Where(s => s.BudgetYear == year
                            && s.EntityId == entityId.Value
                            && s.DepartmentId == deptId.Value
                            && s.CategoryId == cat.CategoryId)
                .OrderByDescending(s => s.VersionNo)
                .FirstOrDefaultAsync();
            var submissionStatus = submission?.Status ?? "Draft";
            // A role without Add/Edit rights on Budget Entry gets the same read-only screen
            // as a submitted budget, so no save controls are offered at all.
            var readOnlyRole = !HttpContext.CanAdd() && !HttpContext.CanEdit();
            var isLocked = IsLockedStatus(submissionStatus) || readOnlyRole;
            ViewBag.IsReadOnlyRole = readOnlyRole;
            ViewBag.Submission = submission;
            ViewBag.SubmissionStatus = submissionStatus;
            ViewBag.SubmissionVersion = submission?.VersionNo ?? 0;
            ViewBag.IsLocked = isLocked;
            ViewBag.LineCount = await _db.BudgetLines.AsNoTracking()
                .CountAsync(b => b.BudgetYear == year
                                 && b.EntityId == entityId.Value
                                 && b.DepartmentId == deptId.Value
                                 && b.CategoryId == cat.CategoryId);

            BudgetLines vm;
            if (editId.HasValue && editId.Value > 0)
            {
                if (isLocked)
                {
                    TempData["Error"] = "Budget has been submitted and is locked. Editing is disabled.";
                    return RedirectToAction(nameof(Entry), new { category });
                }

                var existing = await _db.BudgetLines.AsNoTracking()
                    .FirstOrDefaultAsync(b => b.BudgetLineId == editId.Value
                                           && b.BudgetYear == year
                                           && b.EntityId == entityId.Value
                                           && b.DepartmentId == deptId.Value
                                           && b.CategoryId == cat.CategoryId);

                if (existing == null)
                {
                    TempData["Error"] = "Budget line not found for the selected context.";
                    return RedirectToAction(nameof(Entry), new { category });
                }

                vm = existing;

                if (category == "CAPEX")
                {
                    ViewBag.ExistingDocFileName = await _db.BudgetLineDocuments.AsNoTracking()
                        .Where(d => d.BudgetLineId == vm.BudgetLineId)
                        .Select(d => d.FileName)
                        .FirstOrDefaultAsync();
                }
            }
            else
            {
                vm = new BudgetLines
                {
                    BudgetYear = year,
                    EntityId = entityId.Value,
                    DepartmentId = deptId.Value,
                    CategoryId = cat.CategoryId,
                    DistributionMode = "EQUAL",
                    Dep_Method = "STRAIGHT",
                    Dep_LifeMonths = 0,
                    Dep_StartMonth = 1
                };
            }

            await PopulateItemsByCategory(category);
            await PopulatePrograms(entityId.Value);
            await PopulateActivities(deptId.Value, vm.ProgramId);
            await PopulateProjects(deptId.Value);

            ViewBag.Recent = await (
                from b in _db.BudgetLines.AsNoTracking()
                join item in _db.Items.AsNoTracking() on b.ItemId equals item.ItemId
                join act in _db.Activities.AsNoTracking() on b.ActivityId equals act.ActivityId into actJoin
                from act in actJoin.DefaultIfEmpty()
                join proj in _db.Projects.AsNoTracking() on b.ProjectId equals proj.ProjectId into projJoin
                from proj in projJoin.DefaultIfEmpty()
                join doc in _db.BudgetLineDocuments.AsNoTracking() on b.BudgetLineId equals doc.BudgetLineId into docJoin
                from doc in docJoin.DefaultIfEmpty()
                where b.CategoryId == cat.CategoryId
                   && b.BudgetYear == year
                   && b.EntityId == entityId.Value
                   && b.DepartmentId == deptId.Value
                orderby b.BudgetLineId descending
                select new
                {
                    b.BudgetLineId,
                    ItemCode = item.ItemCode,
                    b.Description,
                    b.EntrySource,
                    ActivityCode = act != null ? act.ActivityCode : "",
                    ProjectCode = proj != null ? proj.ProjectCode : "",
                    b.Quantity,
                    b.UnitPrice,
                    b.Amount,
                    b.F1_Percent,
                    b.F1_Amount,
                    b.F2_Percent,
                    b.F2_Amount,
                    DocFileName = doc != null ? doc.FileName : null
                }
            ).Take(100).ToListAsync();

            var canManageItems = IsGlobalAdmin();
            ViewBag.CanManageItems = canManageItems;
            var canManageMaster = User.IsInRole("ADMIN") || User.IsInRole("SYSADMIN");
            ViewBag.CanManageMasterData = canManageMaster;
            if (canManageItems)
            {
                ViewBag.ItemGLAccounts = new SelectList(await _db.GLAccounts.AsNoTracking()
                    .Where(g => g.GLType == category)
                    .OrderBy(g => g.GLCode)
                    .Select(g => new { g.GLAccountId, Display = g.GLCode + " - " + g.GLName })
                    .ToListAsync(), "GLAccountId", "Display");
            }
            if (canManageMaster)
            {
                ViewBag.EntityDepartments = new SelectList(await _db.Departments.AsNoTracking()
                    .Where(d => d.EntityId == entityId.Value)
                    .OrderBy(d => d.DeptCode)
                    .Select(d => new { d.DepartmentId, Display = d.DeptCode + " - " + d.DeptName })
                    .ToListAsync(), "DepartmentId", "Display", deptId.Value);
            }

            return View(vm);
        }

        private bool IsGlobalAdmin()
        {
            var entityClaim = User.Claims.FirstOrDefault(c => c.Type == "EntityId")?.Value;
            var hasEntityScope = int.TryParse(entityClaim, out var entityId) && entityId > 0;
            return User.IsInRole("SYSADMIN") || (User.IsInRole("ADMIN") && !hasEntityScope);
        }

        // ---------- POST ----------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Entry(string currentCategory, BudgetLines model, IFormFile? capexAttachment)
        {
            currentCategory = (currentCategory ?? "OPEX").ToUpperInvariant();
            ViewBag.CategoryCode = currentCategory;

            var userName = User.Identity?.Name ?? "Unknown";

            // Force context from session (never trust posted)
            model.BudgetYear = HttpContext.Session.GetInt("ctxYear") ?? model.BudgetYear;
            model.EntityId = HttpContext.Session.GetInt("ctxEntityId") ?? model.EntityId;
            model.DepartmentId = HttpContext.Session.GetInt("ctxDeptId") ?? model.DepartmentId;

            // If the context was lost (e.g. session expired), send the user back to pick it
            // rather than re-rendering a form whose lookups have no valid context.
            if (model.EntityId <= 0 || model.DepartmentId <= 0)
            {
                TempData["Error"] = "Your session expired. Please select your budget context again, then re-enter the line.";
                return RedirectToAction("Select", "Context");
            }

            // Auto-assign ProgramId from Activity if missing
            if (model.ActivityId.HasValue && model.ActivityId.Value > 0)
            {
                var act = await _db.Activities.FindAsync(model.ActivityId.Value);
                if (act != null)
                {
                    model.ProgramId = act.ProgramId;
                }
            }

            model.UpdatedBy = userName;
            model.UpdatedAt = DateTime.UtcNow;

            // Remove validation for navigation properties (fix for silent validation failure)
            ModelState.Remove(nameof(model.Entity));
            ModelState.Remove(nameof(model.Department));
            ModelState.Remove(nameof(model.Category));
            ModelState.Remove(nameof(model.Item));
            ModelState.Remove(nameof(model.Program));
            ModelState.Remove(nameof(model.Activity));
            ModelState.Remove(nameof(model.Project));
            ModelState.Remove(nameof(model.DistributionMode));
            ModelState.Remove(nameof(model.Dep_Method));
            ModelState.Remove(nameof(model.BudgetLineDocuments));

            // Validate mandatory fields
            if (model.EntityId <= 0) ModelState.AddModelError("EntityId", "Context missing: Entity.");
            if (model.DepartmentId <= 0) ModelState.AddModelError("DepartmentId", "Context missing: Department.");
            if (model.BudgetYear <= 0) ModelState.AddModelError("BudgetYear", "Year is required.");
            if (model.ItemId <= 0) ModelState.AddModelError("ItemId", "Item is required.");
            if (!model.ActivityId.HasValue || model.ActivityId.Value <= 0) ModelState.AddModelError("ActivityId", "Activity is required.");
            if (string.IsNullOrWhiteSpace(model.Description))
                ModelState.AddModelError("Description", "Description is required.");
            if (model.Quantity < 0) ModelState.AddModelError("Quantity", "Quantity cannot be negative.");
            if (model.UnitPrice < 0) ModelState.AddModelError("UnitPrice", "Unit Price cannot be negative.");
            if (model.Amount < 0) ModelState.AddModelError("Amount", "Amount cannot be negative.");

            // Enforce category by code
            var cat = await _db.Categories.FirstAsync(c => c.CategoryCode == currentCategory);
            model.CategoryId = cat.CategoryId;

            if (await IsBudgetLocked(model.BudgetYear, model.EntityId, model.DepartmentId, model.CategoryId))
            {
                TempData["Error"] = "Budget has been submitted and is locked. Changes are not allowed.";
                return RedirectToAction(nameof(Entry), new { category = currentCategory });
            }

            // Always enforce Amount = Qty * UnitPrice
            model.Amount = Math.Round(model.Quantity * model.UnitPrice, 2, MidpointRounding.AwayFromZero);

            // Distribution
            if (string.Equals(model.DistributionMode, "EQUAL", StringComparison.OrdinalIgnoreCase))
            {
                var (m1, m2, m3, m4, m5, m6, m7, m8, m9, m10, m11, m12) = BudgetCalcService.Equal12(model.Amount);
                model.M01 = m1; model.M02 = m2; model.M03 = m3; model.M04 = m4; model.M05 = m5; model.M06 = m6;
                model.M07 = m7; model.M08 = m8; model.M09 = m9; model.M10 = m10; model.M11 = m11; model.M12 = m12;
            }
            else
            {
                var sum = BudgetCalcService.SumMonths(model);
                if (Math.Round(sum, 2) != Math.Round(model.Amount, 2))
                    ModelState.AddModelError("", "Manual distribution must sum to Amount.");
            }

            // Forecasts
            model.F1_Amount = BudgetCalcService.ComputeForecast(model.Amount, model.F1_Percent, model.F1_Amount);
            model.F2_Amount = BudgetCalcService.ComputeForecast(model.Amount, model.F2_Percent, model.F2_Amount);

            // CAPEX defaults for non‑CAPEX
            if (currentCategory != "CAPEX")
            {
                model.CapexAssetType = null;
                model.Dep_Method = "STRAIGHT";
                model.Dep_LifeMonths = 0;
                model.Dep_StartMonth = 1;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(model.CapexAssetType))
                {
                    model.CapexAssetType = null;
                    ModelState.AddModelError(nameof(model.CapexAssetType), "Asset Type is required for CAPEX.");
                }
                else
                {
                    var normalized = model.CapexAssetType.Trim().ToUpperInvariant();
                    if (normalized != "NEW" && normalized != "REPLACEMENT")
                    {
                        ModelState.AddModelError(nameof(model.CapexAssetType), "Please select New or Replacement.");
                    }
                    else
                    {
                        model.CapexAssetType = normalized;
                    }
                }
                BudgetCalcService.EnsureCapexDefaults(model);
            }

            if (currentCategory == "CAPEX" && capexAttachment != null && capexAttachment.Length > 0)
            {
                if (capexAttachment.Length > CapexAttachmentMaxBytes)
                {
                    ModelState.AddModelError("", $"Supporting document must be <= {CapexAttachmentMaxBytes / (1024 * 1024)} MB.");
                }

                var ext = Path.GetExtension(capexAttachment.FileName);
                if (!IsAllowedCapexAttachmentExtension(ext))
                {
                    ModelState.AddModelError("", "Supporting document must be a PDF or an image (JPG/PNG).");
                }
            }

            if (!ModelState.IsValid)
            {
                await ReloadViewData(model, currentCategory);
                return View(model);
            }

            var isUpdate = model.BudgetLineId > 0;

            try
            {
                BudgetLines target;
                if (isUpdate)
                {
                    target = await _db.BudgetLines.FirstOrDefaultAsync(b => b.BudgetLineId == model.BudgetLineId
                                                                         && b.BudgetYear == model.BudgetYear
                                                                         && b.EntityId == model.EntityId
                                                                         && b.DepartmentId == model.DepartmentId
                                                                         && b.CategoryId == model.CategoryId)
                             ?? throw new InvalidOperationException("Budget line not found for update.");

                    target.ItemId = model.ItemId;
                    target.ProgramId = model.ProgramId;
                    target.ActivityId = model.ActivityId;
                    target.ProjectId = model.ProjectId;
                    target.Description = model.Description;
                    target.Quantity = model.Quantity;
                    target.UnitPrice = model.UnitPrice;
                    target.Amount = model.Amount;
                    target.DistributionMode = model.DistributionMode;
                    target.M01 = model.M01; target.M02 = model.M02; target.M03 = model.M03; target.M04 = model.M04; target.M05 = model.M05; target.M06 = model.M06;
                    target.M07 = model.M07; target.M08 = model.M08; target.M09 = model.M09; target.M10 = model.M10; target.M11 = model.M11; target.M12 = model.M12;
                    target.F1_Percent = model.F1_Percent;
                    target.F1_Amount = model.F1_Amount;
                    target.F2_Percent = model.F2_Percent;
                    target.F2_Amount = model.F2_Amount;
                    target.Dep_Method = model.Dep_Method;
                    target.Dep_LifeMonths = model.Dep_LifeMonths;
                    target.Dep_StartMonth = model.Dep_StartMonth;
                    target.CapexAssetType = model.CapexAssetType;
                    target.Notes = model.Notes;
                    target.UpdatedAt = DateTime.UtcNow;
                    target.UpdatedBy = userName;
                    // A hand edit marks the line as EDITED (still protected like MANUAL by
                    // the bulk-upload overwrite logic). This lets the grid show that an
                    // uploaded/manual line was later changed by the user.
                    target.EntrySource = EntrySourceEdited;
                }
                else
                {
                    target = model;
                    target.EntrySource = EntrySourceManual;
                    target.CreatedAt = DateTime.UtcNow;
                    target.CreatedBy = userName;
                    target.UpdatedAt = DateTime.UtcNow;
                    target.UpdatedBy = userName;
                    _db.BudgetLines.Add(target);
                }

                await _db.SaveChangesAsync();

                if (currentCategory == "CAPEX" && capexAttachment != null && capexAttachment.Length > 0)
                {
                    var safeFileName = Path.GetFileName(capexAttachment.FileName);
                    var contentType = string.IsNullOrWhiteSpace(capexAttachment.ContentType)
                        ? "application/octet-stream"
                        : capexAttachment.ContentType;

                    byte[] bytes;
                    using (var ms = new MemoryStream())
                    {
                        await capexAttachment.CopyToAsync(ms);
                        bytes = ms.ToArray();
                    }

                    var doc = await _db.BudgetLineDocuments.FirstOrDefaultAsync(d => d.BudgetLineId == target.BudgetLineId);
                    if (doc == null)
                    {
                        doc = new BudgetLineDocuments
                        {
                            BudgetLineId = target.BudgetLineId,
                            FileName = safeFileName,
                            ContentType = contentType,
                            SizeBytes = (int)capexAttachment.Length,
                            Content = bytes,
                            UploadedAt = DateTime.UtcNow,
                            UploadedBy = userName
                        };
                        _db.BudgetLineDocuments.Add(doc);
                    }
                    else
                    {
                        doc.FileName = safeFileName;
                        doc.ContentType = contentType;
                        doc.SizeBytes = (int)capexAttachment.Length;
                        doc.Content = bytes;
                        doc.UploadedAt = DateTime.UtcNow;
                        doc.UploadedBy = userName;
                    }

                    await _db.SaveChangesAsync();
                }

                // Audit Log
                _db.AuditLogs.Add(new AuditLogs
                {
                    UserName = userName,
                    Action = isUpdate ? "UPDATE" : "INSERT",
                    EntityName = "BudgetLines",
                    RecordId = target.BudgetLineId.ToString(),
                    Timestamp = DateTime.UtcNow,
                    Details = $"{(isUpdate ? "Updated" : "Added")} line for {target.BudgetYear} / {currentCategory}. Amount: {target.Amount}"
                });
                await _db.SaveChangesAsync();

                TempData["Success"] = isUpdate ? "Budget line updated." : "Budget line saved.";
                TempData["LastSavedId"] = target.BudgetLineId.ToString();
                return RedirectToAction(nameof(Entry), new { category = currentCategory });
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", "Unable to save changes. Please review the required fields and try again.");
                await ReloadViewData(model, currentCategory);
                return View(model);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(string category)
        {
            category = (category ?? "OPEX").ToUpperInvariant();

            var year = HttpContext.Session.GetInt("ctxYear");
            var entityId = HttpContext.Session.GetInt("ctxEntityId");
            var deptId = HttpContext.Session.GetInt("ctxDeptId");
            if (!(year.HasValue && entityId.HasValue && deptId.HasValue))
                return RedirectToAction("Select", "Context");

            var cat = await _db.Categories.FirstAsync(c => c.CategoryCode == category);

            var hasLines = await _db.BudgetLines.AsNoTracking()
                .AnyAsync(b => b.BudgetYear == year.Value
                               && b.EntityId == entityId.Value
                               && b.DepartmentId == deptId.Value
                               && b.CategoryId == cat.CategoryId);
            if (!hasLines)
            {
                TempData["Error"] = "No budget lines found to submit for the selected context.";
                return RedirectToAction(nameof(Entry), new { category });
            }

            var latest = await _db.BudgetSubmissions
                .Where(s => s.BudgetYear == year.Value
                            && s.EntityId == entityId.Value
                            && s.DepartmentId == deptId.Value
                            && s.CategoryId == cat.CategoryId)
                .OrderByDescending(s => s.VersionNo)
                .FirstOrDefaultAsync();

            if (latest != null && IsLockedStatus(latest.Status))
            {
                TempData["Error"] = "Budget is already submitted and locked.";
                return RedirectToAction(nameof(Entry), new { category });
            }

            var userName = User.Identity?.Name ?? "Unknown";
            if (latest == null)
            {
                latest = new BudgetSubmissions
                {
                    BudgetYear = year.Value,
                    EntityId = entityId.Value,
                    DepartmentId = deptId.Value,
                    CategoryId = cat.CategoryId,
                    VersionNo = 1,
                    Status = "Submitted",
                    SubmittedAt = DateTime.UtcNow,
                    SubmittedBy = userName
                };
                _db.BudgetSubmissions.Add(latest);
            }
            else
            {
                latest.Status = "Submitted";
                latest.SubmittedAt = DateTime.UtcNow;
                latest.SubmittedBy = userName;
            }

            await _db.SaveChangesAsync();

            await _db.Database.ExecuteSqlInterpolatedAsync($@"
DELETE FROM core.BudgetSubmissionLines
WHERE SubmissionId = {latest.SubmissionId};");

            await _db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.BudgetSubmissionLines
(
    SubmissionId,
    SourceBudgetLineId,
    BudgetYear,
    EntityId,
    DepartmentId,
    CategoryId,
    ItemId,
    ProgramId,
    ActivityId,
    ProjectId,
    Quantity,
    UnitPrice,
    Amount,
    DistributionMode,
    M01, M02, M03, M04, M05, M06, M07, M08, M09, M10, M11, M12,
    F1_Percent,
    F1_Amount,
    F2_Percent,
    F2_Amount,
    Dep_Method,
    Dep_LifeMonths,
    Dep_StartMonth,
    CapexAssetType,
    Notes,
    Description,
    CreatedAt,
    CreatedBy,
    UpdatedAt,
    UpdatedBy,
    DocFileName,
    DocContentType,
    DocSizeBytes,
    DocContent,
    DocUploadedAt,
    DocUploadedBy,
    SnapshottedAt,
    SnapshottedBy
)
SELECT
    {latest.SubmissionId} AS SubmissionId,
    b.BudgetLineId AS SourceBudgetLineId,
    b.BudgetYear,
    b.EntityId,
    b.DepartmentId,
    b.CategoryId,
    b.ItemId,
    b.ProgramId,
    b.ActivityId,
    b.ProjectId,
    b.Quantity,
    b.UnitPrice,
    b.Amount,
    b.DistributionMode,
    b.M01, b.M02, b.M03, b.M04, b.M05, b.M06, b.M07, b.M08, b.M09, b.M10, b.M11, b.M12,
    b.F1_Percent,
    b.F1_Amount,
    b.F2_Percent,
    b.F2_Amount,
    b.Dep_Method,
    b.Dep_LifeMonths,
    b.Dep_StartMonth,
    b.CapexAssetType,
    b.Notes,
    b.Description,
    b.CreatedAt,
    b.CreatedBy,
    b.UpdatedAt,
    b.UpdatedBy,
    d.FileName,
    d.ContentType,
    d.SizeBytes,
    d.Content,
    d.UploadedAt,
    d.UploadedBy,
    SYSUTCDATETIME(),
    {userName}
FROM core.BudgetLines b
LEFT JOIN core.BudgetLineDocuments d ON d.BudgetLineId = b.BudgetLineId
WHERE b.BudgetYear = {year.Value}
  AND b.EntityId = {entityId.Value}
  AND b.DepartmentId = {deptId.Value}
  AND b.CategoryId = {cat.CategoryId};");

            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = userName,
                Action = "UPDATE",
                EntityName = "BudgetSubmissions",
                RecordId = latest.SubmissionId.ToString(),
                Timestamp = DateTime.UtcNow,
                Details = $"Submitted budget for approval: {year.Value} / {category}."
            });

            await _db.SaveChangesAsync();

            TempData["Success"] = "Budget submitted for approval. Editing is now locked.";
            return RedirectToAction(nameof(Entry), new { category });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StartRevision(string category)
        {
            category = (category ?? "OPEX").ToUpperInvariant();

            var year = HttpContext.Session.GetInt("ctxYear");
            var entityId = HttpContext.Session.GetInt("ctxEntityId");
            var deptId = HttpContext.Session.GetInt("ctxDeptId");
            if (!(year.HasValue && entityId.HasValue && deptId.HasValue))
                return RedirectToAction("Select", "Context");

            var cat = await _db.Categories.FirstAsync(c => c.CategoryCode == category);

            var latest = await _db.BudgetSubmissions
                .Where(s => s.BudgetYear == year.Value
                            && s.EntityId == entityId.Value
                            && s.DepartmentId == deptId.Value
                            && s.CategoryId == cat.CategoryId)
                .OrderByDescending(s => s.VersionNo)
                .FirstOrDefaultAsync();

            if (latest == null)
            {
                _db.BudgetSubmissions.Add(new BudgetSubmissions
                {
                    BudgetYear = year.Value,
                    EntityId = entityId.Value,
                    DepartmentId = deptId.Value,
                    CategoryId = cat.CategoryId,
                    VersionNo = 1,
                    Status = "Draft"
                });
                await _db.SaveChangesAsync();

                TempData["Success"] = "Draft created.";
                return RedirectToAction(nameof(Entry), new { category });
            }

            if (!string.Equals(latest.Status, "Returned", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Revision can only be started after the submission is returned.";
                return RedirectToAction(nameof(Entry), new { category });
            }

            var userName = User.Identity?.Name ?? "Unknown";
            var next = new BudgetSubmissions
            {
                BudgetYear = latest.BudgetYear,
                EntityId = latest.EntityId,
                DepartmentId = latest.DepartmentId,
                CategoryId = latest.CategoryId,
                VersionNo = latest.VersionNo + 1,
                ParentSubmissionId = latest.SubmissionId,
                Status = "Draft"
            };

            _db.BudgetSubmissions.Add(next);
            _db.BudgetRevisionRequests.Add(new BudgetRevisionRequests
            {
                SubmissionId = latest.SubmissionId,
                ActionType = "StartRevision",
                RequestedAt = DateTime.UtcNow,
                RequestedBy = userName
            });
            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = userName,
                Action = "INSERT",
                EntityName = "BudgetSubmissions",
                RecordId = "",
                Timestamp = DateTime.UtcNow,
                Details = $"Started revision v{next.VersionNo} for {latest.BudgetYear} / {category}."
            });

            await _db.SaveChangesAsync();

            TempData["Success"] = "Revision started. Editing is unlocked.";
            return RedirectToAction(nameof(Entry), new { category });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(long id, string category)
        {
            category = (category ?? "OPEX").ToUpperInvariant();

            var year = HttpContext.Session.GetInt("ctxYear");
            var entityId = HttpContext.Session.GetInt("ctxEntityId");
            var deptId = HttpContext.Session.GetInt("ctxDeptId");
            if (!(year.HasValue && entityId.HasValue && deptId.HasValue))
                return RedirectToAction("Select", "Context");

            var cat = await _db.Categories.FirstAsync(c => c.CategoryCode == category);

            if (await IsBudgetLocked(year.Value, entityId.Value, deptId.Value, cat.CategoryId))
            {
                TempData["Error"] = "Budget has been submitted and is locked. Delete is disabled.";
                return RedirectToAction(nameof(Entry), new { category });
            }

            var line = await _db.BudgetLines.FirstOrDefaultAsync(b => b.BudgetLineId == id
                                                                  && b.BudgetYear == year.Value
                                                                  && b.EntityId == entityId.Value
                                                                  && b.DepartmentId == deptId.Value
                                                                  && b.CategoryId == cat.CategoryId);
            if (line == null)
            {
                TempData["Error"] = "Budget line not found for the selected context.";
                return RedirectToAction(nameof(Entry), new { category });
            }

            _db.BudgetLines.Remove(line);
            await _db.SaveChangesAsync();

            var userName = User.Identity?.Name ?? "Unknown";
            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = userName,
                Action = "DELETE",
                EntityName = "BudgetLines",
                RecordId = id.ToString(),
                Timestamp = DateTime.UtcNow,
                Details = $"Deleted line for {year.Value} / {category}."
            });
            await _db.SaveChangesAsync();

            TempData["Success"] = "Budget line deleted.";
            return RedirectToAction(nameof(Entry), new { category });
        }

        [HttpGet]
        public async Task<IActionResult> DownloadSupportDoc(long id, string category)
        {
            category = (category ?? "OPEX").ToUpperInvariant();

            var year = HttpContext.Session.GetInt("ctxYear");
            var entityId = HttpContext.Session.GetInt("ctxEntityId");
            var deptId = HttpContext.Session.GetInt("ctxDeptId");
            if (!(year.HasValue && entityId.HasValue && deptId.HasValue))
                return RedirectToAction("Select", "Context");

            var cat = await _db.Categories.FirstAsync(c => c.CategoryCode == category);

            var lineExists = await _db.BudgetLines.AsNoTracking().AnyAsync(b => b.BudgetLineId == id
                                                                            && b.BudgetYear == year.Value
                                                                            && b.EntityId == entityId.Value
                                                                            && b.DepartmentId == deptId.Value
                                                                            && b.CategoryId == cat.CategoryId);
            if (!lineExists) return NotFound();

            var doc = await _db.BudgetLineDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.BudgetLineId == id);
            if (doc == null) return NotFound();

            return File(doc.Content, doc.ContentType, doc.FileName);
        }

        // ---------- Excel bulk template & upload ----------

        private static readonly string[] BaseColumns =
        {
            "Year","EntityCode","DeptCode","ItemCode","Description","ActivityCode","ProjectCode",
            "Quantity","UnitPrice","DistributionMode",
            "M01","M02","M03","M04","M05","M06","M07","M08","M09","M10","M11","M12",
            "F1_Percent","F2_Percent","Notes"
        };
        private static readonly string[] CapexExtraColumns =
        {
            "CapexAssetType","Dep_Method","Dep_LifeMonths","Dep_StartMonth"
        };

        private static string[] ColumnsFor(string category) =>
            category == "CAPEX" ? BaseColumns.Concat(CapexExtraColumns).ToArray() : BaseColumns;

        private static bool IsBulkCategory(string category) =>
            category == "REVENUE" || category == "OPEX" || category == "CAPEX";

        // GET: Download a category-specific template (with reference sheets of valid codes)
        [HttpGet]
        public async Task<IActionResult> Template(string category = "OPEX")
        {
            category = (category ?? "OPEX").ToUpperInvariant();
            if (!IsBulkCategory(category)) category = "OPEX";

            var (_, entityScope, deptScope) = GetScope();

            var entitiesQ = _db.Entities.AsNoTracking().Where(e => e.IsActive);
            if (entityScope.HasValue) entitiesQ = entitiesQ.Where(e => e.EntityId == entityScope.Value);
            var entities = await entitiesQ.OrderBy(e => e.EntityCode).ToListAsync();

            var deptQ = _db.Departments.AsNoTracking().Include(d => d.Entity).Where(d => d.IsActive);
            if (entityScope.HasValue) deptQ = deptQ.Where(d => d.EntityId == entityScope.Value);
            if (deptScope.HasValue) deptQ = deptQ.Where(d => d.DepartmentId == deptScope.Value);
            var depts = await deptQ.OrderBy(d => d.Entity.EntityCode).ThenBy(d => d.DeptCode).ToListAsync();
            var deptIds = depts.Select(d => d.DepartmentId).ToList();

            var items = await _db.Items.AsNoTracking().Include(i => i.GLAccount)
                .Where(i => i.IsActive && i.GLAccount.GLType == category)
                .OrderBy(i => i.ItemCode).ToListAsync();

            var activities = await _db.Activities.AsNoTracking()
                .Include(a => a.Department).Include(a => a.Program)
                .Where(a => a.IsActive && deptIds.Contains(a.DepartmentId))
                .OrderBy(a => a.Department.DeptCode).ThenBy(a => a.ActivityCode).ToListAsync();

            var projects = await _db.Projects.AsNoTracking().Include(p => p.OwningDepartment)
                .Where(p => p.IsActive && (p.OwningDepartmentId == null || deptIds.Contains(p.OwningDepartmentId.Value)))
                .OrderBy(p => p.ProjectCode).ToListAsync();

            using var wb = new XLWorkbook();

            var cols = ColumnsFor(category);
            var ws = wb.Worksheets.Add(category);
            for (int c = 0; c < cols.Length; c++) ws.Cell(1, c + 1).Value = cols[c];
            ws.Range(1, 1, 1, cols.Length).Style.Font.Bold = true;
            ws.SheetView.FreezeRows(1);

            var sample = new Dictionary<string, object?>
            {
                ["Year"] = DateTime.Now.Year,
                ["EntityCode"] = entities.FirstOrDefault()?.EntityCode ?? "ENT01",
                ["DeptCode"] = depts.FirstOrDefault()?.DeptCode ?? "DEP01",
                ["ItemCode"] = items.FirstOrDefault()?.ItemCode ?? "ITEM001",
                ["Description"] = "Example line - replace or delete this row",
                ["ActivityCode"] = activities.FirstOrDefault()?.ActivityCode ?? "ACT01",
                ["ProjectCode"] = "",
                ["Quantity"] = 1,
                ["UnitPrice"] = 1000,
                ["DistributionMode"] = "EQUAL",
                ["F1_Percent"] = 0,
                ["F2_Percent"] = 0,
                ["Notes"] = ""
            };
            if (category == "CAPEX")
            {
                sample["CapexAssetType"] = "NEW";
                sample["Dep_Method"] = "STRAIGHT";
                sample["Dep_LifeMonths"] = 60;
                sample["Dep_StartMonth"] = 1;
            }
            for (int c = 0; c < cols.Length; c++)
                if (sample.TryGetValue(cols[c], out var v)) SetCell(ws, 2, c + 1, v);

            // Drop-down lists so the keyword columns cannot be mistyped or left blank.
            const int lastValidationRow = 2000;
            void AddList(string column, string csvOptions, string message)
            {
                var idx = Array.IndexOf(cols, column);
                if (idx < 0) return;
                var dv = ws.Range(2, idx + 1, lastValidationRow, idx + 1).CreateDataValidation();
                dv.List($"\"{csvOptions}\"", true);
                dv.IgnoreBlanks = false;
                dv.ErrorStyle = XLErrorStyle.Stop;
                dv.ErrorTitle = column;
                dv.ErrorMessage = message;
            }

            AddList("DistributionMode", "EQUAL,MANUAL", "Choose EQUAL (split evenly) or MANUAL (enter M01..M12).");
            if (category == "CAPEX")
            {
                AddList("CapexAssetType", "NEW,REPLACEMENT", "Required for CAPEX. Choose NEW or REPLACEMENT.");
                AddList("Dep_Method", "STRAIGHT", "Depreciation method. STRAIGHT is currently the only supported value.");

                // Make the mandatory CAPEX column stand out in the header row.
                var assetCol = Array.IndexOf(cols, "CapexAssetType");
                if (assetCol >= 0)
                    ws.Cell(1, assetCol + 1).Style.Fill.BackgroundColor =
                        XLColor.FromHtml(GovBudget.Utils.BrandColors.AccentHex);
            }

            ws.Columns(1, cols.Length).AdjustToContents();

            void AddRef(string name, string[] headers, IEnumerable<string[]> rows)
            {
                var rs = wb.Worksheets.Add(name);
                for (int c = 0; c < headers.Length; c++) rs.Cell(1, c + 1).Value = headers[c];
                rs.Range(1, 1, 1, headers.Length).Style.Font.Bold = true;
                var r = 2;
                foreach (var row in rows)
                {
                    for (int c = 0; c < row.Length; c++) rs.Cell(r, c + 1).Value = row[c] ?? "";
                    r++;
                }
                rs.Columns(1, headers.Length).AdjustToContents();
            }

            var help = wb.Worksheets.Add("Instructions");
            help.Cell(1, 1).Value = $"{category} bulk upload template";
            help.Cell(1, 1).Style.Font.Bold = true;
            help.Cell(3, 1).Value = "1) Enter one budget line per row on the '" + category + "' sheet. Delete the example row.";
            help.Cell(4, 1).Value = "2) Use CODES (not IDs). Valid codes are on the Entities/Departments/Items/Activities/Projects sheets.";
            help.Cell(5, 1).Value = "3) ProjectCode is optional. ActivityCode is required and must belong to the same cost center (DeptCode).";
            help.Cell(6, 1).Value = "4) Amount is Quantity x UnitPrice (computed on upload). DistributionMode EQUAL splits evenly; MANUAL requires M01..M12 to sum to Amount.";
            help.Cell(7, 1).Value = "5) Items shown are only those whose GL type matches this category (" + category + ").";
            help.Cell(8, 1).Value = "6) REPLACEMENT: if data already exists for the same Year + Entity in this category, you will see a warning page first. Confirming DELETES all existing lines for that Year + Entity (every cost center you are allowed to edit, manual lines included) and then stores this file. You can tick an option to keep manually-entered lines. Locked/submitted budgets are rejected and nothing is deleted.";
            if (category == "CAPEX")
            {
                help.Cell(10, 1).Value = "CAPEX-only columns on the '" + category + "' sheet: CapexAssetType is REQUIRED and must be NEW or REPLACEMENT (pick from the drop-down). "
                                        + "Dep_Method (STRAIGHT), Dep_LifeMonths (e.g. 60) and Dep_StartMonth (1-12) are optional and default to STRAIGHT / 0 / 1.";
                help.Cell(11, 1).Value = "Do NOT build a CAPEX file from the OPEX template - it has no CapexAssetType column and the upload will be rejected.";
                help.Cell(11, 1).Style.Font.Bold = true;
            }
            help.Column(1).Width = 120;

            AddRef("Entities", new[] { "EntityCode", "EntityName" },
                entities.Select(e => new[] { e.EntityCode, e.EntityName }));
            AddRef("Departments", new[] { "EntityCode", "DeptCode", "DeptName" },
                depts.Select(d => new[] { d.Entity.EntityCode, d.DeptCode, d.DeptName }));
            AddRef("Items", new[] { "ItemCode", "ItemName", "GLCode", "GLType" },
                items.Select(i => new[] { i.ItemCode, i.ItemName, i.GLAccount?.GLCode ?? "", i.GLAccount?.GLType ?? "" }));
            AddRef("Activities", new[] { "DeptCode", "ActivityCode", "ActivityName", "ProgramCode" },
                activities.Select(a => new[] { a.Department.DeptCode, a.ActivityCode, a.ActivityName, a.Program?.ProgramCode ?? "" }));
            AddRef("Projects", new[] { "DeptCode", "ProjectCode", "ProjectName" },
                projects.Select(p => new[] { p.OwningDepartment?.DeptCode ?? "(any)", p.ProjectCode, p.ProjectName }));

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return File(stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"BudgetLines_{category}_Template.xlsx");
        }

        // POST: Bulk upload budget lines from the template
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Upload(string category, IFormFile? file)
        {
            category = (category ?? "OPEX").ToUpperInvariant();
            if (!IsBulkCategory(category))
            {
                TempData["Error"] = "Unknown category for upload.";
                return RedirectToAction(nameof(Entry), new { category = "OPEX" });
            }

            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Please choose an Excel file to upload.";
                return RedirectToAction(nameof(Entry), new { category });
            }
            if (!Path.GetExtension(file.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Only .xlsx files are supported.";
                return RedirectToAction(nameof(Entry), new { category });
            }

            var userName = User.Identity?.Name ?? "Unknown";
            var (isAdminLike, entityScope, deptScope) = GetScope();

            var cat = await _db.Categories.FirstOrDefaultAsync(c => c.CategoryCode == category);
            if (cat == null)
            {
                TempData["Error"] = $"Category '{category}' is not configured.";
                return RedirectToAction(nameof(Entry), new { category });
            }

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            ms.Position = 0;
            using var wb = new XLWorkbook(ms);

            var ws = wb.Worksheets.FirstOrDefault(s => string.Equals(s.Name, category, StringComparison.OrdinalIgnoreCase))
                     ?? wb.Worksheets.FirstOrDefault();
            var headerRow = ws?.FirstRowUsed();
            if (ws == null || headerRow == null)
            {
                TempData["Error"] = "The uploaded file has no data sheet. Please use the downloaded template.";
                return RedirectToAction(nameof(Entry), new { category });
            }

            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in headerRow.CellsUsed())
            {
                var name = (cell.GetString() ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(name)) map[name] = cell.Address.ColumnNumber;
            }

            var required = new List<string> { "Year", "EntityCode", "DeptCode", "ItemCode", "Description", "ActivityCode", "Quantity", "UnitPrice" };
            // CapexAssetType only exists on the CAPEX template and every CAPEX row needs it, so
            // catch a file built from another category's template before validating 100s of rows.
            if (category == "CAPEX") required.Add("CapexAssetType");

            var missing = required.Where(h => !map.ContainsKey(h)).ToList();
            if (missing.Any())
            {
                TempData["Error"] = "Missing required columns: " + string.Join(", ", missing)
                                    + $". Please download the {category} template and re-enter the rows there "
                                    + "(each category has its own column set).";
                return RedirectToAction(nameof(Entry), new { category });
            }

            // Lookup dictionaries. Codes are compared through NormCode so that stray spaces or
            // invisible Unicode marks (common when cells are copied out of Arabic sheets) do not
            // make an otherwise valid code look missing.
            var entities = await _db.Entities.AsNoTracking().ToListAsync();
            var entityByCode = entities.GroupBy(e => NormCode(e.EntityCode))
                                       .ToDictionary(g => g.Key, g => g.First());

            var departments = await _db.Departments.AsNoTracking().ToListAsync();
            var deptByKey = new Dictionary<(int, string), Departments>();
            foreach (var d in departments)
            {
                var k = (d.EntityId, NormCode(d.DeptCode));
                if (!deptByKey.ContainsKey(k)) deptByKey[k] = d;
            }

            var allItems = await _db.Items.AsNoTracking().Include(i => i.GLAccount).ToListAsync();
            var itemByCode = allItems.GroupBy(i => NormCode(i.ItemCode))
                                     .ToDictionary(g => g.Key, g => g.First());

            var allActivities = await _db.Activities.AsNoTracking().ToListAsync();
            var actByKey = new Dictionary<(int, string), Activities>();
            foreach (var a in allActivities)
            {
                var k = (a.DepartmentId, NormCode(a.ActivityCode));
                if (!actByKey.ContainsKey(k)) actByKey[k] = a;
            }

            // Which cost center(s) each activity code actually belongs to, so a mismatch can be
            // reported with the fix instead of a bare "not found".
            var deptById = departments.ToDictionary(d => d.DepartmentId);
            var actOwners = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in allActivities)
            {
                if (!deptById.TryGetValue(a.DepartmentId, out var owner)) continue;
                var code = NormCode(a.ActivityCode);
                if (!actOwners.TryGetValue(code, out var owners))
                {
                    owners = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    actOwners[code] = owners;
                }
                owners.Add(owner.DeptCode);
            }

            var allProjects = await _db.Projects.AsNoTracking().ToListAsync();
            var projByCode = allProjects.GroupBy(p => NormCode(p.ProjectCode))
                                        .ToDictionary(g => g.Key, g => g.First());

            var lastRow = ws.LastRowUsed()?.RowNumber() ?? headerRow.RowNumber();
            var firstDataRow = headerRow.RowNumber() + 1;

            var toInsert = new List<BudgetLines>();
            var errors = new List<string>();
            var lockedCache = new Dictionary<(int, int, int), bool>();

            for (int r = firstDataRow; r <= lastRow; r++)
            {
                var row = ws.Row(r);

                var yearStr = CellStr(row, map, "Year");
                var entityCode = CellStr(row, map, "EntityCode");
                var deptCode = CellStr(row, map, "DeptCode");
                var itemCode = CellStr(row, map, "ItemCode");
                var desc = CellStr(row, map, "Description");
                var activityCode = CellStr(row, map, "ActivityCode");

                if (string.IsNullOrWhiteSpace(entityCode) && string.IsNullOrWhiteSpace(deptCode) &&
                    string.IsNullOrWhiteSpace(itemCode) && string.IsNullOrWhiteSpace(desc) &&
                    string.IsNullOrWhiteSpace(activityCode))
                    continue; // blank row

                if (!int.TryParse(yearStr, out var year) || year < 2000 || year > 2100)
                { errors.Add($"Row {r}: invalid Year '{yearStr}'."); goto Cap; }
                if (!entityByCode.TryGetValue(NormCode(entityCode), out var ent))
                { errors.Add($"Row {r}: EntityCode '{entityCode}' not found."); goto Cap; }
                if (!deptByKey.TryGetValue((ent.EntityId, NormCode(deptCode)), out var dept))
                { errors.Add($"Row {r}: DeptCode '{deptCode}' not found under entity '{entityCode}'."); goto Cap; }
                if (!CanUse(isAdminLike, entityScope, deptScope, ent.EntityId, dept.DepartmentId))
                { errors.Add($"Row {r}: you are not permitted to load into {entityCode}/{deptCode}."); goto Cap; }
                if (string.IsNullOrWhiteSpace(desc))
                { errors.Add($"Row {r}: Description is required."); goto Cap; }
                if (!itemByCode.TryGetValue(NormCode(itemCode), out var item))
                { errors.Add($"Row {r}: ItemCode '{itemCode}' not found."); goto Cap; }
                if (!item.IsActive)
                { errors.Add($"Row {r}: item '{itemCode}' is inactive."); goto Cap; }
                if (item.GLAccount == null || !string.Equals(item.GLAccount.GLType, category, StringComparison.OrdinalIgnoreCase))
                { errors.Add($"Row {r}: item '{itemCode}' is not a {category} item (GL type '{item.GLAccount?.GLType}')."); goto Cap; }
                if (string.IsNullOrWhiteSpace(activityCode))
                { errors.Add($"Row {r}: ActivityCode is required."); goto Cap; }
                if (!actByKey.TryGetValue((dept.DepartmentId, NormCode(activityCode)), out var activity))
                {
                    var hint = actOwners.TryGetValue(NormCode(activityCode), out var owners) && owners.Count > 0
                        ? $" It is defined under cost center(s): {string.Join(", ", owners)} — either set DeptCode to one of those, or add the activity under '{deptCode}'."
                        : $" No activity with this code exists in any cost center — create it under '{deptCode}' first (Admin Room > Activities).";
                    errors.Add($"Row {r}: ActivityCode '{activityCode}' does not belong to cost center '{deptCode}'.{hint}");
                    goto Cap;
                }

                int? projectId = null;
                var projectCode = CellStr(row, map, "ProjectCode");
                if (!string.IsNullOrWhiteSpace(projectCode))
                {
                    if (!projByCode.TryGetValue(NormCode(projectCode), out var proj))
                    { errors.Add($"Row {r}: ProjectCode '{projectCode}' not found."); goto Cap; }
                    if (proj.OwningDepartmentId.HasValue && proj.OwningDepartmentId.Value != dept.DepartmentId)
                    { errors.Add($"Row {r}: project '{projectCode}' does not belong to cost center '{deptCode}'."); goto Cap; }
                    projectId = proj.ProjectId;
                }

                var lockKey = (year, ent.EntityId, dept.DepartmentId);
                if (!lockedCache.TryGetValue(lockKey, out var locked))
                {
                    locked = await IsBudgetLocked(year, ent.EntityId, dept.DepartmentId, cat.CategoryId);
                    lockedCache[lockKey] = locked;
                }
                if (locked)
                { errors.Add($"Row {r}: {category} budget for {entityCode}/{deptCode} {year} is submitted/locked."); goto Cap; }

                var qty = CellDec(row, map, "Quantity");
                var price = CellDec(row, map, "UnitPrice");
                if (qty < 0 || price < 0)
                { errors.Add($"Row {r}: Quantity/UnitPrice cannot be negative."); goto Cap; }
                var amount = Math.Round(qty * price, 2, MidpointRounding.AwayFromZero);

                var mode = CellStr(row, map, "DistributionMode").ToUpperInvariant();
                if (mode != "MANUAL") mode = "EQUAL";

                var line = new BudgetLines
                {
                    BudgetYear = year,
                    EntityId = ent.EntityId,
                    DepartmentId = dept.DepartmentId,
                    CategoryId = cat.CategoryId,
                    ItemId = item.ItemId,
                    ActivityId = activity.ActivityId,
                    ProgramId = activity.ProgramId,
                    ProjectId = projectId,
                    Description = desc,
                    Quantity = qty,
                    UnitPrice = price,
                    Amount = amount,
                    DistributionMode = mode,
                    F1_Percent = CellDec(row, map, "F1_Percent"),
                    F2_Percent = CellDec(row, map, "F2_Percent"),
                    Notes = CellStr(row, map, "Notes"),
                    Dep_Method = "STRAIGHT",
                    Dep_LifeMonths = 0,
                    Dep_StartMonth = 1,
                    EntrySource = EntrySourceUpload,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = userName,
                    UpdatedAt = DateTime.UtcNow,
                    UpdatedBy = userName
                };

                if (mode == "EQUAL")
                {
                    var (m1, m2, m3, m4, m5, m6, m7, m8, m9, m10, m11, m12) = BudgetCalcService.Equal12(amount);
                    line.M01 = m1; line.M02 = m2; line.M03 = m3; line.M04 = m4; line.M05 = m5; line.M06 = m6;
                    line.M07 = m7; line.M08 = m8; line.M09 = m9; line.M10 = m10; line.M11 = m11; line.M12 = m12;
                }
                else
                {
                    line.M01 = CellDec(row, map, "M01"); line.M02 = CellDec(row, map, "M02"); line.M03 = CellDec(row, map, "M03");
                    line.M04 = CellDec(row, map, "M04"); line.M05 = CellDec(row, map, "M05"); line.M06 = CellDec(row, map, "M06");
                    line.M07 = CellDec(row, map, "M07"); line.M08 = CellDec(row, map, "M08"); line.M09 = CellDec(row, map, "M09");
                    line.M10 = CellDec(row, map, "M10"); line.M11 = CellDec(row, map, "M11"); line.M12 = CellDec(row, map, "M12");
                    if (Math.Round(BudgetCalcService.SumMonths(line), 2) != Math.Round(amount, 2))
                    { errors.Add($"Row {r}: MANUAL months must sum to Amount ({amount})."); goto Cap; }
                }

                line.F1_Amount = BudgetCalcService.ComputeForecast(amount, line.F1_Percent, 0);
                line.F2_Amount = BudgetCalcService.ComputeForecast(amount, line.F2_Percent, 0);

                if (category == "CAPEX")
                {
                    var assetTypeRaw = CellStr(row, map, "CapexAssetType");
                    var assetType = NormCode(assetTypeRaw);
                    if (assetType != "NEW" && assetType != "REPLACEMENT")
                    { errors.Add($"Row {r}: CapexAssetType must be NEW or REPLACEMENT (found '{assetTypeRaw}')."); goto Cap; }
                    line.CapexAssetType = assetType;
                    var depMethod = NormCode(CellStr(row, map, "Dep_Method"));
                    line.Dep_Method = string.IsNullOrWhiteSpace(depMethod) ? "STRAIGHT" : depMethod;
                    line.Dep_LifeMonths = CellInt(row, map, "Dep_LifeMonths");
                    var startM = CellInt(row, map, "Dep_StartMonth");
                    line.Dep_StartMonth = (byte)(startM >= 1 && startM <= 12 ? startM : 1);
                    BudgetCalcService.EnsureCapexDefaults(line);
                }

                toInsert.Add(line);

            Cap:
                if (errors.Count >= 50) { errors.Add("Too many errors; stopped reading further rows."); break; }
            }

            if (errors.Any())
            {
                TempData["Error"] = $"Upload cancelled — {errors.Count} problem(s) found; no rows were saved. "
                                    + string.Join(" | ", errors.Take(30));
                return RedirectToAction(nameof(Entry), new { category });
            }
            if (!toInsert.Any())
            {
                TempData["Error"] = "No data rows found to import.";
                return RedirectToAction(nameof(Entry), new { category });
            }

            // The (year, entity, cost center) scopes carried by the file itself.
            var fileScopes = toInsert
                .Select(l => (l.BudgetYear, l.EntityId, l.DepartmentId))
                .Distinct()
                .ToList();

            // A confirmed upload replaces the whole year + entity + category, so widen the
            // scope set to every cost center that already holds data for those year/entity
            // pairs (limited to what this user is allowed to write to).
            var scopes = await ResolveReplaceScopesAsync(cat.CategoryId, fileScopes, isAdminLike, entityScope, deptScope);

            // Nothing may be deleted from a submitted/locked budget, including the cost centers
            // that were pulled in by the entity-wide scope above.
            var lockedLabels = new List<string>();
            foreach (var s in scopes)
            {
                var lockKey = (s.BudgetYear, s.EntityId, s.DepartmentId);
                if (!lockedCache.TryGetValue(lockKey, out var isLocked))
                {
                    isLocked = await IsBudgetLocked(s.BudgetYear, s.EntityId, s.DepartmentId, cat.CategoryId);
                    lockedCache[lockKey] = isLocked;
                }
                if (isLocked) lockedLabels.Add(await ScopeLabelAsync(s.BudgetYear, s.DepartmentId));
            }
            if (lockedLabels.Count > 0)
            {
                TempData["Error"] = $"Upload cancelled — replacing the {category} budget for this entity/year would touch "
                                    + $"submitted/locked data: {string.Join(" | ", lockedLabels.Distinct())}. Nothing was changed.";
                return RedirectToAction(nameof(Entry), new { category });
            }

            var (uploadedCount, uploadedTotal, manualCount, manualTotal) =
                await CountExistingBulkAsync(cat.CategoryId, scopes);

            if (uploadedCount + manualCount > 0)
            {
                // Existing transactions are present. Stash the parsed rows plus the exact scopes
                // that will be cleared, and ask the user to confirm the replacement.
                HttpContext.Session.SetString(PendingBulkKey, JsonSerializer.Serialize(new PendingBulkUpload
                {
                    Category = category,
                    Lines = toInsert,
                    Scopes = scopes.Select(s => new BulkScope
                    {
                        BudgetYear = s.BudgetYear,
                        EntityId = s.EntityId,
                        DepartmentId = s.DepartmentId
                    }).ToList()
                }));

                var vm = await BuildBulkOverwriteVmAsync(category, cat.CategoryId, scopes, toInsert,
                    uploadedCount, uploadedTotal, manualCount, manualTotal);
                return View("ConfirmOverwrite", vm);
            }

            // No existing data for these scopes: straight insert (nothing to delete).
            await ApplyBulkAsync(toInsert, cat.CategoryId, scopes, deleteExisting: false, deleteManual: false, userName, category);
            TempData["Success"] = $"Imported {toInsert.Count} {category} budget line(s).";
            return RedirectToAction(nameof(Entry), new { category });
        }

        // A confirmed re-upload replaces the data for the same YEAR + ENTITY + category, not just
        // the cost centers listed in the file. This returns the file scopes plus every other
        // (year, entity, cost center) combination that already holds data for the same year/entity
        // pair. Scopes the user is not permitted to write to are left out, so a cost-center user
        // can never wipe another cost center's budget.
        private async Task<List<(int BudgetYear, int EntityId, int DepartmentId)>> ResolveReplaceScopesAsync(
            int categoryId,
            List<(int BudgetYear, int EntityId, int DepartmentId)> fileScopes,
            bool isAdminLike, int? entityScope, int? deptScope)
        {
            var years = fileScopes.Select(s => s.BudgetYear).Distinct().ToList();
            var entityIds = fileScopes.Select(s => s.EntityId).Distinct().ToList();

            var existing = await _db.BudgetLines.AsNoTracking()
                .Where(b => b.CategoryId == categoryId
                            && years.Contains(b.BudgetYear)
                            && entityIds.Contains(b.EntityId))
                .Select(b => new { b.BudgetYear, b.EntityId, b.DepartmentId })
                .Distinct()
                .ToListAsync();

            var set = new HashSet<(int, int, int)>(
                fileScopes.Select(s => (s.BudgetYear, s.EntityId, s.DepartmentId)));

            foreach (var e in existing)
            {
                // Only widen within year/entity pairs that the file actually carries.
                if (!fileScopes.Any(s => s.BudgetYear == e.BudgetYear && s.EntityId == e.EntityId)) continue;
                if (!CanUse(isAdminLike, entityScope, deptScope, e.EntityId, e.DepartmentId)) continue;
                set.Add((e.BudgetYear, e.EntityId, e.DepartmentId));
            }

            return set.Select(x => (BudgetYear: x.Item1, EntityId: x.Item2, DepartmentId: x.Item3)).ToList();
        }

        private async Task<string> ScopeLabelAsync(int year, int deptId)
        {
            var dep = await _db.Departments.AsNoTracking().Include(d => d.Entity)
                .FirstOrDefaultAsync(d => d.DepartmentId == deptId);
            return dep != null
                ? $"{year} — {dep.Entity?.EntityCode ?? "?"}/{dep.DeptCode} {dep.DeptName}"
                : $"{year}";
        }

        // Second step: the user confirmed replacing (delete-then-store) the existing transactions.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmUpload(bool keepManual = false)
        {
            // Replacement is now all-inclusive: every existing line in scope is deleted unless
            // the user explicitly ticked "keep manually-entered lines".
            var deleteManual = !keepManual;

            var json = HttpContext.Session.GetString(PendingBulkKey);
            HttpContext.Session.Remove(PendingBulkKey);
            if (string.IsNullOrEmpty(json))
            {
                TempData["Error"] = "Your upload session expired. Please upload the file again.";
                return RedirectToAction(nameof(Entry), new { category = "OPEX" });
            }

            PendingBulkUpload? pending;
            try { pending = JsonSerializer.Deserialize<PendingBulkUpload>(json); }
            catch { pending = null; }

            var category = (pending?.Category ?? "OPEX").ToUpperInvariant();
            if (!IsBulkCategory(category)) category = "OPEX";

            if (pending?.Lines == null || pending.Lines.Count == 0)
            {
                TempData["Error"] = "Could not restore the pending upload. Please upload the file again.";
                return RedirectToAction(nameof(Entry), new { category });
            }

            var cat = await _db.Categories.FirstOrDefaultAsync(c => c.CategoryCode == category);
            if (cat == null)
            {
                TempData["Error"] = $"Category '{category}' is not configured.";
                return RedirectToAction(nameof(Entry), new { category });
            }

            var userName = User.Identity?.Name ?? "Unknown";
            var toInsert = pending.Lines;

            // Re-stamp identity/audit fields for a clean insert.
            foreach (var l in toInsert)
            {
                l.BudgetLineId = 0;
                l.CreatedAt = DateTime.UtcNow;
                l.CreatedBy = userName;
                l.UpdatedAt = DateTime.UtcNow;
                l.UpdatedBy = userName;
            }

            // Use the scopes computed at upload time (year + entity wide), falling back to the
            // file's own scopes for a session stashed by an older build.
            var scopes = pending.Scopes != null && pending.Scopes.Count > 0
                ? pending.Scopes.Select(s => (s.BudgetYear, s.EntityId, s.DepartmentId)).Distinct().ToList()
                : toInsert.Select(l => (l.BudgetYear, l.EntityId, l.DepartmentId)).Distinct().ToList();

            int deleted;
            try
            {
                deleted = await ApplyBulkAsync(toInsert, cat.CategoryId, scopes, deleteExisting: true, deleteManual, userName, category);
            }
            catch (Exception)
            {
                TempData["Error"] = "Could not replace the data. Nothing was changed. Please try again.";
                return RedirectToAction(nameof(Entry), new { category });
            }

            var manualNote = deleteManual ? " (including manually-entered lines)" : " (manually-entered lines kept)";

            TempData["Success"] = $"Replaced existing {category} data: deleted {deleted} transaction(s){manualNote} and imported {toInsert.Count} new line(s).";
            return RedirectToAction(nameof(Entry), new { category });
        }

        // The user declined: discard the stashed upload and keep the existing data unchanged.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult CancelUpload(string category)
        {
            category = (category ?? "OPEX").ToUpperInvariant();
            if (!IsBulkCategory(category)) category = "OPEX";
            HttpContext.Session.Remove(PendingBulkKey);
            TempData["Success"] = "Upload cancelled — existing data was kept unchanged.";
            return RedirectToAction(nameof(Entry), new { category });
        }

        // Counts existing budget lines for the given scopes/category, split into uploaded
        // (EntrySource == "UPLOAD", replaced automatically) and manual/legacy (everything else,
        // protected unless the user confirms deletion).
        private async Task<(int uploadedCount, decimal uploadedTotal, int manualCount, decimal manualTotal)>
            CountExistingBulkAsync(int categoryId, List<(int BudgetYear, int EntityId, int DepartmentId)> scopes)
        {
            var uploadedCount = 0;
            var uploadedTotal = 0m;
            var manualCount = 0;
            var manualTotal = 0m;

            foreach (var s in scopes)
            {
                var scopeQ = _db.BudgetLines.AsNoTracking().Where(b => b.CategoryId == categoryId
                    && b.BudgetYear == s.BudgetYear && b.EntityId == s.EntityId && b.DepartmentId == s.DepartmentId);

                var uploadedQ = scopeQ.Where(b => b.EntrySource == EntrySourceUpload);
                var manualQ = scopeQ.Where(b => b.EntrySource != EntrySourceUpload);

                uploadedCount += await uploadedQ.CountAsync();
                uploadedTotal += await uploadedQ.SumAsync(b => (decimal?)b.Amount) ?? 0m;
                manualCount += await manualQ.CountAsync();
                manualTotal += await manualQ.SumAsync(b => (decimal?)b.Amount) ?? 0m;
            }

            return (uploadedCount, uploadedTotal, manualCount, manualTotal);
        }

        // Builds the confirmation view-model: overall + per-scope uploaded/manual/incoming figures.
        private async Task<BulkOverwriteConfirmVm> BuildBulkOverwriteVmAsync(
            string category, int categoryId,
            List<(int BudgetYear, int EntityId, int DepartmentId)> scopes,
            List<BudgetLines> toInsert,
            int uploadedCount, decimal uploadedTotal, int manualCount, decimal manualTotal)
        {
            var deptIds = scopes.Select(s => s.DepartmentId).Distinct().ToList();
            var depMap = await _db.Departments.AsNoTracking().Include(d => d.Entity)
                .Where(d => deptIds.Contains(d.DepartmentId))
                .ToDictionaryAsync(d => d.DepartmentId);

            var scopeVms = new List<BulkOverwriteScopeVm>();
            foreach (var s in scopes.OrderBy(x => x.BudgetYear).ThenBy(x => x.DepartmentId))
            {
                depMap.TryGetValue(s.DepartmentId, out var dep);
                var label = dep != null
                    ? $"{s.BudgetYear} — {dep.Entity?.EntityCode ?? "?"}/{dep.DeptCode} {dep.DeptName}"
                    : $"{s.BudgetYear}";

                var scopeQ = _db.BudgetLines.AsNoTracking().Where(b => b.CategoryId == categoryId
                    && b.BudgetYear == s.BudgetYear && b.EntityId == s.EntityId && b.DepartmentId == s.DepartmentId);
                var uploadedQ = scopeQ.Where(b => b.EntrySource == EntrySourceUpload);
                var manualQ = scopeQ.Where(b => b.EntrySource != EntrySourceUpload);

                var scopeNew = toInsert.Where(l => l.BudgetYear == s.BudgetYear
                    && l.EntityId == s.EntityId && l.DepartmentId == s.DepartmentId).ToList();

                scopeVms.Add(new BulkOverwriteScopeVm
                {
                    Label = label,
                    InFile = scopeNew.Count > 0,
                    UploadedCount = await uploadedQ.CountAsync(),
                    UploadedTotal = await uploadedQ.SumAsync(b => (decimal?)b.Amount) ?? 0m,
                    ManualCount = await manualQ.CountAsync(),
                    ManualTotal = await manualQ.SumAsync(b => (decimal?)b.Amount) ?? 0m,
                    NewCount = scopeNew.Count,
                    NewTotal = scopeNew.Sum(l => l.Amount)
                });
            }

            return new BulkOverwriteConfirmVm
            {
                Category = category,
                UploadedCount = uploadedCount,
                UploadedTotal = uploadedTotal,
                ManualCount = manualCount,
                ManualTotal = manualTotal,
                NewCount = toInsert.Count,
                NewTotal = toInsert.Sum(l => l.Amount),
                Scopes = scopeVms
            };
        }

        // Applies the parsed rows. When deleteExisting is true, the existing lines (and their
        // documents) for the same scopes are deleted first, then the new lines inserted — all in
        // a single transaction so it is atomic (delete runs first, store second). Manually-entered
        // lines are preserved unless deleteManual is true.
        private async Task<int> ApplyBulkAsync(
            List<BudgetLines> toInsert, int categoryId,
            List<(int BudgetYear, int EntityId, int DepartmentId)> scopes,
            bool deleteExisting, bool deleteManual, string userName, string category)
        {
            var deleted = 0;
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                deleted = 0;
                _db.ChangeTracker.Clear(); // keep idempotent across retry attempts

                await using var tx = await _db.Database.BeginTransactionAsync();

                if (deleteExisting)
                {
                    foreach (var s in scopes)
                    {
                        var scopeLines = _db.BudgetLines.Where(b => b.CategoryId == categoryId
                            && b.BudgetYear == s.BudgetYear && b.EntityId == s.EntityId && b.DepartmentId == s.DepartmentId);

                        // By default only replace previously-uploaded lines; keep manual/legacy
                        // lines unless the user explicitly asked to delete them too.
                        if (!deleteManual)
                            scopeLines = scopeLines.Where(b => b.EntrySource == EntrySourceUpload);

                        var scopeLineIds = scopeLines.Select(b => b.BudgetLineId);

                        // Remove dependent documents first to satisfy the FK.
                        await _db.BudgetLineDocuments
                            .Where(d => scopeLineIds.Contains(d.BudgetLineId))
                            .ExecuteDeleteAsync();

                        deleted += await scopeLines.ExecuteDeleteAsync();
                    }
                }

                _db.BudgetLines.AddRange(toInsert);
                await _db.SaveChangesAsync();

                _db.AuditLogs.Add(new AuditLogs
                {
                    UserName = userName,
                    Action = deleteExisting ? "BULK_REPLACE" : "BULK_INSERT",
                    EntityName = "BudgetLines",
                    RecordId = "",
                    Timestamp = DateTime.UtcNow,
                    Details = deleteExisting
                        ? $"Bulk replace: deleted {deleted} and inserted {toInsert.Count} {category} line(s) from Excel."
                        : $"Bulk uploaded {toInsert.Count} {category} line(s) from Excel."
                });
                await _db.SaveChangesAsync();

                await tx.CommitAsync();
            });
            return deleted;
        }

        private (bool isAdminLike, int? entityScope, int? deptScope) GetScope()
        {
            var isAdminLike = User.IsInRole("ADMIN") || User.IsInRole("SYSADMIN");
            int? entityScope = null, deptScope = null;
            var ec = User.Claims.FirstOrDefault(c => c.Type == "EntityId")?.Value;
            if (int.TryParse(ec, out var e) && e > 0) entityScope = e;
            var dc = User.Claims.FirstOrDefault(c => c.Type == "DepartmentId")?.Value;
            if (int.TryParse(dc, out var d) && d > 0) deptScope = d;
            return (isAdminLike, entityScope, deptScope);
        }

        private static bool CanUse(bool isAdminLike, int? entityScope, int? deptScope, int entityId, int deptId)
        {
            if (entityScope.HasValue && entityId != entityScope.Value) return false;
            if (deptScope.HasValue && deptId != deptScope.Value) return false;
            if (!isAdminLike && !entityScope.HasValue) return false;
            return true;
        }

        private static void SetCell(IXLWorksheet ws, int r, int c, object? v)
        {
            if (v == null) return;
            var cell = ws.Cell(r, c);
            switch (v)
            {
                case int i: cell.Value = i; break;
                case decimal d: cell.Value = d; break;
                case double db: cell.Value = db; break;
                case bool b: cell.Value = b; break;
                default: cell.Value = v.ToString(); break;
            }
        }

        private static string CellStr(IXLRow row, Dictionary<string, int> map, string col)
            => map.TryGetValue(col, out var ci) ? (row.Cell(ci).GetString() ?? "").Trim() : "";

        // Canonical form used to match codes between an uploaded sheet and the master data.
        // Drops all whitespace plus the invisible characters that Excel keeps when cells are
        // copied from mixed Arabic/English sources (zero-width joiners, bidi marks, BOM).
        private static string NormCode(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (var ch in raw)
            {
                if (char.IsWhiteSpace(ch)) continue;
                switch (ch)
                {
                    case '\u200B': // zero-width space
                    case '\u200C': // zero-width non-joiner
                    case '\u200D': // zero-width joiner
                    case '\u200E': // left-to-right mark
                    case '\u200F': // right-to-left mark
                    case '\u202A': // LTR embedding
                    case '\u202B': // RTL embedding
                    case '\u202C': // pop directional formatting
                    case '\u202D': // LTR override
                    case '\u202E': // RTL override
                    case '\u2066': // LTR isolate
                    case '\u2067': // RTL isolate
                    case '\u2068': // first strong isolate
                    case '\u2069': // pop directional isolate
                    case '\uFEFF': // BOM / zero-width no-break space
                    case '\u00AD': // soft hyphen
                        continue;
                }
                sb.Append(ch);
            }
            return sb.ToString().ToUpperInvariant();
        }

        private static decimal CellDec(IXLRow row, Dictionary<string, int> map, string col)
        {
            if (!map.TryGetValue(col, out var ci)) return 0m;
            var cell = row.Cell(ci);
            if (cell.TryGetValue<double>(out var dv)) return (decimal)dv;
            var s = (cell.GetString() ?? "").Trim();
            return decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;
        }

        private static int CellInt(IXLRow row, Dictionary<string, int> map, string col)
        {
            if (!map.TryGetValue(col, out var ci)) return 0;
            var cell = row.Cell(ci);
            if (cell.TryGetValue<double>(out var dv)) return (int)Math.Round(dv);
            var s = (cell.GetString() ?? "").Trim();
            return int.TryParse(s, out var i) ? i : 0;
        }

        // ---------- helpers ----------
        private static bool IsLockedStatus(string? status)
        {
            return !string.IsNullOrWhiteSpace(status)
                   && !string.Equals(status, "Draft", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<bool> IsBudgetLocked(int year, int entityId, int deptId, int categoryId)
        {
            var latestStatus = await _db.BudgetSubmissions.AsNoTracking()
                .Where(s => s.BudgetYear == year
                            && s.EntityId == entityId
                            && s.DepartmentId == deptId
                            && s.CategoryId == categoryId)
                .OrderByDescending(s => s.VersionNo)
                .Select(s => s.Status)
                .FirstOrDefaultAsync();

            return IsLockedStatus(latestStatus);
        }

        private async Task PopulatePrograms(int entityId)
        {
            ViewBag.ProgramId = new SelectList(await _db.Programs
                .Where(p => p.EntityId == entityId && p.IsActive)
                .OrderBy(p => p.ProgramCode)
                .Select(p => new { p.ProgramId, Display = p.ProgramCode + " - " + p.ProgramName })
                .ToListAsync(), "ProgramId", "Display");
        }

        private async Task PopulateActivities(int deptId, int? programId)
        {
            var q = _db.Activities.Where(a => a.DepartmentId == deptId && a.IsActive);
            if (programId.HasValue) q = q.Where(a => a.ProgramId == programId.Value);

            ViewBag.ActivityId = new SelectList(await q
                .OrderBy(a => a.ActivityCode)
                .Select(a => new { a.ActivityId, Display = a.ActivityCode + " - " + a.ActivityName })
                .ToListAsync(), "ActivityId", "Display");
        }

        private async Task PopulateItemsByCategory(string categoryCode)
        {
            ViewBag.ItemId = new SelectList(await _db.Items
                .Include(i => i.GLAccount)
                .Where(i => i.IsActive && i.GLAccount.GLType == categoryCode)
                .OrderBy(i => i.ItemCode)
                .Select(i => new { i.ItemId, Display = i.ItemCode + " - " + i.ItemName })
                .ToListAsync(), "ItemId", "Display");
        }

        private async Task PopulateProjects(int deptId)
        {
            ViewBag.ProjectId = new SelectList(await _db.Projects
                .Where(p => p.IsActive && (p.OwningDepartmentId == null || p.OwningDepartmentId == deptId))
                .OrderBy(p => p.ProjectCode)
                .Select(p => new { p.ProjectId, Display = p.ProjectCode + " - " + p.ProjectName })
                .ToListAsync(), "ProjectId", "Display");
        }

        private async Task ReloadViewData(BudgetLines model, string categoryCode)
        {
            await PopulateItemsByCategory(categoryCode);
            await PopulatePrograms(model.EntityId);
            await PopulateActivities(model.DepartmentId, model.ProgramId);
            await PopulateProjects(model.DepartmentId);

            ViewBag.Recent = await (
                from b in _db.BudgetLines.AsNoTracking()
                join item in _db.Items.AsNoTracking() on b.ItemId equals item.ItemId
                join act in _db.Activities.AsNoTracking() on b.ActivityId equals act.ActivityId into actJoin
                from act in actJoin.DefaultIfEmpty()
                join proj in _db.Projects.AsNoTracking() on b.ProjectId equals proj.ProjectId into projJoin
                from proj in projJoin.DefaultIfEmpty()
                join doc in _db.BudgetLineDocuments.AsNoTracking() on b.BudgetLineId equals doc.BudgetLineId into docJoin
                from doc in docJoin.DefaultIfEmpty()
                where b.CategoryId == model.CategoryId
                   && b.BudgetYear == model.BudgetYear
                   && b.EntityId == model.EntityId
                   && b.DepartmentId == model.DepartmentId
                orderby b.BudgetLineId descending
                select new
                {
                    b.BudgetLineId,
                    ItemCode = item.ItemCode,
                    b.Description,
                    b.EntrySource,
                    ActivityCode = act != null ? act.ActivityCode : "",
                    ProjectCode = proj != null ? proj.ProjectCode : "",
                    b.Quantity,
                    b.UnitPrice,
                    b.Amount,
                    b.F1_Percent,
                    b.F1_Amount,
                    b.F2_Percent,
                    b.F2_Amount,
                    DocFileName = doc != null ? doc.FileName : null
                }
            ).Take(100).ToListAsync();

            var dep = await _db.Departments.Include(d => d.Entity)
                            .FirstOrDefaultAsync(d => d.DepartmentId == model.DepartmentId);
            ViewBag.ContextLabel = dep != null
                ? $"{model.BudgetYear} — {dep.Entity?.EntityCode ?? "?"}/{dep.DeptCode} {dep.DeptName}"
                : $"{model.BudgetYear}";

            if (categoryCode == "CAPEX" && model.BudgetLineId > 0)
            {
                ViewBag.ExistingDocFileName = await _db.BudgetLineDocuments.AsNoTracking()
                    .Where(d => d.BudgetLineId == model.BudgetLineId)
                    .Select(d => d.FileName)
                    .FirstOrDefaultAsync();
            }
        }

        private static bool IsAllowedCapexAttachmentExtension(string? ext)
        {
            if (string.IsNullOrWhiteSpace(ext)) return false;
            return ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".png", StringComparison.OrdinalIgnoreCase);
        }
    }

    // Parsed rows held in session between the bulk upload and the overwrite confirmation.
    public class PendingBulkUpload
    {
        public string Category { get; set; } = "";
        public List<BudgetLines> Lines { get; set; } = new();
        // The (year, entity, cost center) combinations that will be cleared on confirmation.
        public List<BulkScope> Scopes { get; set; } = new();
    }

    public class BulkScope
    {
        public int BudgetYear { get; set; }
        public int EntityId { get; set; }
        public int DepartmentId { get; set; }
    }

    // Per-scope (year / entity / department) comparison shown on the confirmation page.
    public class BulkOverwriteScopeVm
    {
        public string Label { get; set; } = "";
        // False when the cost center holds existing data but is absent from the uploaded file;
        // it is still cleared because the replacement covers the whole entity/year.
        public bool InFile { get; set; }
        public int UploadedCount { get; set; }      // previously uploaded
        public decimal UploadedTotal { get; set; }
        public int ManualCount { get; set; }        // manual/edited — deleted unless the user opts to keep them
        public decimal ManualTotal { get; set; }
        public int NewCount { get; set; }
        public decimal NewTotal { get; set; }
    }

    public class BulkOverwriteConfirmVm
    {
        public string Category { get; set; } = "";
        public int UploadedCount { get; set; }
        public decimal UploadedTotal { get; set; }
        public int ManualCount { get; set; }
        public decimal ManualTotal { get; set; }
        public int NewCount { get; set; }
        public decimal NewTotal { get; set; }
        public List<BulkOverwriteScopeVm> Scopes { get; set; } = new();
    }
}
