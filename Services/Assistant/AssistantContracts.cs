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

    public enum AssistantStreamEventKind
    {
        /// <summary>A tool is about to run. Text is the tool name.</summary>
        Tool,

        /// <summary>A fragment of the reply text, to append to what was already shown.</summary>
        Delta,

        /// <summary>Text streamed so far was a preamble to tool calls; discard it.</summary>
        Reset
    }

    public sealed record AssistantStreamEvent(AssistantStreamEventKind Kind, string Text);

    public interface IChatAssistantService
    {
        bool IsConfigured { get; }

        Task<AssistantAnswer> AskAsync(
            string question,
            IReadOnlyList<ChatTurn> history,
            AssistantUserContext user,
            CancellationToken ct);

        /// <summary>
        /// Same as <see cref="AskAsync"/>, but reports progress and reply text through
        /// <paramref name="onEvent"/> as it arrives. The returned answer is authoritative:
        /// it may add text (such as the HR caveat) that was never streamed.
        /// </summary>
        Task<AssistantAnswer> AskStreamAsync(
            string question,
            IReadOnlyList<ChatTurn> history,
            AssistantUserContext user,
            Func<AssistantStreamEvent, Task> onEvent,
            CancellationToken ct);
    }
}
