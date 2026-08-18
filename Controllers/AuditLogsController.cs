using System;
using System.Linq;
using System.Threading.Tasks;
using GovBudget.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Controllers
{
    [Authorize(Policy = "AdminOnly")]
    public class AuditLogsController : Controller
    {
        private readonly GovBudgetContext _context;

        public AuditLogsController(GovBudgetContext context)
        {
            _context = context;
        }

        private bool IsGlobalAdmin()
        {
            var entityClaim = User.Claims.FirstOrDefault(c => c.Type == "EntityId")?.Value;
            var hasEntityScope = int.TryParse(entityClaim, out var entityId) && entityId > 0;
            return User.IsInRole("SYSADMIN") || (User.IsInRole("ADMIN") && !hasEntityScope);
        }

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            if (!IsGlobalAdmin())
            {
                context.Result = Forbid();
                return;
            }

            base.OnActionExecuting(context);
        }

        public async Task<IActionResult> Index()
        {
            var logs = await _context.AuditLogs
                .OrderByDescending(a => a.Timestamp)
                .Take(500) // Limit to last 500 for performance
                .ToListAsync();

            return View(logs);
        }
    }
}
