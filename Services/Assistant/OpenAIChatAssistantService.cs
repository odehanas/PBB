using System;
using System.Collections.Generic;
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

        public async Task<AssistantAnswer> AskAsync(
            string question,
            IReadOnlyList<ChatTurn> history,
            AssistantUserContext user,
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
                    ["tool_choice"] = round == _options.MaxToolRounds ? "none" : "auto"
                };

                JsonElement message;
                try
                {
                    message = await PostAsync(client, payload, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Assistant completion failed.");
                    return new AssistantAnswer(false, "The assistant service could not be reached. Please try again.", toolsUsed);
                }

                var toolCalls = message.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array
                    ? tc.EnumerateArray().ToList()
                    : new List<JsonElement>();

                if (toolCalls.Count == 0)
                {
                    var content = message.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                    return new AssistantAnswer(true,
                        string.IsNullOrWhiteSpace(content) ? "I could not produce an answer for that question." : content,
                        toolsUsed);
                }

                var assistantMessage = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = message.TryGetProperty("content", out var mc) && mc.ValueKind == JsonValueKind.String
                        ? mc.GetString()
                        : null,
                    ["tool_calls"] = JsonNode.Parse(tc.GetRawText())
                };
                messages.Add(assistantMessage);

                foreach (var call in toolCalls)
                {
                    var id = call.GetProperty("id").GetString() ?? "";
                    var fn = call.GetProperty("function");
                    var name = fn.GetProperty("name").GetString() ?? "";
                    var argsJson = fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";

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

        private async Task<JsonElement> PostAsync(HttpClient client, JsonObject payload, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/chat/completions")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"OpenAI returned {(int)response.StatusCode}: {body}");
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").Clone();
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
            - Amounts are whole units of the reporting currency. Never label a column
              "in thousands" or "in millions", never rescale a figure, and keep the two
              decimals the tool returned. Show thousands separators and state the year.
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
