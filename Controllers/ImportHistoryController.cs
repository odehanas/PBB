using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GovBudget.Models;
using GovBudget.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Controllers
{
    // Admin > Upload History: every Excel upload that changed data, with undo for an upload
    // whose records have not been changed since. Gated by the Admin Room form
    // (FormPermissionFilter) and the AdminOnly policy; entity-scoped admins only see and undo
    // uploads for their own entity.
    [Authorize(Policy = "AdminOnly")]
    public class ImportHistoryController : Controller
    {
        private readonly GovBudgetContext _db;
        private readonly ImportHistoryService _history;

        public ImportHistoryController(GovBudgetContext db, ImportHistoryService history)
        {
            _db = db;
            _history = history;
        }

        private int? ScopedEntityId()
        {
            var claim = User.Claims.FirstOrDefault(c => c.Type == "EntityId")?.Value;
            return int.TryParse(claim, out var id) && id > 0 ? id : null;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? type = null, int? year = null, int page = 1)
        {
            const int pageSize = 50;
            var scoped = ScopedEntityId();

            IQueryable<ImportBatches> q = _db.ImportBatches.AsNoTracking();
            if (scoped.HasValue) q = q.Where(b => b.EntityId == scoped.Value);
            if (!string.IsNullOrWhiteSpace(type)) q = q.Where(b => b.ImportType == type);
            if (year.HasValue) q = q.Where(b => b.BudgetYear == year.Value);

            var total = await q.CountAsync();
            page = Math.Max(1, page);
            var batches = await q.OrderByDescending(b => b.ImportBatchId)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .ToListAsync();

            var entityIds = batches.Where(b => b.EntityId.HasValue).Select(b => b.EntityId!.Value).Distinct().ToList();
            var entityCodes = await _db.Entities.AsNoTracking()
                .Where(e => entityIds.Contains(e.EntityId))
                .ToDictionaryAsync(e => e.EntityId, e => e.EntityCode);

            // Undo is offered unless a later applied upload changed the same records (the same
            // rule the service enforces). The service re-checks everything, including on-screen
            // edits and locks, on click.
            var appliedIds = batches.Where(b => b.Status == "APPLIED").Select(b => b.ImportBatchId).ToList();
            var blockedByLater = (await (
                from c in _db.ImportBatchChanges.AsNoTracking()
                where appliedIds.Contains(c.ImportBatchId)
                join l in _db.ImportBatchChanges.AsNoTracking()
                    on new { c.TableName, c.RecordId } equals new { l.TableName, l.RecordId }
                join b in _db.ImportBatches.AsNoTracking() on l.ImportBatchId equals b.ImportBatchId
                where l.ImportBatchId > c.ImportBatchId && b.Status == "APPLIED"
                select c.ImportBatchId).Distinct().ToListAsync()).ToHashSet();

            var vm = new ImportHistoryVm
            {
                Type = type,
                Year = year,
                Page = page,
                PageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)),
                Rows = batches.Select(b => new ImportHistoryRowVm
                {
                    Batch = b,
                    EntityCode = b.EntityId.HasValue && entityCodes.TryGetValue(b.EntityId.Value, out var c) ? c : (b.EntityId.HasValue ? b.EntityId.ToString()! : "Several"),
                    CanOfferUndo = b.Status == "APPLIED" && !blockedByLater.Contains(b.ImportBatchId)
                }).ToList(),
                TypeOptions = new List<SelectListItem>
                {
                    new("All types", "", string.IsNullOrEmpty(type)),
                    new("Budget lines", ImportTypes.BudgetLines, type == ImportTypes.BudgetLines),
                    new("Mid-year actuals", ImportTypes.MidYear, type == ImportTypes.MidYear),
                    new("Historical actuals", ImportTypes.Historical, type == ImportTypes.Historical),
                    new("Items", ImportTypes.Items, type == ImportTypes.Items),
                }
            };
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Undo(long id, string? type = null, int? year = null)
        {
            var batch = await _db.ImportBatches.AsNoTracking().FirstOrDefaultAsync(b => b.ImportBatchId == id);
            if (batch == null) return NotFound();

            var scoped = ScopedEntityId();
            if (scoped.HasValue && batch.EntityId != scoped.Value)
            {
                return Forbid();
            }

            var result = await _history.UndoAsync(id, User.Identity?.Name ?? "Unknown");
            TempData[result.Ok ? "Success" : "Error"] = result.Message;
            return RedirectToAction(nameof(Index), new { type, year });
        }

        public class ImportHistoryVm
        {
            public string? Type { get; set; }
            public int? Year { get; set; }
            public int Page { get; set; }
            public int PageCount { get; set; }
            public List<ImportHistoryRowVm> Rows { get; set; } = new();
            public List<SelectListItem> TypeOptions { get; set; } = new();
        }

        public class ImportHistoryRowVm
        {
            public ImportBatches Batch { get; set; } = null!;
            public string EntityCode { get; set; } = "";
            public bool CanOfferUndo { get; set; }
        }
    }
}
