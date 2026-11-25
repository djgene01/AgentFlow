using System.Text.Json;

namespace AgentFlow.Core.Agents;

/// <summary>Claude Code asking permission for one tool call, as received by the permission prompt tool.</summary>
public sealed record ApprovalRequest(string ToolName, JsonElement Input, string? ToolUseId, string RawArguments);

public enum ApprovalChoice { AllowOnce, AlwaysAllow, Deny }

/// <summary>What happens to a permission prompt (a tool call that no rule or permission mode allows).</summary>
public enum ApprovalPolicy
{
    /// <summary>Show a card in the chat and wait for your answer.</summary>
    Ask,
    /// <summary>Deny it straight away.</summary>
    Deny,
    /// <summary>
    /// Unattended workflows: allow it straight away during a workflow run and record it in the chat;
    /// direct chat still asks. Deny rules, read-only presets and plan mode still apply, because Claude
    /// Code checks them before it ever asks.
    /// </summary>
    AllowInWorkflows,
    /// <summary>
    /// As <see cref="AllowInWorkflows"/>, and in direct chat too. Still not bypassPermissions: deny rules,
    /// read-only presets and plan mode are applied by Claude Code first, and tools that cost money still ask.
    /// </summary>
    AllowEverywhere,
}

/// <summary>Your answer. Message is shown to Claude on a deny.</summary>
public sealed record ApprovalAnswer(ApprovalChoice Choice, string? Message = null);

/// <summary>
/// Where a turn's permission prompts go. The runner passes it to the CLI as an MCP server plus
/// --permission-prompt-tool; the handler decides each request (usually by asking you in the chat).
/// </summary>
public interface IApprovalChannel
{
    /// <summary>Registers a turn and returns what the CLI needs to reach the handler. Dispose when the turn ends.</summary>
    ApprovalRegistration Register(Func<ApprovalRequest, CancellationToken, Task<ApprovalAnswer>> handler);
}

public sealed class ApprovalRegistration(string mcpConfigJson, string promptToolName, Action onDispose) : IDisposable
{
    /// <summary>The --mcp-config JSON that points the CLI at the app's permission server for this turn.</summary>
    public string McpConfigJson { get; } = mcpConfigJson;

    /// <summary>The --permission-prompt-tool value, mcp__&lt;server&gt;__&lt;tool&gt;.</summary>
    public string PromptToolName { get; } = promptToolName;

    public void Dispose() => onDispose();
}
