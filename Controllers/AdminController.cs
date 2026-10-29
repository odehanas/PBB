using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using GovBudget.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace GovBudget.Controllers
{
    [Authorize(Policy = "AdminOnly")]
    public class AdminController : Controller
    {
        private readonly GovBudgetContext _db;
        private readonly IHostEnvironment _env;
        private readonly GovBudget.Services.IPasswordResetNotifier _resetNotifier;

        public AdminController(GovBudgetContext db, IHostEnvironment env, GovBudget.Services.IPasswordResetNotifier resetNotifier)
        {
            _db = db;
            _env = env;
            _resetNotifier = resetNotifier;
        }

        private int? GetAdminScopedEntityId()
        {
            var entityClaim = User.Claims.FirstOrDefault(c => c.Type == "EntityId")?.Value;
            if (!int.TryParse(entityClaim, out var entityId) || entityId <= 0)
            {
                return null;
            }

            return entityId;
        }

        private bool IsSystemLevelAdmin(int? adminEntityId)
        {
            return User.IsInRole("SYSADMIN") || (User.IsInRole("ADMIN") && !adminEntityId.HasValue);
        }

        public IActionResult Index()
        {
            return View();
        }

        // GET: Admin/PasswordResets
        [HttpGet]
        public async Task<IActionResult> PasswordResets(string? status = null)
        {
            var adminEntityId = GetAdminScopedEntityId();
            var isSystemLevel = IsSystemLevelAdmin(adminEntityId);

            var query = _db.PasswordResetRequests.AsNoTracking();

            if (!isSystemLevel && adminEntityId.HasValue)
            {
                query = query.Where(r => r.EntityId == adminEntityId.Value);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(r => r.Status == status);
            }

            var rows = await query
                .OrderBy(r => r.Status == "Pending" ? 0 : 1)
                .ThenByDescending(r => r.RequestedAt)
                .Take(200)
                .ToListAsync();

            ViewBag.FilterStatus = status ?? "";
            return View(rows);
        }

        // POST: Admin/IssueResetLink
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> IssueResetLink(long id)
        {
            var adminEntityId = GetAdminScopedEntityId();
            var isSystemLevel = IsSystemLevelAdmin(adminEntityId);

            var req = await _db.PasswordResetRequests.FirstOrDefaultAsync(r => r.ResetRequestId == id);
            if (req == null) return NotFound();

            if (!isSystemLevel && adminEntityId.HasValue && req.EntityId != adminEntityId.Value)
            {
                return Forbid();
            }

            var token = GovBudget.Utils.ResetTokens.Generate();
            var expires = DateTime.UtcNow.Add(AccountController.ResetTokenLifetime);

            // Only the digest is stored, so the row cannot be replayed as a link.
            req.Token = null;
            req.TokenHash = GovBudget.Services.PasswordHasher.HashToken(token);
            req.TokenExpiresAt = expires;
            req.TokenUsedAt = null;
            req.Status = "LinkIssued";
            req.IssuedAt = DateTime.UtcNow;
            req.IssuedBy = User.Identity?.Name ?? "Unknown";

            // Any earlier link for the same user stops working.
            var superseded = await _db.PasswordResetRequests
                .Where(r => r.ResetRequestId != req.ResetRequestId
                            && r.TokenUsedAt == null
                            && r.TokenHash != null
                            && (r.UserName == req.UserName || (req.UserId != null && r.UserId == req.UserId)))
                .ToListAsync();

            foreach (var old in superseded)
            {
                old.TokenHash = null;
                old.Token = null;
                old.TokenExpiresAt = DateTime.UtcNow.AddMinutes(-1);
                old.Status = "Rejected";
                old.AdminNote = "Superseded by a newer reset link.";
            }

            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = req.IssuedBy,
                Action = "UPDATE",
                EntityName = "PasswordResetRequests",
                RecordId = id.ToString(),
                Timestamp = DateTime.UtcNow,
                Details = $"Issued password reset link for user '{req.UserName}'."
            });

            await _db.SaveChangesAsync();

            var resetUrl = Url.Action("ResetPassword", "Account", new { token }, Request.Scheme) ?? "";

            await _resetNotifier.NotifyLinkIssuedAsync(new GovBudget.Services.PasswordResetNotification
            {
                UserName = req.UserName,
                ContactInfo = req.ContactInfo,
                ResetUrl = resetUrl,
                ExpiresAt = expires
            });

            TempData["ResetLink"] = resetUrl;
            TempData["ResetLinkUser"] = req.UserName;
            TempData["Success"] = $"Reset link generated for '{req.UserName}'. It is valid for {AccountController.ResetTokenLifetime.TotalMinutes:0} minutes and can be used once - send it to the user now.";
            return RedirectToAction(nameof(PasswordResets));
        }

        // POST: Admin/RejectReset
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RejectReset(long id, string? note)
        {
            var adminEntityId = GetAdminScopedEntityId();
            var isSystemLevel = IsSystemLevelAdmin(adminEntityId);

            var req = await _db.PasswordResetRequests.FirstOrDefaultAsync(r => r.ResetRequestId == id);
            if (req == null) return NotFound();

            if (!isSystemLevel && adminEntityId.HasValue && req.EntityId != adminEntityId.Value)
            {
                return Forbid();
            }

            req.Status = "Rejected";
            req.RejectedAt = DateTime.UtcNow;
            req.RejectedBy = User.Identity?.Name ?? "Unknown";
            req.AdminNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            req.Token = null;
            req.TokenHash = null;
            req.TokenExpiresAt = null;

            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = req.RejectedBy,
                Action = "UPDATE",
                EntityName = "PasswordResetRequests",
                RecordId = id.ToString(),
                Timestamp = DateTime.UtcNow,
                Details = $"Rejected password reset request for user '{req.UserName}'."
            });

            await _db.SaveChangesAsync();

            TempData["Success"] = $"Request for '{req.UserName}' rejected.";
            return RedirectToAction(nameof(PasswordResets));
        }

        // GET: Admin/Structure
        // Tree: Entity -> Cost Center (Department) -> Program -> Activity -> linked Projects
        [HttpGet]
        public async Task<IActionResult> Structure()
        {
            var adminEntityId = GetAdminScopedEntityId();
            var tree = await BuildStructureTreeAsync(adminEntityId);
            return View(tree);
        }

        // GET: Admin/StructureExportExcel
        [HttpGet]
        public async Task<IActionResult> StructureExportExcel()
        {
            var adminEntityId = GetAdminScopedEntityId();
            var tree = await BuildStructureTreeAsync(adminEntityId);

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Program Structure");
            ws.Outline.SummaryVLocation = XLOutlineSummaryVLocation.Top;

            ws.Cell(1, 1).Value = "Structure";
            ws.Cell(1, 2).Value = "Level";
            ws.Cell(1, 3).Value = "Type";
            ws.Cell(1, 4).Value = "Status";
            ws.Range(1, 1, 1, 4).Style.Font.Bold = true;
            ws.Range(1, 1, 1, 4).Style.Fill.BackgroundColor = XLColor.FromHtml("#0d6efd");
            ws.Range(1, 1, 1, 4).Style.Font.FontColor = XLColor.White;
            ws.SheetView.FreezeRows(1);

            var r = 2;

            void WriteRow(string text, string level, string type, bool isActive, int outlineLevel, string fillHex)
            {
                var indent = outlineLevel - 1;
                var cell = ws.Cell(r, 1);
                cell.Value = text;
                cell.Style.Alignment.Indent = indent;
                if (!string.IsNullOrEmpty(fillHex))
                {
                    cell.Style.Fill.BackgroundColor = XLColor.FromHtml(fillHex);
                }
                if (outlineLevel <= 2) cell.Style.Font.Bold = true;

                ws.Cell(r, 2).Value = level;
                ws.Cell(r, 3).Value = type;
                ws.Cell(r, 4).Value = isActive ? "Active" : "Inactive";
                if (!isActive) ws.Cell(r, 4).Style.Font.FontColor = XLColor.Gray;

                ws.Row(r).OutlineLevel = Math.Min(outlineLevel, 7);
                r++;
            }

            foreach (var e in tree)
            {
                WriteRow($"{e.EntityCode} — {e.EntityName}", "Entity", "", e.IsActive, 1, "#cfe2ff");
                foreach (var d in e.Departments)
                {
                    WriteRow($"{d.DeptCode} — {d.DeptName}", "Cost Center", "", d.IsActive, 2, "#e2e3e5");
                    foreach (var p in d.Programs)
                    {
                        WriteRow($"{p.ProgramCode} — {p.ProgramName}", "Program", p.ProgramType, p.IsActive, 3, "#cff4fc");
                        foreach (var a in p.Activities)
                        {
                            WriteRow($"{a.ActivityCode} — {a.ActivityName}", "Activity", "", a.IsActive, 4, "#d1e7dd");
                            foreach (var pr in a.Projects)
                            {
                                WriteRow($"{pr.ProjectCode} — {pr.ProjectName}", "Project", "", pr.IsActive, 5, "#fff3cd");
                            }
                        }
                    }
                }
            }

            ws.Column(1).Width = 70;
            ws.Columns(2, 4).AdjustToContents();
            var usedRange = ws.RangeUsed();
            if (usedRange != null)
            {
                usedRange.Style.Border.BottomBorder = XLBorderStyleValues.Hair;
                usedRange.Style.Border.BottomBorderColor = XLColor.LightGray;
            }

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            var fileName = $"ProgramStructure_{DateTime.Now:yyyyMMdd}.xlsx";
            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        private async Task<List<StructureEntityNode>> BuildStructureTreeAsync(int? adminEntityId)
        {
            var entitiesQ = _db.Entities.AsNoTracking().AsQueryable();
            if (adminEntityId.HasValue) entitiesQ = entitiesQ.Where(e => e.EntityId == adminEntityId.Value);
            var entities = await entitiesQ.OrderBy(e => e.EntityCode).ToListAsync();

            var deptsQ = _db.Departments.AsNoTracking().AsQueryable();
            if (adminEntityId.HasValue) deptsQ = deptsQ.Where(d => d.EntityId == adminEntityId.Value);
            var depts = await deptsQ.OrderBy(d => d.DeptCode).ToListAsync();

            var programsQ = _db.Programs.AsNoTracking().AsQueryable();
            if (adminEntityId.HasValue) programsQ = programsQ.Where(p => p.EntityId == adminEntityId.Value);
            var programs = await programsQ.OrderBy(p => p.ProgramCode).ToListAsync();
            var programById = programs.ToDictionary(p => p.ProgramId, p => p);

            var activitiesQ = _db.Activities.AsNoTracking().Include(a => a.Program).AsQueryable();
            if (adminEntityId.HasValue) activitiesQ = activitiesQ.Where(a => a.Program.EntityId == adminEntityId.Value);
            var activities = await activitiesQ.OrderBy(a => a.ActivityCode).ToListAsync();

            // Projects linked to an activity through budget lines.
            var linkQ = _db.BudgetLines.AsNoTracking().Where(b => b.ActivityId != null && b.ProjectId != null);
            if (adminEntityId.HasValue) linkQ = linkQ.Where(b => b.EntityId == adminEntityId.Value);
            var links = await linkQ
                .Select(b => new { ActivityId = b.ActivityId!.Value, ProjectId = b.ProjectId!.Value })
                .Distinct()
                .ToListAsync();
            var projectIdsByActivity = links
                .GroupBy(x => x.ActivityId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.ProjectId).Distinct().ToList());

            var projectById = await _db.Projects.AsNoTracking().ToDictionaryAsync(p => p.ProjectId, p => p);

            var tree = new List<StructureEntityNode>();
            foreach (var e in entities)
            {
                var eNode = new StructureEntityNode
                {
                    EntityId = e.EntityId,
                    EntityCode = e.EntityCode,
                    EntityName = e.EntityName,
                    IsActive = e.IsActive
                };

                foreach (var d in depts.Where(d => d.EntityId == e.EntityId))
                {
                    var dNode = new StructureDepartmentNode
                    {
                        DepartmentId = d.DepartmentId,
                        DeptCode = d.DeptCode,
                        DeptName = d.DeptName,
                        IsActive = d.IsActive
                    };

                    var deptActivities = activities.Where(a => a.DepartmentId == d.DepartmentId).ToList();
                    var programGroups = deptActivities
                        .GroupBy(a => a.ProgramId)
                        .OrderBy(g => programById.TryGetValue(g.Key, out var pp) ? pp.ProgramCode : "");

                    foreach (var progGroup in programGroups)
                    {
                        if (!programById.TryGetValue(progGroup.Key, out var prog)) continue;

                        var pNode = new StructureProgramNode
                        {
                            ProgramId = prog.ProgramId,
                            ProgramCode = prog.ProgramCode,
                            ProgramName = prog.ProgramName,
                            ProgramType = string.IsNullOrWhiteSpace(prog.ProgramType) ? "Mandate" : prog.ProgramType,
                            IsActive = prog.IsActive
                        };

                        foreach (var a in progGroup.OrderBy(a => a.ActivityCode))
                        {
                            var aNode = new StructureActivityNode
                            {
                                ActivityId = a.ActivityId,
                                ActivityCode = a.ActivityCode,
                                ActivityName = a.ActivityName,
                                IsActive = a.IsActive
                            };

                            if (projectIdsByActivity.TryGetValue(a.ActivityId, out var pids))
                            {
                                foreach (var pid in pids)
                                {
                                    if (projectById.TryGetValue(pid, out var pr))
                                    {
                                        aNode.Projects.Add(new StructureProjectNode
                                        {
                                            ProjectId = pr.ProjectId,
                                            ProjectCode = pr.ProjectCode,
                                            ProjectName = pr.ProjectName,
                                            IsActive = pr.IsActive
                                        });
                                    }
                                }
                                aNode.Projects = aNode.Projects.OrderBy(x => x.ProjectCode).ToList();
                            }

                            pNode.Activities.Add(aNode);
                        }

                        dNode.Programs.Add(pNode);
                    }

                    eNode.Departments.Add(dNode);
                }

                tree.Add(eNode);
            }

            return tree;
        }

        [HttpGet]
        public async Task<IActionResult> DbPing()
        {
            try
            {
                var ok = await _db.Database.CanConnectAsync();
                var conn = _db.Database.GetDbConnection();
                var dataSource = conn.DataSource ?? "";
                var database = conn.Database ?? "";
                return Json(new
                {
                    ok,
                    environment = _env.EnvironmentName,
                    dataSource,
                    database,
                    isLocalDb = dataSource.Contains("(localdb)", StringComparison.OrdinalIgnoreCase) ||
                                dataSource.Contains("MSSQLLocalDB", StringComparison.OrdinalIgnoreCase)
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    ok = false,
                    environment = _env.EnvironmentName,
                    error = ex.Message
                });
            }
        }

        [HttpGet]
        public async Task<IActionResult> HistoricalActuals(int? year = null, int? entityId = null, int? departmentId = null, string? glCode = null)
        {
            var adminEntityId = GetAdminScopedEntityId();
            var isSystemLevelAdmin = IsSystemLevelAdmin(adminEntityId);
            var isEntityScopedAdmin = User.IsInRole("ADMIN") && adminEntityId.HasValue && !User.IsInRole("SYSADMIN");
            if (!(isSystemLevelAdmin || isEntityScopedAdmin))
            {
                return Forbid();
            }

            var thisYear = DateTime.Now.Year;
            var selectedYear = year ?? (thisYear - 1);

            int? effectiveEntityId = entityId;
            if (adminEntityId.HasValue)
            {
                effectiveEntityId = adminEntityId.Value;
            }

            IQueryable<HistoricalGlActuals> q = _db.HistoricalGlActuals.AsNoTracking()
                .Include(x => x.Entity)
                .Include(x => x.Department)
                .Where(x => x.BudgetYear == selectedYear);

            if (effectiveEntityId.HasValue && effectiveEntityId.Value > 0)
            {
                q = q.Where(x => x.EntityId == effectiveEntityId.Value);
            }

            if (departmentId.HasValue && departmentId.Value > 0)
            {
                q = q.Where(x => x.DepartmentId == departmentId.Value);
            }

            if (!string.IsNullOrWhiteSpace(glCode))
            {
                var needle = glCode.Trim();
                q = q.Where(x => x.GLCode.Contains(needle));
            }

            var rows = await q
                .OrderBy(x => x.Entity.EntityCode)
                .ThenBy(x => x.Department.DeptCode)
                .ThenBy(x => x.GLCode)
                .Take(500)
                .ToListAsync();

            var yearOptions = new List<SelectListItem>();
            for (var y = thisYear; y >= thisYear - 10; y--)
            {
                yearOptions.Add(new SelectListItem(y.ToString(), y.ToString(), y == selectedYear));
            }

            var entityOptions = new List<SelectListItem> { new SelectListItem("All", "", !effectiveEntityId.HasValue) };
            if (User.IsInRole("SYSADMIN"))
            {
                var entities = await _db.Entities.AsNoTracking()
                    .Where(e => e.IsActive)
                    .OrderBy(e => e.EntityCode)
                    .Select(e => new { e.EntityId, e.EntityCode, e.EntityName })
                    .ToListAsync();

                foreach (var e in entities)
                {
                    entityOptions.Add(new SelectListItem($"{e.EntityCode} - {e.EntityName}", e.EntityId.ToString(), effectiveEntityId == e.EntityId));
                }
            }

            var deptOptions = new List<SelectListItem> { new SelectListItem("All", "", !departmentId.HasValue) };
            if ((effectiveEntityId.HasValue && effectiveEntityId.Value > 0) || adminEntityId.HasValue)
            {
                var depts = await _db.Departments.AsNoTracking()
                    .Where(d => d.IsActive && d.EntityId == effectiveEntityId)
                    .OrderBy(d => d.DeptCode)
                    .Select(d => new { d.DepartmentId, d.DeptCode, d.DeptName })
                    .ToListAsync();

                foreach (var d in depts)
                {
                    deptOptions.Add(new SelectListItem($"{d.DeptCode} - {d.DeptName}", d.DepartmentId.ToString(), departmentId == d.DepartmentId));
                }
            }

            ViewBag.SelectedYear = selectedYear;
            ViewBag.YearOptions = yearOptions;
            ViewBag.EntityOptions = entityOptions;
            ViewBag.DepartmentOptions = deptOptions;
            ViewBag.EffectiveEntityId = effectiveEntityId;
            ViewBag.GlCode = glCode ?? "";

            return View(rows);
        }

        [HttpGet]
        public IActionResult HistoricalActualsTemplate()
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("HistoricalActuals");
            ws.Cell(1, 1).Value = "BudgetYear";
            ws.Cell(1, 2).Value = "EntityCode";
            ws.Cell(1, 3).Value = "DeptCode";
            ws.Cell(1, 4).Value = "GLCode";
            ws.Cell(1, 5).Value = "GLType";
            ws.Cell(1, 6).Value = "Amount";
            ws.Range(1, 1, 1, 6).Style.Font.Bold = true;
            ws.Columns(1, 6).AdjustToContents();

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "HistoricalActuals_Template.xlsx");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadHistoricalActuals(IFormFile? file)
        {
            var adminEntityId = GetAdminScopedEntityId();
            var isSystemLevelAdmin = IsSystemLevelAdmin(adminEntityId);
            var isEntityScopedAdmin = User.IsInRole("ADMIN") && adminEntityId.HasValue && !User.IsInRole("SYSADMIN");
            if (!(isSystemLevelAdmin || isEntityScopedAdmin))
            {
                return Forbid();
            }

            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Please choose an Excel file to upload.";
                return RedirectToAction(nameof(HistoricalActuals));
            }

            if (!Path.GetExtension(file.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Only .xlsx files are supported.";
                return RedirectToAction(nameof(HistoricalActuals));
            }

            var errors = new List<string>();
            var created = 0;
            var updated = 0;
            var latestYear = (int?)null;
            var userName = User.Identity?.Name;

            var entityByCode = await _db.Entities.AsNoTracking()
                .ToDictionaryAsync(x => x.EntityCode, x => x, StringComparer.OrdinalIgnoreCase);
            var deptByCode = await _db.Departments.AsNoTracking()
                .ToDictionaryAsync(x => x.DeptCode, x => x, StringComparer.OrdinalIgnoreCase);
            var glSet = await _db.GLAccounts.AsNoTracking()
                .Select(x => x.GLCode)
                .ToHashSetAsync(StringComparer.OrdinalIgnoreCase);

            var parsed = new List<HistoricalGlActuals>();

            using var stream = file.OpenReadStream();
            using var wb = new XLWorkbook(stream);
            var ws = wb.Worksheets.FirstOrDefault();
            if (ws == null)
            {
                TempData["Error"] = "No worksheet found in the uploaded file.";
                return RedirectToAction(nameof(HistoricalActuals));
            }

            var headerRow = ws.FirstRowUsed();
            if (headerRow == null)
            {
                TempData["Error"] = "The uploaded file is empty.";
                return RedirectToAction(nameof(HistoricalActuals));
            }

            var headerRowNumber = headerRow.RowNumber();
            var colMap = BuildHeaderMap(headerRow);

            var hasYearCol = colMap.TryGetValue("budgetyear", out var yearCol) || colMap.TryGetValue("year", out yearCol);
            var hasEntityCol = colMap.TryGetValue("entitycode", out var entityCol) || colMap.TryGetValue("entity", out entityCol);
            var hasDeptCol = colMap.TryGetValue("deptcode", out var deptCol) || colMap.TryGetValue("departmentcode", out deptCol) || colMap.TryGetValue("department", out deptCol);
            var hasGlCol = colMap.TryGetValue("glcode", out var glCol) || colMap.TryGetValue("gl", out glCol) || colMap.TryGetValue("glaccountcode", out glCol);
            var hasGlTypeCol = colMap.TryGetValue("gltype", out var glTypeCol) || colMap.TryGetValue("type", out glTypeCol);
            var hasAmountCol = colMap.TryGetValue("amount", out var amountCol) || colMap.TryGetValue("annualamount", out amountCol);

            if (!hasYearCol || !hasEntityCol || !hasDeptCol || !hasGlCol || !hasGlTypeCol || !hasAmountCol)
            {
                TempData["Error"] = "Missing required columns. Required: BudgetYear, EntityCode, DeptCode, GLCode, GLType, Amount.";
                return RedirectToAction(nameof(HistoricalActuals));
            }

            var lastRow = ws.LastRowUsed()?.RowNumber() ?? headerRowNumber;
            for (var r = headerRowNumber + 1; r <= lastRow; r++)
            {
                var row = ws.Row(r);

                var yearRaw = row.Cell(yearCol).GetValue<string>().Trim();
                var entityCode = row.Cell(entityCol).GetString().Trim();
                var deptCode = row.Cell(deptCol).GetString().Trim();
                var glCode = row.Cell(glCol).GetString().Trim();
                var glTypeRaw = row.Cell(glTypeCol).GetString().Trim();
                var amountRaw = row.Cell(amountCol).GetValue<string>().Trim();

                if (string.IsNullOrWhiteSpace(yearRaw) &&
                    string.IsNullOrWhiteSpace(entityCode) &&
                    string.IsNullOrWhiteSpace(deptCode) &&
                    string.IsNullOrWhiteSpace(glCode) &&
                    string.IsNullOrWhiteSpace(amountRaw))
                {
                    continue;
                }

                if (!int.TryParse(yearRaw, out var budgetYear) || budgetYear < 1900 || budgetYear > 3000)
                {
                    errors.Add($"Row {r}: Invalid BudgetYear '{yearRaw}'.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entityCode) || !entityByCode.TryGetValue(entityCode, out var ent))
                {
                    errors.Add($"Row {r}: Unknown EntityCode '{entityCode}'.");
                    continue;
                }

                if (adminEntityId.HasValue && ent.EntityId != adminEntityId.Value)
                {
                    errors.Add($"Row {r}: EntityCode '{entityCode}' is not allowed for this user.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(deptCode) || !deptByCode.TryGetValue(deptCode, out var dep))
                {
                    errors.Add($"Row {r}: Unknown DeptCode '{deptCode}'.");
                    continue;
                }

                if (dep.EntityId != ent.EntityId)
                {
                    errors.Add($"Row {r}: DeptCode '{deptCode}' does not belong to EntityCode '{entityCode}'.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(glCode))
                {
                    errors.Add($"Row {r}: Missing GLCode.");
                    continue;
                }

                if (!glSet.Contains(glCode))
                {
                    errors.Add($"Row {r}: Unknown GLCode '{glCode}'.");
                    continue;
                }

                if (!TryNormalizeGlType(glTypeRaw, out var normalizedGlType))
                {
                    errors.Add($"Row {r}: Invalid GLType '{glTypeRaw}'. Allowed: Revenue, HR, Opex, Capex.");
                    continue;
                }

                if (!TryParseDecimal(amountRaw, out var amount))
                {
                    errors.Add($"Row {r}: Invalid Amount '{amountRaw}'.");
                    continue;
                }

                latestYear = !latestYear.HasValue ? budgetYear : Math.Max(latestYear.Value, budgetYear);

                parsed.Add(new HistoricalGlActuals
                {
                    BudgetYear = budgetYear,
                    EntityId = ent.EntityId,
                    DepartmentId = dep.DepartmentId,
                    GLCode = glCode,
                    GLType = normalizedGlType,
                    Amount = amount,
                    CreatedBy = userName,
                    SourceFile = file.FileName
                });
            }

            if (errors.Count > 0)
            {
                TempData["Error"] = "Upload failed:\n" + string.Join("\n", errors.Take(50));
                return RedirectToAction(nameof(HistoricalActuals));
            }

            if (parsed.Count == 0)
            {
                TempData["Error"] = "No valid rows found in the uploaded file.";
                return RedirectToAction(nameof(HistoricalActuals));
            }

            var yearsInFile = parsed.Select(x => x.BudgetYear).Distinct().ToList();
            var entityIdsInFile = parsed.Select(x => x.EntityId).Distinct().ToList();
            var deptIdsInFile = parsed.Select(x => x.DepartmentId).Distinct().ToList();

            var existing = await _db.HistoricalGlActuals
                .Where(x => yearsInFile.Contains(x.BudgetYear)
                            && entityIdsInFile.Contains(x.EntityId)
                            && deptIdsInFile.Contains(x.DepartmentId))
                .ToListAsync();

            var existingByKey = existing.ToDictionary(
                x => (x.BudgetYear, x.EntityId, x.DepartmentId, (x.GLCode ?? "").Trim().ToUpperInvariant()),
                x => x);

            foreach (var row in parsed)
            {
                var key = (row.BudgetYear, row.EntityId, row.DepartmentId, (row.GLCode ?? "").Trim().ToUpperInvariant());
                if (existingByKey.TryGetValue(key, out var ex))
                {
                    ex.Amount = row.Amount;
                    ex.GLType = row.GLType;
                    ex.CreatedBy = row.CreatedBy;
                    ex.SourceFile = row.SourceFile;
                    updated++;
                }
                else
                {
                    _db.HistoricalGlActuals.Add(row);
                    created++;
                }
            }

            await _db.SaveChangesAsync();
            TempData["Success"] = $"Upload complete. Created: {created}. Updated: {updated}.";

            return RedirectToAction(nameof(HistoricalActuals), new { year = latestYear });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteHistoricalActual(long id, int? year = null, int? entityId = null, int? departmentId = null, string? glCode = null)
        {
            var adminEntityId = GetAdminScopedEntityId();
            var isSystemLevelAdmin = IsSystemLevelAdmin(adminEntityId);
            var isEntityScopedAdmin = User.IsInRole("ADMIN") && adminEntityId.HasValue && !User.IsInRole("SYSADMIN");
            if (!(isSystemLevelAdmin || isEntityScopedAdmin))
            {
                return Forbid();
            }

            var row = await _db.HistoricalGlActuals.FirstOrDefaultAsync(x => x.HistoricalActualId == id);
            if (row == null)
            {
                return RedirectToAction(nameof(HistoricalActuals), new { year, entityId, departmentId, glCode });
            }

            if (adminEntityId.HasValue && row.EntityId != adminEntityId.Value)
            {
                return Forbid();
            }

            _db.HistoricalGlActuals.Remove(row);
            await _db.SaveChangesAsync();
            TempData["Success"] = "Deleted.";

            return RedirectToAction(nameof(HistoricalActuals), new { year, entityId, departmentId, glCode });
        }

        [HttpGet]
        public async Task<IActionResult> MidYearActuals(int? year = null, int? entityId = null, string? glCode = null)
        {
            var adminEntityId = GetAdminScopedEntityId();
            var isSystemLevelAdmin = IsSystemLevelAdmin(adminEntityId);
            var isEntityScopedAdmin = User.IsInRole("ADMIN") && adminEntityId.HasValue && !User.IsInRole("SYSADMIN");
            if (!(isSystemLevelAdmin || isEntityScopedAdmin))
            {
                return Forbid();
            }

            var thisYear = DateTime.Now.Year;
            var selectedYear = year ?? thisYear;

            int? effectiveEntityId = entityId;
            if (adminEntityId.HasValue)
            {
                effectiveEntityId = adminEntityId.Value;
            }

            IQueryable<MidYearGlActualForecasts> q = _db.MidYearGlActualForecasts.AsNoTracking()
                .Include(x => x.Entity)
                .Where(x => x.BudgetYear == selectedYear);

            if (effectiveEntityId.HasValue && effectiveEntityId.Value > 0)
            {
                q = q.Where(x => x.EntityId == effectiveEntityId.Value);
            }

            if (!string.IsNullOrWhiteSpace(glCode))
            {
                var needle = glCode.Trim();
                q = q.Where(x => x.GLCode.Contains(needle));
            }

            var rows = await q
                .OrderBy(x => x.Entity.EntityCode)
                .ThenBy(x => x.GLType)
                .ThenBy(x => x.GLCode)
                .Take(500)
                .ToListAsync();

            var yearOptions = new List<SelectListItem>();
            for (var y = thisYear + 1; y >= thisYear - 5; y--)
            {
                yearOptions.Add(new SelectListItem(y.ToString(), y.ToString(), y == selectedYear));
            }

            var entityOptions = new List<SelectListItem> { new SelectListItem("All", "", !effectiveEntityId.HasValue) };
            if (User.IsInRole("SYSADMIN"))
            {
                var entities = await _db.Entities.AsNoTracking()
                    .Where(e => e.IsActive)
                    .OrderBy(e => e.EntityCode)
                    .Select(e => new { e.EntityId, e.EntityCode, e.EntityName })
                    .ToListAsync();

                foreach (var e in entities)
                {
                    entityOptions.Add(new SelectListItem($"{e.EntityCode} - {e.EntityName}", e.EntityId.ToString(), effectiveEntityId == e.EntityId));
                }
            }

            ViewBag.SelectedYear = selectedYear;
            ViewBag.YearOptions = yearOptions;
            ViewBag.EntityOptions = entityOptions;
            ViewBag.EffectiveEntityId = effectiveEntityId;
            ViewBag.GlCode = glCode ?? "";

            return View(rows);
        }

        [HttpGet]
        public IActionResult MidYearActualsTemplate()
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("MidYearActuals");
            ws.Cell(1, 1).Value = "BudgetYear";
            ws.Cell(1, 2).Value = "EntityCode";
            ws.Cell(1, 3).Value = "GLCode";
            ws.Cell(1, 4).Value = "GLType";
            ws.Cell(1, 5).Value = "ActualH1Amount";
            ws.Range(1, 1, 1, 5).Style.Font.Bold = true;
            ws.Columns(1, 5).AdjustToContents();

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "MidYearActuals_Template.xlsx");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadMidYearActuals(IFormFile? file)
        {
            var adminEntityId = GetAdminScopedEntityId();
            var isSystemLevelAdmin = IsSystemLevelAdmin(adminEntityId);
            var isEntityScopedAdmin = User.IsInRole("ADMIN") && adminEntityId.HasValue && !User.IsInRole("SYSADMIN");
            if (!(isSystemLevelAdmin || isEntityScopedAdmin))
            {
                return Forbid();
            }

            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Please choose an Excel file to upload.";
                return RedirectToAction(nameof(MidYearActuals));
            }

            if (!Path.GetExtension(file.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Only .xlsx files are supported.";
                return RedirectToAction(nameof(MidYearActuals));
            }

            var errors = new List<string>();
            var created = 0;
            var updated = 0;
            var latestYear = (int?)null;
            var userName = User.Identity?.Name;

            var entityByCode = await _db.Entities.AsNoTracking()
                .ToDictionaryAsync(x => x.EntityCode, x => x, StringComparer.OrdinalIgnoreCase);
            var glSet = await _db.GLAccounts.AsNoTracking()
                .Select(x => x.GLCode)
                .ToHashSetAsync(StringComparer.OrdinalIgnoreCase);

            var parsed = new List<MidYearGlActualForecasts>();

            using var stream = file.OpenReadStream();
            using var wb = new XLWorkbook(stream);
            var ws = wb.Worksheets.FirstOrDefault();
            if (ws == null)
            {
                TempData["Error"] = "No worksheet found in the uploaded file.";
                return RedirectToAction(nameof(MidYearActuals));
            }

            var headerRow = ws.FirstRowUsed();
            if (headerRow == null)
            {
                TempData["Error"] = "The uploaded file is empty.";
                return RedirectToAction(nameof(MidYearActuals));
            }

            var headerRowNumber = headerRow.RowNumber();
            var colMap = BuildHeaderMap(headerRow);

            var hasYearCol = colMap.TryGetValue("budgetyear", out var yearCol) || colMap.TryGetValue("year", out yearCol);
            var hasEntityCol = colMap.TryGetValue("entitycode", out var entityCol) || colMap.TryGetValue("entity", out entityCol);
            var hasGlCol = colMap.TryGetValue("glcode", out var glCol) || colMap.TryGetValue("gl", out glCol) || colMap.TryGetValue("glaccountcode", out glCol);
            var hasGlTypeCol = colMap.TryGetValue("gltype", out var glTypeCol) || colMap.TryGetValue("type", out glTypeCol);
            var hasActualCol = colMap.TryGetValue("actualh1amount", out var actualCol) || colMap.TryGetValue("h1amount", out actualCol) || colMap.TryGetValue("actual", out actualCol);

            if (!hasYearCol || !hasEntityCol || !hasGlCol || !hasGlTypeCol || !hasActualCol)
            {
                TempData["Error"] = "Missing required columns. Required: BudgetYear, EntityCode, GLCode, GLType, ActualH1Amount.";
                return RedirectToAction(nameof(MidYearActuals));
            }

            var lastRow = ws.LastRowUsed()?.RowNumber() ?? headerRowNumber;
            for (var r = headerRowNumber + 1; r <= lastRow; r++)
            {
                var row = ws.Row(r);

                var yearRaw = row.Cell(yearCol).GetValue<string>().Trim();
                var entityCode = row.Cell(entityCol).GetString().Trim();
                var glCode = row.Cell(glCol).GetString().Trim();
                var glTypeRaw = row.Cell(glTypeCol).GetString().Trim();
                var actualRaw = row.Cell(actualCol).GetValue<string>().Trim();

                if (string.IsNullOrWhiteSpace(yearRaw) &&
                    string.IsNullOrWhiteSpace(entityCode) &&
                    string.IsNullOrWhiteSpace(glCode) &&
                    string.IsNullOrWhiteSpace(glTypeRaw) &&
                    string.IsNullOrWhiteSpace(actualRaw))
                {
                    continue;
                }

                if (!int.TryParse(yearRaw, out var budgetYear) || budgetYear < 1900 || budgetYear > 3000)
                {
                    errors.Add($"Row {r}: Invalid BudgetYear '{yearRaw}'.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entityCode) || !entityByCode.TryGetValue(entityCode, out var ent))
                {
                    errors.Add($"Row {r}: Unknown EntityCode '{entityCode}'.");
                    continue;
                }

                if (adminEntityId.HasValue && ent.EntityId != adminEntityId.Value)
                {
                    errors.Add($"Row {r}: EntityCode '{entityCode}' is not allowed for this user.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(glCode))
                {
                    errors.Add($"Row {r}: Missing GLCode.");
                    continue;
                }

                if (!glSet.Contains(glCode))
                {
                    errors.Add($"Row {r}: Unknown GLCode '{glCode}'.");
                    continue;
                }

                if (!TryNormalizeGlType(glTypeRaw, out var normalizedGlType))
                {
                    errors.Add($"Row {r}: Invalid GLType '{glTypeRaw}'. Allowed: Revenue, HR, Opex, Capex.");
                    continue;
                }

                if (!TryParseDecimal(actualRaw, out var actualAmount))
                {
                    errors.Add($"Row {r}: Invalid ActualH1Amount '{actualRaw}'.");
                    continue;
                }

                latestYear = !latestYear.HasValue ? budgetYear : Math.Max(latestYear.Value, budgetYear);

                parsed.Add(new MidYearGlActualForecasts
                {
                    BudgetYear = budgetYear,
                    EntityId = ent.EntityId,
                    GLCode = glCode,
                    GLType = normalizedGlType,
                    ActualH1Amount = actualAmount,
                    CreatedBy = userName,
                    SourceFile = file.FileName
                });
            }

            if (errors.Count > 0)
            {
                TempData["Error"] = "Upload failed:\n" + string.Join("\n", errors.Take(50));
                return RedirectToAction(nameof(MidYearActuals));
            }

            if (parsed.Count == 0)
            {
                TempData["Error"] = "No valid rows found in the uploaded file.";
                return RedirectToAction(nameof(MidYearActuals));
            }

            var yearsInFile = parsed.Select(x => x.BudgetYear).Distinct().ToList();
            var entityIdsInFile = parsed.Select(x => x.EntityId).Distinct().ToList();

            var existing = await _db.MidYearGlActualForecasts
                .Where(x => yearsInFile.Contains(x.BudgetYear) && entityIdsInFile.Contains(x.EntityId))
                .ToListAsync();

            var existingByKey = existing.ToDictionary(
                x => (x.BudgetYear, x.EntityId, (x.GLCode ?? "").Trim().ToUpperInvariant()),
                x => x);

            foreach (var row in parsed)
            {
                var key = (row.BudgetYear, row.EntityId, (row.GLCode ?? "").Trim().ToUpperInvariant());
                if (existingByKey.TryGetValue(key, out var ex))
                {
                    ex.GLType = row.GLType;
                    ex.ActualH1Amount = row.ActualH1Amount;
                    ex.CreatedBy = row.CreatedBy;
                    ex.SourceFile = row.SourceFile;
                    updated++;
                }
                else
                {
                    _db.MidYearGlActualForecasts.Add(row);
                    created++;
                }
            }

            await _db.SaveChangesAsync();
            TempData["Success"] = $"Upload complete. Created: {created}. Updated: {updated}.";

            return RedirectToAction(nameof(MidYearActuals), new { year = latestYear });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteMidYearActual(long id, int? year = null, int? entityId = null, string? glCode = null)
        {
            var adminEntityId = GetAdminScopedEntityId();
            var isSystemLevelAdmin = IsSystemLevelAdmin(adminEntityId);
            var isEntityScopedAdmin = User.IsInRole("ADMIN") && adminEntityId.HasValue && !User.IsInRole("SYSADMIN");
            if (!(isSystemLevelAdmin || isEntityScopedAdmin))
            {
                return Forbid();
            }

            var row = await _db.MidYearGlActualForecasts.FirstOrDefaultAsync(x => x.MidYearId == id);
            if (row == null)
            {
                return RedirectToAction(nameof(MidYearActuals), new { year, entityId, glCode });
            }

            if (adminEntityId.HasValue && row.EntityId != adminEntityId.Value)
            {
                return Forbid();
            }

            _db.MidYearGlActualForecasts.Remove(row);
            await _db.SaveChangesAsync();
            TempData["Success"] = "Deleted.";

            return RedirectToAction(nameof(MidYearActuals), new { year, entityId, glCode });
        }

        private static Dictionary<string, int> BuildHeaderMap(IXLRow headerRow)
        {
            var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in headerRow.CellsUsed())
            {
                var name = (cell.GetString() ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (!headerMap.ContainsKey(name))
                {
                    headerMap[name] = cell.Address.ColumnNumber;
                }
            }
            return headerMap;
        }

        private static bool TryParseDecimal(string raw, out decimal value)
        {
            var s = (raw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s))
            {
                value = 0m;
                return false;
            }

            s = s.Replace(",", "");
            return decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryNormalizeGlType(string raw, out string normalized)
        {
            var t = (raw ?? "").Trim().ToUpperInvariant();
            normalized = t switch
            {
                "REVENUE" => "REVENUE",
                "REV" => "REVENUE",
                "HR" => "HR",
                "OPEX" => "OPEX",
                "CAPEX" => "CAPEX",
                _ => ""
            };

            return normalized != "";
        }

        [HttpGet]
        public async Task<IActionResult> Submissions(int? year = null, string? status = null)
        {
            var selectedYear = year ?? DateTime.Now.Year;
            var adminEntityId = GetAdminScopedEntityId();

            var query = _db.BudgetSubmissions.AsNoTracking()
                .Include(s => s.Entity)
                .Include(s => s.Department)
                .Include(s => s.Category)
                .Where(s => s.BudgetYear == selectedYear);

            if (adminEntityId.HasValue)
            {
                query = query.Where(s => s.EntityId == adminEntityId.Value);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(s => s.Status == status);
            }

            var rows = await query
                .OrderBy(s => s.Entity.EntityCode)
                .ThenBy(s => s.Department.DeptCode)
                .ThenBy(s => s.Category.CategoryCode)
                .ThenByDescending(s => s.VersionNo)
                .ThenByDescending(s => s.SubmittedAt)
                .Take(500)
                .ToListAsync();

            ViewBag.SelectedYear = selectedYear;
            ViewBag.FilterStatus = status ?? "";
            return View(rows);
        }

        [HttpGet]
        public async Task<IActionResult> ReviewSubmission(long id)
        {
            var adminEntityId = GetAdminScopedEntityId();
            var submission = await _db.BudgetSubmissions.AsNoTracking()
                .Include(s => s.Entity)
                .Include(s => s.Department)
                .Include(s => s.Category)
                .FirstOrDefaultAsync(s => s.SubmissionId == id);

            if (submission == null) return NotFound();
            if (adminEntityId.HasValue && submission.EntityId != adminEntityId.Value) return Forbid();

            var snapshotLines = await _db.BudgetSubmissionLines.AsNoTracking()
                .Where(b => b.SubmissionId == submission.SubmissionId)
                .OrderByDescending(b => b.SourceBudgetLineId)
                .Take(200)
                .ToListAsync();

            if (snapshotLines.Count > 0)
            {
                ViewBag.LineCount = snapshotLines.Count;
                ViewBag.TotalAmount = snapshotLines.Sum(x => x.Amount);
                ViewBag.Lines = snapshotLines;
                return View(submission);
            }

            var liveLines = await _db.BudgetLines.AsNoTracking()
                .Where(b => b.BudgetYear == submission.BudgetYear
                            && b.EntityId == submission.EntityId
                            && b.DepartmentId == submission.DepartmentId
                            && b.CategoryId == submission.CategoryId)
                .OrderByDescending(b => b.BudgetLineId)
                .Take(200)
                .ToListAsync();

            ViewBag.LineCount = liveLines.Count;
            ViewBag.TotalAmount = liveLines.Sum(x => x.Amount);
            ViewBag.Lines = liveLines;

            return View(submission);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApproveSubmission(long id, string? approvalNote)
        {
            var userName = User.Identity?.Name ?? "Unknown";
            var adminEntityId = GetAdminScopedEntityId();

            var submission = await _db.BudgetSubmissions.FirstOrDefaultAsync(s => s.SubmissionId == id);
            if (submission == null) return NotFound();
            if (adminEntityId.HasValue && submission.EntityId != adminEntityId.Value) return Forbid();

            if (!string.Equals(submission.Status, "Submitted", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Only submissions in Submitted status can be approved.";
                return RedirectToAction(nameof(ReviewSubmission), new { id });
            }

            var approvedAt = DateTime.UtcNow;
            submission.Status = "EntityApproved";
            submission.ApprovedAt = approvedAt;
            submission.ApprovedBy = userName;
            submission.ApprovalNote = string.IsNullOrWhiteSpace(approvalNote) ? null : approvalNote.Trim();
            await _db.SaveChangesAsync();

            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = userName,
                Action = "UPDATE",
                EntityName = "BudgetSubmissions",
                RecordId = submission.SubmissionId.ToString(),
                Timestamp = DateTime.UtcNow,
                Details = $"Entity approved budget submission {submission.SubmissionId}."
            });
            await _db.SaveChangesAsync();

            TempData["Success"] = "Entity approval recorded.";
            return RedirectToAction(nameof(ReviewSubmission), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReturnSubmission(long id, string? returnNote)
        {
            var userName = User.Identity?.Name ?? "Unknown";
            var adminEntityId = GetAdminScopedEntityId();
            var isSystemLevelAdmin = IsSystemLevelAdmin(adminEntityId);
            var isEntityScopedAdmin = User.IsInRole("ADMIN") && adminEntityId.HasValue && !User.IsInRole("SYSADMIN");
            if (!(isSystemLevelAdmin || isEntityScopedAdmin))
            {
                return Forbid();
            }

            var submission = await _db.BudgetSubmissions.FirstOrDefaultAsync(s => s.SubmissionId == id);
            if (submission == null) return NotFound();
            if (adminEntityId.HasValue && submission.EntityId != adminEntityId.Value) return Forbid();

            var latest = await _db.BudgetSubmissions
                .Where(s => s.BudgetYear == submission.BudgetYear
                            && s.EntityId == submission.EntityId
                            && s.DepartmentId == submission.DepartmentId
                            && s.CategoryId == submission.CategoryId)
                .OrderByDescending(s => s.VersionNo)
                .FirstOrDefaultAsync();

            if (latest == null || latest.SubmissionId != submission.SubmissionId)
            {
                TempData["Error"] = "Only the latest submission version can be returned.";
                return RedirectToAction(nameof(ReviewSubmission), new { id });
            }

            var canReturnStatus = isSystemLevelAdmin
                ? (string.Equals(submission.Status, "Submitted", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(submission.Status, "EntityApproved", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(submission.Status, "Approved", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(submission.Status, "SentToCentral", StringComparison.OrdinalIgnoreCase))
                : string.Equals(submission.Status, "Submitted", StringComparison.OrdinalIgnoreCase);

            if (!canReturnStatus)
            {
                TempData["Error"] = "This submission status cannot be returned for revision.";
                return RedirectToAction(nameof(ReviewSubmission), new { id });
            }

            await using var tx = await _db.Database.BeginTransactionAsync();

            var note = string.IsNullOrWhiteSpace(returnNote) ? null : returnNote.Trim();

            submission.Status = "Returned";
            submission.ReturnedAt = DateTime.UtcNow;
            submission.ReturnedBy = userName;
            submission.ReturnNote = note;

            var next = new BudgetSubmissions
            {
                BudgetYear = submission.BudgetYear,
                EntityId = submission.EntityId,
                DepartmentId = submission.DepartmentId,
                CategoryId = submission.CategoryId,
                VersionNo = submission.VersionNo + 1,
                ParentSubmissionId = submission.SubmissionId,
                Status = "Draft"
            };
            _db.BudgetSubmissions.Add(next);

            _db.BudgetRevisionRequests.Add(new BudgetRevisionRequests
            {
                SubmissionId = submission.SubmissionId,
                ActionType = isSystemLevelAdmin ? "ReturnAndUnlock" : "RejectAndUnlock",
                Note = note,
                RequestedAt = DateTime.UtcNow,
                RequestedBy = userName
            });

            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = userName,
                Action = "UPDATE",
                EntityName = "BudgetSubmissions",
                RecordId = submission.SubmissionId.ToString(),
                Timestamp = DateTime.UtcNow,
                Details = $"{(isSystemLevelAdmin ? "Returned" : "Rejected")} submission {submission.SubmissionId} and unlocked revision v{next.VersionNo}."
            });

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            TempData["Success"] = $"{(isSystemLevelAdmin ? "Returned" : "Rejected")} for revision. New draft version v{next.VersionNo} is unlocked for editing.";
            return RedirectToAction(nameof(ReviewSubmission), new { id });
        }

        [HttpGet]
        public async Task<IActionResult> Variance(long id, long? compareToId = null)
        {
            var adminEntityId = GetAdminScopedEntityId();

            var current = await _db.BudgetSubmissions.AsNoTracking()
                .Include(s => s.Entity)
                .Include(s => s.Department)
                .Include(s => s.Category)
                .FirstOrDefaultAsync(s => s.SubmissionId == id);
            if (current == null) return NotFound();
            if (adminEntityId.HasValue && current.EntityId != adminEntityId.Value) return Forbid();

            BudgetSubmissions? previous = null;
            if (compareToId.HasValue)
            {
                previous = await _db.BudgetSubmissions.AsNoTracking()
                    .Include(s => s.Entity)
                    .Include(s => s.Department)
                    .Include(s => s.Category)
                    .FirstOrDefaultAsync(s => s.SubmissionId == compareToId.Value);
            }
            else if (current.ParentSubmissionId.HasValue)
            {
                previous = await _db.BudgetSubmissions.AsNoTracking()
                    .Include(s => s.Entity)
                    .Include(s => s.Department)
                    .Include(s => s.Category)
                    .FirstOrDefaultAsync(s => s.SubmissionId == current.ParentSubmissionId.Value);
            }
            else if (current.VersionNo > 1)
            {
                previous = await _db.BudgetSubmissions.AsNoTracking()
                    .Where(s => s.BudgetYear == current.BudgetYear
                                && s.EntityId == current.EntityId
                                && s.DepartmentId == current.DepartmentId
                                && s.CategoryId == current.CategoryId
                                && s.VersionNo < current.VersionNo)
                    .OrderByDescending(s => s.VersionNo)
                    .FirstOrDefaultAsync();
            }

            if (previous == null)
            {
                TempData["Error"] = "No previous version found to compare.";
                return RedirectToAction(nameof(ReviewSubmission), new { id });
            }

            var currentLines = await _db.BudgetSubmissionLines.AsNoTracking()
                .Where(l => l.SubmissionId == current.SubmissionId)
                .ToListAsync();
            var previousLines = await _db.BudgetSubmissionLines.AsNoTracking()
                .Where(l => l.SubmissionId == previous.SubmissionId)
                .ToListAsync();

            string MakeKey(BudgetSubmissionLines l)
            {
                var desc = (l.Description ?? "").Trim();
                return $"{l.ItemId}|{l.ProgramId}|{l.ActivityId}|{l.ProjectId}|{desc}";
            }

            var itemIds = currentLines.Select(l => l.ItemId).Concat(previousLines.Select(l => l.ItemId)).Distinct().ToList();
            var items = await _db.Items.AsNoTracking()
                .Where(i => itemIds.Contains(i.ItemId))
                .Select(i => new { i.ItemId, i.ItemCode, i.ItemName })
                .ToListAsync();
            var itemDisplay = items.ToDictionary(i => i.ItemId, i => $"{i.ItemCode} - {i.ItemName}");

            var actIds = currentLines.Where(l => l.ActivityId.HasValue).Select(l => l.ActivityId!.Value)
                .Concat(previousLines.Where(l => l.ActivityId.HasValue).Select(l => l.ActivityId!.Value))
                .Distinct()
                .ToList();
            var activities = await _db.Activities.AsNoTracking()
                .Where(a => actIds.Contains(a.ActivityId))
                .Select(a => new { a.ActivityId, a.ActivityCode, a.ActivityName })
                .ToListAsync();
            var actDisplay = activities.ToDictionary(a => a.ActivityId, a => $"{a.ActivityCode} - {a.ActivityName}");

            var projIds = currentLines.Where(l => l.ProjectId.HasValue).Select(l => l.ProjectId!.Value)
                .Concat(previousLines.Where(l => l.ProjectId.HasValue).Select(l => l.ProjectId!.Value))
                .Distinct()
                .ToList();
            var projects = await _db.Projects.AsNoTracking()
                .Where(p => projIds.Contains(p.ProjectId))
                .Select(p => new { p.ProjectId, p.ProjectCode, p.ProjectName })
                .ToListAsync();
            var projDisplay = projects.ToDictionary(p => p.ProjectId, p => $"{p.ProjectCode} - {p.ProjectName}");

            var prevByKey = previousLines.GroupBy(MakeKey).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
            var curByKey = currentLines.GroupBy(MakeKey).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

            var keys = prevByKey.Keys.Concat(curByKey.Keys).Distinct().OrderBy(k => k).ToList();
            var rows = keys.Select(k =>
            {
                var parts = k.Split('|');
                var itemId = int.Parse(parts[0]);
                int? programId = string.IsNullOrWhiteSpace(parts[1]) ? null : int.Parse(parts[1]);
                int? activityId = string.IsNullOrWhiteSpace(parts[2]) ? null : int.Parse(parts[2]);
                int? projectId = string.IsNullOrWhiteSpace(parts[3]) ? null : int.Parse(parts[3]);
                var desc = parts.Length > 4 ? parts[4] : "";

                var oldAmt = prevByKey.TryGetValue(k, out var o) ? o : 0m;
                var newAmt = curByKey.TryGetValue(k, out var n) ? n : 0m;

                return new SubmissionVarianceRow
                {
                    Item = itemDisplay.TryGetValue(itemId, out var it) ? it : itemId.ToString(),
                    ProgramId = programId,
                    Activity = activityId.HasValue && actDisplay.TryGetValue(activityId.Value, out var ad) ? ad : "",
                    Project = projectId.HasValue && projDisplay.TryGetValue(projectId.Value, out var pd) ? pd : "",
                    Description = desc,
                    OldAmount = oldAmt,
                    NewAmount = newAmt
                };
            }).Where(r => r.Delta != 0m).OrderByDescending(r => Math.Abs(r.Delta)).ToList();

            ViewBag.Current = current;
            ViewBag.Previous = previous;
            ViewBag.TotalOld = prevByKey.Values.Sum();
            ViewBag.TotalNew = curByKey.Values.Sum();
            return View(rows);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendToCentral(long id)
        {
            var userName = User.Identity?.Name ?? "Unknown";
            var adminEntityId = GetAdminScopedEntityId();

            var submission = await _db.BudgetSubmissions.FirstOrDefaultAsync(s => s.SubmissionId == id);
            if (submission == null) return NotFound();
            if (adminEntityId.HasValue && submission.EntityId != adminEntityId.Value) return Forbid();

            if (!(string.Equals(submission.Status, "EntityApproved", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(submission.Status, "Approved", StringComparison.OrdinalIgnoreCase)))
            {
                TempData["Error"] = "Only Entity Approved submissions can be sent to central finance.";
                return RedirectToAction(nameof(ReviewSubmission), new { id });
            }

            submission.Status = "SentToCentral";
            submission.SentToCentralAt = DateTime.UtcNow;
            submission.SentToCentralBy = userName;

            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = userName,
                Action = "UPDATE",
                EntityName = "BudgetSubmissions",
                RecordId = submission.SubmissionId.ToString(),
                Timestamp = DateTime.UtcNow,
                Details = $"Sent budget submission {submission.SubmissionId} to central finance."
            });

            await _db.SaveChangesAsync();

            TempData["Success"] = "Marked as sent to central finance.";
            return RedirectToAction(nameof(ReviewSubmission), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SysApproveSubmission(long id, string? sysApprovalNote)
        {
            if (!User.IsInRole("SYSADMIN"))
            {
                return Forbid();
            }

            var userName = User.Identity?.Name ?? "Unknown";

            var submission = await _db.BudgetSubmissions.FirstOrDefaultAsync(s => s.SubmissionId == id);
            if (submission == null) return NotFound();

            if (!string.Equals(submission.Status, "SentToCentral", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Only SentToCentral submissions can be finally approved.";
                return RedirectToAction(nameof(ReviewSubmission), new { id });
            }

            await using var tx = await _db.Database.BeginTransactionAsync();

            var approvedAt = DateTime.UtcNow;
            submission.Status = "SysApproved";
            submission.SysApprovedAt = approvedAt;
            submission.SysApprovedBy = userName;
            submission.SysApprovalNote = string.IsNullOrWhiteSpace(sysApprovalNote) ? null : sysApprovalNote.Trim();
            submission.FinalizedAt = approvedAt;
            submission.FinalizedBy = userName;
            await _db.SaveChangesAsync();

            var deleteSql = @"
DELETE FROM core.DOF_CombindBudget_Final
WHERE SubmissionId = {0};";
            await _db.Database.ExecuteSqlRawAsync(deleteSql, submission.SubmissionId);

            await _db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.DOF_CombindBudget_Final
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
    ApprovedAt,
    ApprovedBy,
    ApprovalNote
)
SELECT
    {submission.SubmissionId} AS SubmissionId,
    b.SourceBudgetLineId AS SourceBudgetLineId,
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
    b.DocFileName,
    b.DocContentType,
    b.DocSizeBytes,
    b.DocContent,
    b.DocUploadedAt,
    b.DocUploadedBy,
    {approvedAt} AS ApprovedAt,
    {userName} AS ApprovedBy,
    {submission.SysApprovalNote} AS ApprovalNote
FROM core.BudgetSubmissionLines b
WHERE b.SubmissionId = {submission.SubmissionId};");

            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = userName,
                Action = "UPDATE",
                EntityName = "BudgetSubmissions",
                RecordId = submission.SubmissionId.ToString(),
                Timestamp = DateTime.UtcNow,
                Details = $"System approved budget submission {submission.SubmissionId}."
            });
            await _db.SaveChangesAsync();

            await tx.CommitAsync();

            TempData["Success"] = "System approval completed and copied to final table.";
            return RedirectToAction(nameof(ReviewSubmission), new { id });
        }
    }
}
