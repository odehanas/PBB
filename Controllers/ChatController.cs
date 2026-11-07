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
using Microsoft.Extensions.Caching.Memory;
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

        // Live voice session state, kept in the user's own server session.
        private const string RealtimeStartedKey = "assistantRealtimeStarted";
        private const string RealtimeToolCallsKey = "assistantRealtimeToolCalls";

        private readonly IChatAssistantService _assistant;
        private readonly IPermissionService _permissions;
        private readonly AssistantProposalCollector _proposals;
        private readonly OpenAISpeechService _speech;
        private readonly OpenAIRealtimeService _realtime;
        private readonly IEnumerable<IAssistantToolProvider> _toolProviders;
        private readonly IMemoryCache _cache;
        private readonly AssistantAttachmentStore _attachments;
        private readonly ILogger<ChatController> _logger;

        public ChatController(
            IChatAssistantService assistant,
            IPermissionService permissions,
            AssistantProposalCollector proposals,
            OpenAISpeechService speech,
            OpenAIRealtimeService realtime,
            IEnumerable<IAssistantToolProvider> toolProviders,
            IMemoryCache cache,
            AssistantAttachmentStore attachments,
            ILogger<ChatController> logger)
        {
            _assistant = assistant;
            _permissions = permissions;
            _proposals = proposals;
            _speech = speech;
            _realtime = realtime;
            _toolProviders = toolProviders;
            _cache = cache;
            _attachments = attachments;
            _logger = logger;
        }

        public sealed class RealtimeSessionRequest
        {
            public string? Screen { get; set; }
        }

        public sealed class RealtimeToolRequest
        {
            public string? Name { get; set; }
            public string? Arguments { get; set; }
        }

        public sealed class RealtimeLogRequest
        {
            public string? Role { get; set; }
            public string? Text { get; set; }
        }

        public sealed class SpeakRequest
        {
            public string? Text { get; set; }
        }

        public sealed class AskRequest
        {
            public string? Message { get; set; }

            /// <summary>A file attached in the panel for this question (from Attach).</summary>
            public string? AttachmentId { get; set; }
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

            return Json(new { ok = answer.Success, reply = answer.Reply, tools = answer.ToolsUsed, proposals = _proposals.Proposals, openScreen = _proposals.OpenScreen, download = _proposals.Download });
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

            await WriteEventAsync("done", new { ok = answer.Success, reply = answer.Reply, tools = answer.ToolsUsed, proposals = _proposals.Proposals, openScreen = _proposals.OpenScreen, download = _proposals.Download }, ct);
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

        // ---- Files attached in the panel ----

        /// <summary>
        /// Reads a workbook the user attached and keeps only its summary (layout, counts, totals,
        /// entity and cost-center names). The file itself stays in the browser: an import happens
        /// only when the user confirms the card and the browser posts it to the import screen.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("assistantVoice")]
        [RequestSizeLimit(MaxAttachmentBytes + 64 * 1024)]
        public async Task<IActionResult> Attach(IFormFile? file)
        {
            if (!await _permissions.CanViewAsync(User, AppForms.Assistant))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { ok = false, error = "You do not have access to the assistant." });
            }
            if (file is null || file.Length == 0)
            {
                return Json(new { ok = false, error = "No file was received." });
            }
            if (!string.Equals(System.IO.Path.GetExtension(file.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                return Json(new { ok = false, error = "Only Excel .xlsx files can be attached." });
            }
            if (file.Length > MaxAttachmentBytes)
            {
                return Json(new { ok = false, error = "The file is larger than 10 MB." });
            }

            SpreadsheetInspector.Result inspected;
            try
            {
                await using var stream = file.OpenReadStream();
                inspected = SpreadsheetInspector.Inspect(stream);
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Assistant could not read an attached workbook.");
                return Json(new { ok = false, error = "The file could not be read as an Excel workbook." });
            }

            var name = System.IO.Path.GetFileName(file.FileName);
            if (name.Length > 120) name = name[..120];
            var attachment = _attachments.Add(User.Identity?.Name ?? "", name, file.Length, inspected);
            return Json(new { ok = true, id = attachment.Id, fileName = attachment.FileName, layout = attachment.Layout, note = attachment.Note });
        }

        private const long MaxAttachmentBytes = 10 * 1024 * 1024;

        // ---- Live voice (OpenAI Realtime) ----

        /// <summary>
        /// Starts a live voice conversation: returns a short-lived key the browser uses to connect
        /// to OpenAI directly. The real API key stays here.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("assistantVoice")]
        public async Task<IActionResult> RealtimeSession([FromBody] RealtimeSessionRequest? request, CancellationToken ct)
        {
            if (!await _permissions.CanViewAsync(User, AppForms.Assistant) || !_realtime.IsConfigured)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Live voice is not available." });
            }

            var user = BuildUserContext();
            var options = _realtime.Options;

            // Each live minute is billed, so every user gets a daily allowance.
            var countKey = $"assistantRealtimeCount:{user.UserName}:{DateTime.UtcNow:yyyyMMdd}";
            var started = _cache.GetOrCreate(countKey, e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1);
                return 0;
            });
            if (started >= options.RealtimeDailySessionsPerUser)
            {
                return StatusCode(StatusCodes.Status429TooManyRequests,
                    new { error = $"You have used today's {options.RealtimeDailySessionsPerUser} live voice conversations. You can still type your questions." });
            }

            try
            {
                var session = await _realtime.StartAsync(user, ReadHistory(), Clip(request?.Screen, 100), ct);

                _cache.Set(countKey, started + 1, TimeSpan.FromDays(1));
                HttpContext.Session.SetString(RealtimeStartedKey, DateTime.UtcNow.Ticks.ToString());
                HttpContext.Session.SetInt(RealtimeToolCallsKey, 0);

                return Json(new
                {
                    clientSecret = session.ClientSecret,
                    model = session.Model,
                    expiresAt = session.ExpiresAt,
                    maxMinutes = options.RealtimeMaxSessionMinutes
                });
            }
            catch (OpenAIRealtimeService.RealtimeException ex)
            {
                var reason = user.Role is "ADMIN" or "SYSADMIN" && ex.Detail is not null ? $" (OpenAI {ex.Detail})" : "";
                return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message + reason });
            }
        }

        /// <summary>
        /// Runs a tool the live model asked for, with the caller's own entity and cost-center
        /// scope worked out here from the sign-in. Nothing about identity or scope is taken from
        /// the browser or the model. propose_change still only prepares a card to confirm.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RealtimeTool([FromBody] RealtimeToolRequest? request, CancellationToken ct)
        {
            if (!await _permissions.CanViewAsync(User, AppForms.Assistant) || !_realtime.IsConfigured)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Live voice is not available." });
            }

            var options = _realtime.Options;
            if (!long.TryParse(HttpContext.Session.GetString(RealtimeStartedKey), out var startedTicks)
                || new DateTime(startedTicks, DateTimeKind.Utc).AddMinutes(options.RealtimeMaxSessionMinutes + 1) < DateTime.UtcNow)
            {
                return Json(new { output = JsonSerializer.Serialize(new { error = "This voice session has ended. Ask the user to start a new one." }) });
            }

            var calls = (HttpContext.Session.GetInt(RealtimeToolCallsKey) ?? 0) + 1;
            HttpContext.Session.SetInt(RealtimeToolCallsKey, calls);
            if (calls > options.RealtimeMaxToolCalls)
            {
                return Json(new { output = JsonSerializer.Serialize(new { error = "This voice session has reached its limit of lookups. Ask the user to start a new one." }) });
            }

            var name = (request?.Name ?? "").Trim();
            var tool = _toolProviders.SelectMany(p => p.GetTools())
                .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

            string output;
            if (tool is null)
            {
                output = JsonSerializer.Serialize(new { error = $"Unknown tool '{Clip(name, 100)}'." });
            }
            else
            {
                try
                {
                    using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(request?.Arguments) ? "{}" : request!.Arguments!);
                    output = await tool.InvokeAsync(args.RootElement, BuildUserContext(), ct);
                }
                catch (JsonException)
                {
                    output = JsonSerializer.Serialize(new { error = "The arguments were not valid JSON." });
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Live assistant tool {Tool} failed.", name);
                    output = JsonSerializer.Serialize(new { error = "The tool failed to run." });
                }
            }

            if (output.Length > 12000)
            {
                output = output[..12000] + "…(cut short; ask for fewer rows)";
            }

            return Json(new { output, proposals = _proposals.Proposals, openScreen = _proposals.OpenScreen, download = _proposals.Download });
        }

        /// <summary>
        /// Keeps what was said in the live conversation in the chat history, so typed questions
        /// afterwards (and the next live session) carry on from it.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RealtimeLog([FromBody] RealtimeLogRequest? request)
        {
            var text = Clip(request?.Text, 2000);
            if (!await _permissions.CanViewAsync(User, AppForms.Assistant) || string.IsNullOrEmpty(text))
            {
                return Json(new { ok = false });
            }

            var history = ReadHistory();
            history.Add(new ChatTurn(request?.Role == "assistant" ? "assistant" : "user", text));
            WriteHistory(history);
            return Json(new { ok = true });
        }

        private static string? Clip(string? text, int max)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            return text.Length > max ? text[..max] : text;
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

            question = question.Length > 2000 ? question[..2000] : question;

            // The attachment's summary goes into the question, so it also stays in the history.
            var attachment = _attachments.Get(User.Identity?.Name ?? "", request?.AttachmentId);
            if (attachment is not null)
            {
                question += "\n\n" + attachment.Note;
            }

            return (question, null);
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
