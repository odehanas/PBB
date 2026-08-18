using System;
using System.Linq;
using System.Threading.Tasks;
using GovBudget.Models;
using GovBudget.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Controllers
{
    [Authorize]
    public class InternalMessagesController : Controller
    {
        private readonly GovBudgetContext _db;

        public InternalMessagesController(GovBudgetContext db)
        {
            _db = db;
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

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var userName = User.Identity?.Name ?? "Unknown";

            var dep = await GetContextDepartment();
            if (dep != null)
            {
                var year = HttpContext.Session.GetInt("ctxYear") ?? DateTime.Now.Year;
                ViewBag.ContextLabel = $"{year} — {dep.Entity.EntityCode}/{dep.DeptCode} {dep.DeptName}";
                ViewBag.ContextEntityCode = dep.Entity.EntityCode;
                ViewBag.ContextDeptCode = dep.DeptCode;
            }

            ViewBag.MyMessages = await _db.InternalMessages.AsNoTracking()
                .Where(m => m.FromUser == userName)
                .OrderByDescending(m => m.CreatedAt)
                .Take(50)
                .ToListAsync();

            return View(new InternalMessages
            {
                FromUser = userName,
                FromEntityCode = dep?.Entity.EntityCode,
                FromDeptCode = dep?.DeptCode,
                Status = "Pending"
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Index(InternalMessages model)
        {
            var userName = User.Identity?.Name ?? "Unknown";

            var dep = await GetContextDepartment();
            var entityCode = dep?.Entity.EntityCode;
            var deptCode = dep?.DeptCode;

            model.FromUser = userName;
            model.FromEntityCode = entityCode;
            model.FromDeptCode = deptCode;
            model.Status = "Pending";
            model.CreatedAt = DateTime.UtcNow;

            ModelState.Remove(nameof(model.MessageId));
            ModelState.Remove(nameof(model.FromUser));
            ModelState.Remove(nameof(model.FromEntityCode));
            ModelState.Remove(nameof(model.FromDeptCode));
            ModelState.Remove(nameof(model.Status));
            ModelState.Remove(nameof(model.CreatedAt));
            ModelState.Remove(nameof(model.ReadAt));
            ModelState.Remove(nameof(model.ReadBy));
            ModelState.Remove(nameof(model.AdminResponse));
            ModelState.Remove(nameof(model.RespondedAt));
            ModelState.Remove(nameof(model.RespondedBy));

            if (string.IsNullOrWhiteSpace(model.Subject))
            {
                ModelState.AddModelError(nameof(model.Subject), "Subject is required.");
            }

            if (string.IsNullOrWhiteSpace(model.Body))
            {
                ModelState.AddModelError(nameof(model.Body), "Message is required.");
            }

            if (!ModelState.IsValid)
            {
                if (dep != null)
                {
                    var year = HttpContext.Session.GetInt("ctxYear") ?? DateTime.Now.Year;
                    ViewBag.ContextLabel = $"{year} — {dep.Entity.EntityCode}/{dep.DeptCode} {dep.DeptName}";
                    ViewBag.ContextEntityCode = dep.Entity.EntityCode;
                    ViewBag.ContextDeptCode = dep.DeptCode;
                }

                ViewBag.MyMessages = await _db.InternalMessages.AsNoTracking()
                    .Where(m => m.FromUser == userName)
                    .OrderByDescending(m => m.CreatedAt)
                    .Take(50)
                    .ToListAsync();

                return View(model);
            }

            _db.InternalMessages.Add(model);
            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = userName,
                Action = "INSERT",
                EntityName = "InternalMessages",
                RecordId = "",
                Timestamp = DateTime.UtcNow,
                Details = $"Sent internal message. Subject: {model.Subject}"
            });

            await _db.SaveChangesAsync();

            TempData["Success"] = "Message sent to Admin.";
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        [Authorize(Policy = "AdminOnly")]
        public async Task<IActionResult> AdminInbox(string? status = null)
        {
            var adminEntityId = GetAdminScopedEntityId();
            string? allowedEntityCode = null;
            if (adminEntityId.HasValue)
            {
                allowedEntityCode = await _db.Entities
                    .AsNoTracking()
                    .Where(e => e.EntityId == adminEntityId.Value)
                    .Select(e => e.EntityCode)
                    .FirstOrDefaultAsync();

                if (string.IsNullOrWhiteSpace(allowedEntityCode))
                {
                    return Forbid();
                }
            }

            var query = _db.InternalMessages.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(m => m.Status == status);
            }

            if (!string.IsNullOrWhiteSpace(allowedEntityCode))
            {
                query = query.Where(m => m.FromEntityCode == allowedEntityCode);
            }

            var rows = await query
                .OrderBy(m => m.ReadAt == null ? 0 : 1)
                .ThenByDescending(m => m.CreatedAt)
                .Take(200)
                .ToListAsync();

            ViewBag.FilterStatus = status ?? "";
            return View(rows);
        }

        [HttpGet]
        [Authorize(Policy = "AdminOnly")]
        public async Task<IActionResult> Review(long id)
        {
            var adminEntityId = GetAdminScopedEntityId();
            var msg = await _db.InternalMessages.FirstOrDefaultAsync(m => m.MessageId == id);
            if (msg == null) return NotFound();

            if (adminEntityId.HasValue)
            {
                var allowedEntityCode = await _db.Entities
                    .AsNoTracking()
                    .Where(e => e.EntityId == adminEntityId.Value)
                    .Select(e => e.EntityCode)
                    .FirstOrDefaultAsync();

                if (string.IsNullOrWhiteSpace(allowedEntityCode) || msg.FromEntityCode != allowedEntityCode)
                {
                    return Forbid();
                }
            }

            if (msg.ReadAt == null)
            {
                msg.ReadAt = DateTime.UtcNow;
                msg.ReadBy = User.Identity?.Name ?? "Unknown";
                await _db.SaveChangesAsync();
            }

            return View(msg);
        }

        [HttpPost]
        [Authorize(Policy = "AdminOnly")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Respond(long id, string adminResponse)
        {
            var adminEntityId = GetAdminScopedEntityId();
            var msg = await _db.InternalMessages.FirstOrDefaultAsync(m => m.MessageId == id);
            if (msg == null) return NotFound();

            if (adminEntityId.HasValue)
            {
                var allowedEntityCode = await _db.Entities
                    .AsNoTracking()
                    .Where(e => e.EntityId == adminEntityId.Value)
                    .Select(e => e.EntityCode)
                    .FirstOrDefaultAsync();

                if (string.IsNullOrWhiteSpace(allowedEntityCode) || msg.FromEntityCode != allowedEntityCode)
                {
                    return Forbid();
                }
            }

            if (string.IsNullOrWhiteSpace(adminResponse))
            {
                TempData["Error"] = "Response is required.";
                return RedirectToAction(nameof(Review), new { id });
            }

            msg.AdminResponse = adminResponse.Trim();
            msg.Status = "Responded";
            msg.RespondedAt = DateTime.UtcNow;
            msg.RespondedBy = User.Identity?.Name ?? "Unknown";

            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = msg.RespondedBy,
                Action = "UPDATE",
                EntityName = "InternalMessages",
                RecordId = id.ToString(),
                Timestamp = DateTime.UtcNow,
                Details = $"Responded to internal message. Subject: {msg.Subject}"
            });

            await _db.SaveChangesAsync();

            TempData["Success"] = "Response sent.";
            return RedirectToAction(nameof(AdminInbox));
        }

        private async Task<Departments?> GetContextDepartment()
        {
            var deptId = HttpContext.Session.GetInt("ctxDeptId");
            if (!deptId.HasValue || deptId.Value <= 0) return null;

            return await _db.Departments
                .Include(d => d.Entity)
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.DepartmentId == deptId.Value);
        }
    }
}
