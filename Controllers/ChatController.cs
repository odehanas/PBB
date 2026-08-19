using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GovBudget.Services;
using GovBudget.Services.Assistant;
using GovBudget.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GovBudget.Controllers
{
    /// <summary>
    /// Backend for the in-app assistant. Read-only: it answers questions about the signed-in
    /// user's budget and performance data and about OECD performance-budgeting practice.
    /// </summary>
    [Authorize]
    public class ChatController : Controller
    {
        private const string HistorySessionKey = "assistantHistory";

        private readonly IChatAssistantService _assistant;
        private readonly IPermissionService _permissions;

        public ChatController(IChatAssistantService assistant, IPermissionService permissions)
        {
            _assistant = assistant;
            _permissions = permissions;
        }

        public sealed class AskRequest
        {
            public string? Message { get; set; }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Ask([FromBody] AskRequest request, CancellationToken ct)
        {
            if (!await _permissions.CanViewAsync(User, AppForms.Assistant))
            {
                return Json(new { ok = false, reply = "You do not have access to the assistant." });
            }

            var question = (request?.Message ?? "").Trim();
            if (question.Length == 0)
            {
                return Json(new { ok = false, reply = "Please type a question." });
            }
            if (question.Length > 2000)
            {
                question = question[..2000];
            }

            var history = ReadHistory();
            var answer = await _assistant.AskAsync(question, history, BuildUserContext(), ct);

            if (answer.Success)
            {
                history.Add(new ChatTurn("user", question));
                history.Add(new ChatTurn("assistant", answer.Reply));
                WriteHistory(history);
            }

            return Json(new { ok = answer.Success, reply = answer.Reply, tools = answer.ToolsUsed });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Reset()
        {
            HttpContext.Session.Remove(HistorySessionKey);
            return Json(new { ok = true });
        }

        private AssistantUserContext BuildUserContext()
        {
            var role = (User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.Role)?.Value ?? "")
                .Trim().ToUpperInvariant();

            int? entityId = int.TryParse(User.Claims.FirstOrDefault(c => c.Type == "EntityId")?.Value, out var eid) && eid > 0
                ? eid : null;
            int? departmentId = int.TryParse(User.Claims.FirstOrDefault(c => c.Type == "DepartmentId")?.Value, out var did) && did > 0
                ? did : null;

            // Mirrors the screens: SYSADMIN, and ADMIN without an entity claim, see every entity.
            var isGlobalAdmin = role == "SYSADMIN" || (role == "ADMIN" && entityId is null);

            var year = HttpContext.Session.GetInt("ctxYear") ?? DateTime.Now.Year;

            return new AssistantUserContext(
                User.Identity?.Name ?? "unknown",
                role,
                isGlobalAdmin,
                entityId,
                departmentId,
                year);
        }

        private List<ChatTurn> ReadHistory()
        {
            var raw = HttpContext.Session.GetString(HistorySessionKey);
            if (string.IsNullOrWhiteSpace(raw)) return new List<ChatTurn>();
            try
            {
                return JsonSerializer.Deserialize<List<ChatTurn>>(raw) ?? new List<ChatTurn>();
            }
            catch (JsonException)
            {
                return new List<ChatTurn>();
            }
        }

        private void WriteHistory(List<ChatTurn> history)
        {
            // Session state is capped so a long conversation cannot grow the cookie/session store.
            var trimmed = history.TakeLast(20).ToList();
            HttpContext.Session.SetString(HistorySessionKey, JsonSerializer.Serialize(trimmed));
        }
    }
}
