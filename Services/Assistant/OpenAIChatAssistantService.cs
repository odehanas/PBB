using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GovBudget.Services.Assistant
{
    /// <summary>
    /// Answers user questions with OpenAI chat completions. The model never touches the
    /// database directly: it may only call the registered tools, which apply the caller's
    /// entity and cost-center scope.
    /// </summary>
    public sealed class OpenAIChatAssistantService : IChatAssistantService
    {
        public const string HttpClientName = "openai";

        private readonly AssistantOptions _options;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IEnumerable<IAssistantToolProvider> _toolProviders;
        private readonly ILogger<OpenAIChatAssistantService> _logger;

        public OpenAIChatAssistantService(
            IOptions<AssistantOptions> options,
            IHttpClientFactory httpClientFactory,
            IEnumerable<IAssistantToolProvider> toolProviders,
            ILogger<OpenAIChatAssistantService> logger)
        {
            _options = options.Value;
            _httpClientFactory = httpClientFactory;
            _toolProviders = toolProviders;
            _logger = logger;
        }

        public bool IsConfigured => _options.IsConfigured;

        public Task<AssistantAnswer> AskAsync(
            string question,
            IReadOnlyList<ChatTurn> history,
            AssistantUserContext user,
            CancellationToken ct)
            => AskStreamAsync(question, history, user, _ => Task.CompletedTask, ct);

        public async Task<AssistantAnswer> AskStreamAsync(
            string question,
            IReadOnlyList<ChatTurn> history,
            AssistantUserContext user,
            Func<AssistantStreamEvent, Task> onEvent,
            CancellationToken ct)
        {
            if (!IsConfigured)
            {
                return new AssistantAnswer(false,
                    "The assistant is not configured yet. An administrator must set the OpenAI API key (Assistant:ApiKey or the OPENAI_API_KEY environment variable).",
                    Array.Empty<string>());
            }

            var tools = _toolProviders.SelectMany(p => p.GetTools()).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
            var toolsUsed = new List<string>();
            var hrExcluded = false;

            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt(user) }
            };

            foreach (var turn in history.TakeLast(_options.MaxHistoryMessages))
            {
                messages.Add(new JsonObject { ["role"] = turn.Role, ["content"] = turn.Content });
            }
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = question });

            var toolSchema = new JsonArray();
            foreach (var tool in tools.Values)
            {
                toolSchema.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonNode.Parse(tool.ParametersJson)
                    }
                });
            }

            var client = _httpClientFactory.CreateClient(HttpClientName);

            for (var round = 0; round <= _options.MaxToolRounds; round++)
            {
                var payload = new JsonObject
                {
                    ["model"] = _options.Model,
                    ["messages"] = messages.DeepClone(),
                    ["tools"] = toolSchema.DeepClone(),
                    ["tool_choice"] = round == _options.MaxToolRounds ? "none" : "auto",
                    ["stream"] = true
                };

                StreamedMessage message;
                try
                {
                    message = await PostStreamingAsync(client, payload, onEvent, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Assistant completion failed.");
                    return new AssistantAnswer(false, "The assistant service could not be reached. Please try again.", toolsUsed);
                }

                if (message.ToolCalls.Count == 0)
                {
                    if (string.IsNullOrWhiteSpace(message.Content))
                    {
                        return new AssistantAnswer(true, "I could not produce an answer for that question.", toolsUsed);
                    }

                    return new AssistantAnswer(true, WithHrCaveat(message.Content, hrExcluded), toolsUsed);
                }

                if (message.Content.Length > 0)
                {
                    await onEvent(new AssistantStreamEvent(AssistantStreamEventKind.Reset, ""));
                }

                var toolCallsJson = new JsonArray();
                foreach (var call in message.ToolCalls)
                {
                    toolCallsJson.Add(new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments }
                    });
                }

                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = message.Content.Length > 0 ? message.Content : null,
                    ["tool_calls"] = toolCallsJson
                });

                foreach (var call in message.ToolCalls)
                {
                    var id = call.Id;
                    var name = call.Name;
                    var argsJson = call.Arguments;

                    await onEvent(new AssistantStreamEvent(AssistantStreamEventKind.Tool, name));

                    string result;
                    if (!tools.TryGetValue(name, out var tool))
                    {
                        result = JsonSerializer.Serialize(new { error = $"Unknown tool '{name}'." });
                    }
                    else
                    {
                        try
                        {
                            using var argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
                            result = await tool.InvokeAsync(argsDoc.RootElement, user, ct);
                            toolsUsed.Add(name);
                            hrExcluded |= ExcludesHrStaffCost(result);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Assistant tool {Tool} failed.", name);
                            result = JsonSerializer.Serialize(new { error = "The tool failed to run." });
                        }
                    }

                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = id,
                        ["name"] = name,
                        ["content"] = result
                    });
                }
            }

            return new AssistantAnswer(false, "I could not finish that request. Please narrow the question and try again.", toolsUsed);
        }

        /// <summary>
        /// The model is asked to flag figures that leave staff cost out, but the caveat matters too
        /// much to depend on it, so it is appended here when a tool reported the exclusion.
        /// </summary>
        private static string WithHrCaveat(string reply, bool hrExcluded)
        {
            if (!hrExcluded || reply.Contains("HR", StringComparison.OrdinalIgnoreCase)
                || reply.Contains("staff cost", StringComparison.OrdinalIgnoreCase)
                || reply.Contains("تكلفة الموظفين"))
            {
                return reply;
            }

            return reply.TrimEnd()
                + "\n\n**Note:** these figures come from the budget lines and exclude HR staff cost, "
                + "which is held separately. Ask for the budget by category to see it included.";
        }

        private static bool ExcludesHrStaffCost(string toolResult)
        {
            try
            {
                using var doc = JsonDocument.Parse(toolResult);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("excludes_hr_staff_cost", out var flag)
                    && flag.ValueKind == JsonValueKind.True;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private sealed record StreamedToolCall(string Id, string Name, string Arguments);

        private sealed record StreamedMessage(string Content, IReadOnlyList<StreamedToolCall> ToolCalls);

        private sealed class ToolCallBuilder
        {
            public string Id = "";
            public string Name = "";
            public readonly StringBuilder Arguments = new();
        }

        /// <summary>
        /// Posts a streaming chat completion and reassembles the server-sent chunks. Reply text
        /// is forwarded as it arrives; tool calls arrive in fragments keyed by index and are
        /// only usable once the stream ends.
        /// </summary>
        private async Task<StreamedMessage> PostStreamingAsync(
            HttpClient client,
            JsonObject payload,
            Func<AssistantStreamEvent, Task> onEvent,
            CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/chat/completions")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"OpenAI returned {(int)response.StatusCode}: {body}");
            }

            var content = new StringBuilder();
            var calls = new SortedDictionary<int, ToolCallBuilder>();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                if (data.Length == 0) continue;

                using var doc = JsonDocument.Parse(data);
                if (!doc.RootElement.TryGetProperty("choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0
                    || !choices[0].TryGetProperty("delta", out var delta))
                {
                    continue;
                }

                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    var text = c.GetString() ?? "";
                    if (text.Length > 0)
                    {
                        content.Append(text);
                        await onEvent(new AssistantStreamEvent(AssistantStreamEventKind.Delta, text));
                    }
                }

                if (delta.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in tc.EnumerateArray())
                    {
                        var index = part.TryGetProperty("index", out var ix) ? ix.GetInt32() : 0;
                        if (!calls.TryGetValue(index, out var builder))
                        {
                            calls[index] = builder = new ToolCallBuilder();
                        }

                        if (part.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        {
                            builder.Id = id.GetString() ?? "";
                        }
                        if (part.TryGetProperty("function", out var fn))
                        {
                            if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                            {
                                builder.Name += n.GetString();
                            }
                            if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String)
                            {
                                builder.Arguments.Append(a.GetString());
                            }
                        }
                    }
                }
            }

            var toolCalls = calls.Values
                .Select(b => new StreamedToolCall(b.Id, b.Name, b.Arguments.Length > 0 ? b.Arguments.ToString() : "{}"))
                .ToList();

            return new StreamedMessage(content.ToString(), toolCalls);
        }

        private static string SystemPrompt(AssistantUserContext user) => $"""
            You are the GovBudget assistant inside a performance-based budgeting (PBB) application
            used by a government department of finance.

            You help with two kinds of questions:
            1. The user's own data in this application - budgets, actuals, variances, programs,
               activities, KPIs, outputs and PBB maturity. Answer these only from the data tools;
               never guess a figure.
            2. Performance-based budgeting method and OECD material - use search_pbb_reference
               first, then the live OECD tools when current statistics or a specific OECD page
               is needed.

            Rules:
            - The tools already restrict data to what this user may see. If a tool returns no
              rows, say so plainly instead of inventing numbers.
            - Cite the source of external material (OECD page or dataset) in your answer.
            - Copy every figure digit for digit from the tool result, with its unit or
              currency. Never re-type from memory, never round, and never derive a ratio,
              share or percentage that the tool did not return - if the user wants one that
              is not in the data, say it is not available.
            - Amounts are whole units of the reporting currency. Never label a column
              "in thousands" or "in millions", never rescale a figure, and keep the two
              decimals the tool returned. Show thousands separators and state the year.
            - When a tool returns totals, always show them under the table.
            - Staff cost is held separately from the budget lines. When a tool reports
              excludes_hr_staff_cost, say that the figure excludes HR, or call
              get_budget_summary grouped by category, which includes it.
            - Be concise: short paragraphs or small markdown tables. Every table row must
              have exactly as many cells as the header, including empty ones; never drop a
              cell or shift values into another column.
            - Reply in the language of the question (Arabic or English).
            - You are read-only. If the user wants to change data, point them to the relevant
              screen instead.

            Current user: {user.UserName} (role {user.Role}).
            Working budget year: {user.DefaultYear}.
            Today: {DateTime.UtcNow:yyyy-MM-dd}.
            """;
    }
}
