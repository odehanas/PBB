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
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace GovBudget.Controllers
{
    /// <summary>
    /// Backend for the in-app assistant. Read-only: it answers questions about the signed-in
    /// user's budget and performance data and about OECD performance-budgeting practice.
    /// The widget uses AskStream; Ask returns the same answer in one JSON response.
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
            var (question, error) = await PrepareQuestionAsync(request);
            if (error is not null)
            {
                return Json(new { ok = false, reply = error });
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

        /// <summary>
        /// Streaming variant of <see cref="Ask"/>, answered as server-sent events:
        /// "tool" (a data lookup is running), "delta" (reply text to append), "reset"
        /// (discard text shown so far) and a final "done" carrying the full reply.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task AskStream([FromBody] AskRequest request, CancellationToken ct)
        {
            var (question, error) = await PrepareQuestionAsync(request);
            var history = ReadHistory();

            // The session cookie cannot be issued once the body has started, so the session is
            // established now; the history is written again when the answer is complete.
            if (error is null)
            {
                WriteHistory(history);
            }

            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers["X-Accel-Buffering"] = "no";
            HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            if (error is not null)
            {
                await WriteEventAsync("done", new { ok = false, reply = error }, ct);
                return;
            }

            await Response.Body.FlushAsync(ct);

            var answer = await _assistant.AskStreamAsync(question, history, BuildUserContext(), e => e.Kind switch
            {
                AssistantStreamEventKind.Tool => WriteEventAsync("tool", new { name = e.Text }, ct),
                AssistantStreamEventKind.Delta => WriteEventAsync("delta", new { text = e.Text }, ct),
                _ => WriteEventAsync("reset", new { }, ct)
            }, ct);

            if (answer.Success)
            {
                history.Add(new ChatTurn("user", question));
                history.Add(new ChatTurn("assistant", answer.Reply));
                WriteHistory(history);
            }

            await WriteEventAsync("done", new { ok = answer.Success, reply = answer.Reply, tools = answer.ToolsUsed }, ct);
        }

        private async Task WriteEventAsync(string name, object data, CancellationToken ct)
        {
            await Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data)}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        private async Task<(string Question, string? Error)> PrepareQuestionAsync(AskRequest? request)
        {
            if (!await _permissions.CanViewAsync(User, AppForms.Assistant))
            {
                return ("", "You do not have access to the assistant.");
            }

            var question = (request?.Message ?? "").Trim();
            if (question.Length == 0)
            {
                return ("", "Please type a question.");
            }

            return (question.Length > 2000 ? question[..2000] : question, null);
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
