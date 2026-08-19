using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GovBudget.Services.Assistant
{
    /// <summary>One stored conversation turn. Role is "user" or "assistant".</summary>
    public sealed record ChatTurn(string Role, string Content);

    /// <summary>
    /// Who is asking. Every data tool filters on this, so a user can never read
    /// beyond the entity and cost center their login is scoped to.
    /// </summary>
    public sealed record AssistantUserContext(
        string UserName,
        string Role,
        bool IsGlobalAdmin,
        int? EntityId,
        int? DepartmentId,
        int DefaultYear);

    /// <summary>A callable tool exposed to the model.</summary>
    public sealed record AssistantToolDefinition(
        string Name,
        string Description,
        string ParametersJson,
        Func<JsonElement, AssistantUserContext, CancellationToken, Task<string>> InvokeAsync);

    public interface IAssistantToolProvider
    {
        IEnumerable<AssistantToolDefinition> GetTools();
    }

    public sealed record AssistantAnswer(bool Success, string Reply, IReadOnlyList<string> ToolsUsed);

    public interface IChatAssistantService
    {
        bool IsConfigured { get; }

        Task<AssistantAnswer> AskAsync(
            string question,
            IReadOnlyList<ChatTurn> history,
            AssistantUserContext user,
            CancellationToken ct);
    }
}
