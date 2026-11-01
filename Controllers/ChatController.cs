using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace GovBudget.Controllers
{
    /// <summary>
    /// Backend for the in-app assistant. It answers questions about the signed-in user's budget
    /// and performance data and about OECD performance-budgeting practice, and prepares data
    /// entry as proposals. It never writes: the widget posts a confirmed proposal to the screen's
    /// own action and reports the outcome back through Outcome.
    /// The widget uses AskStream; Ask returns the same answer in one JSON response.
    /// </summary>
    [Authorize]
    public class ChatController : Controller
    {
        private const string HistorySessionKey = "assistantHistory";

        // camelCase, matching what Json(...) sends from the non-streaming endpoint.
        private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);

        private readonly IChatAssistantService _assistant;
        private readonly IPermissionService _permissions;
        private readonly AssistantProposalCollector _proposals;
        private readonly OpenAISpeechService _speech;
        private readonly ILogger<ChatController> _logger;

        public ChatController(
            IChatAssistantService assistant,
            IPermissionService permissions,
            AssistantProposalCollector proposals,
            OpenAISpeechService speech,
            ILogger<ChatController> logger)
        {
            _assistant = assistant;
            _permissions = permissions;
            _proposals = proposals;
            _speech = speech;
            _logger = logger;
        }

        public sealed class SpeakRequest
        {
            public string? Text { get; set; }
        }

        public sealed class AskRequest
        {
            public string? Message { get; set; }
        }

        public sealed class OutcomeRequest
        {
            public string? Title { get; set; }
            public bool Ok { get; set; }
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

            return Json(new { ok = answer.Success, reply = answer.Reply, tools = answer.ToolsUsed, proposals = _proposals.Proposals });
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

            await WriteEventAsync("done", new { ok = answer.Success, reply = answer.Reply, tools = answer.ToolsUsed, proposals = _proposals.Proposals }, ct);
        }

        /// <summary>
        /// Records what a screen answered after the user confirmed (or cancelled) a proposal, so
        /// the assistant knows on the next question whether the change went through.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Outcome([FromBody] OutcomeRequest request)
        {
            if (!await _permissions.CanViewAsync(User, AppForms.Assistant))
            {
                return Json(new { ok = false });
            }

            static string Clip(string? text, int max) =>
                string.IsNullOrWhiteSpace(text) ? "" : text.Length > max ? text[..max] : text.Trim();

            var history = ReadHistory();
            history.Add(new ChatTurn("assistant",
                $"[Confirmed change \"{Clip(request?.Title, 200)}\": {(request?.Ok == true ? "saved" : "not saved")} - {Clip(request?.Message, 500)}]"));
            WriteHistory(history);
            return Json(new { ok = true });
        }

        /// <summary>
        /// Turns a recording from the microphone button into text. The widget then sends that
        /// text through AskStream like a typed question.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("assistantVoice")]
        [RequestSizeLimit(10 * 1024 * 1024)]
        public async Task<IActionResult> Transcribe(IFormFile? audio, CancellationToken ct)
        {
            if (!await _permissions.CanViewAsync(User, AppForms.Assistant) || !_speech.IsConfigured)
            {
                return Json(new { ok = false, error = "Voice input is not available." });
            }

            if (audio is null || audio.Length == 0)
            {
                return Json(new { ok = false, error = "No audio was received. Please try again." });
            }

            try
            {
                await using var stream = audio.OpenReadStream();
                var text = await _speech.TranscribeAsync(stream, audio.FileName, audio.ContentType, ct);
                return text.Length == 0
                    ? Json(new { ok = false, error = "I could not hear anything. Please try again." })
                    : Json(new { ok = true, text = text.Length > 2000 ? text[..2000] : text });
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Speech-to-text failed");
                // Admins get OpenAI's reason (e.g. "403: missing scope api.model.audio.request")
                // so a key or model problem can be fixed without server logs.
                var role = BuildUserContext().Role;
                var reason = role is "ADMIN" or "SYSADMIN" ? $" (OpenAI {ex.Message})" : "";
                return Json(new { ok = false, error = "Voice input failed. Please type your question instead." + reason });
            }
        }

        /// <summary>Reads an answer aloud: returns it as MP3 audio.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("assistantVoice")]
        public async Task<IActionResult> Speak([FromBody] SpeakRequest request, CancellationToken ct)
        {
            if (!await _permissions.CanViewAsync(User, AppForms.Assistant) || !_speech.IsConfigured)
            {
                return NotFound();
            }

            var text = (request?.Text ?? "").Trim();
            if (text.Length == 0)
            {
                return BadRequest();
            }

            try
            {
                return File(await _speech.SynthesizeAsync(text, ct), "audio/mpeg");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Text-to-speech failed");
                return StatusCode(StatusCodes.Status502BadGateway);
            }
        }

        private async Task WriteEventAsync(string name, object data, CancellationToken ct)
        {
            await Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, EventJson)}\n\n", ct);
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
                year,
                HttpContext.Session.GetInt("ctxEntityId"),
                HttpContext.Session.GetInt("ctxDeptId"));
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
