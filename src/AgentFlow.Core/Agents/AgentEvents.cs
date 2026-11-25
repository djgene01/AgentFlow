using System.Text.Json;

namespace AgentFlow.Core.Agents;

/// <summary>
/// One normalised event from an agent turn. Every event keeps the raw line it came from
/// so the chat can be re-rendered after a parser fix.
/// </summary>
public abstract record AgentEvent(string? RawJson);

/// <summary>system/init: the session has started.</summary>
public sealed record SessionStarted(
    string SessionId,
    string? Model,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Capabilities,
    string? PermissionMode,
    string? RawJson) : AgentEvent(RawJson);

/// <summary>stream_event with a text_delta: text as it is generated.</summary>
public sealed record TextDelta(string Text, string? ParentToolUseId, string? RawJson) : AgentEvent(RawJson);

/// <summary>stream_event with a thinking_delta.</summary>
public sealed record ThinkingDelta(string Text, string? ParentToolUseId, string? RawJson) : AgentEvent(RawJson);

/// <summary>A complete assistant message.</summary>
public sealed record AssistantMessage(
    string? MessageId,
    IReadOnlyList<ContentBlock> Content,
    string? ParentToolUseId,
    string? RawJson) : AgentEvent(RawJson)
{
    public string Text => string.Concat(Content.OfType<TextBlock>().Select(b => b.Text));
    public IEnumerable<ToolUseBlock> ToolUses => Content.OfType<ToolUseBlock>();
}

/// <summary>A user message: tool results, or a prompt that drives a subagent.</summary>
public sealed record UserMessage(
    IReadOnlyList<ContentBlock> Content,
    string? ParentToolUseId,
    string? RawJson) : AgentEvent(RawJson)
{
    public IEnumerable<ToolResultBlock> ToolResults => Content.OfType<ToolResultBlock>();
}

/// <summary>system/api_retry.</summary>
public sealed record ApiRetry(int Attempt, int MaxRetries, int RetryDelayMs, int? ErrorStatus, string? Error, string? RawJson)
    : AgentEvent(RawJson);

/// <summary>system/permission_denied.</summary>
public sealed record PermissionDenied(string? ToolName, string? RawJson) : AgentEvent(RawJson);

/// <summary>
/// rate_limit_event: the plan's usage window status. Utilization is 0..1 per window
/// (for example five_hour, seven_day). Shown in the run panel so a long workflow can warn early.
/// </summary>
public sealed record RateLimitStatus(
    string? Status,
    string? RateLimitType,
    DateTimeOffset? ResetsAt,
    IReadOnlyDictionary<string, double> Utilization,
    string? RawJson) : AgentEvent(RawJson);

/// <summary>Any other system subtype (status, post_turn_summary, commands_changed, ...).</summary>
public sealed record SystemNotice(string Subtype, string? RawJson) : AgentEvent(RawJson);

/// <summary>A stream_event the app does not render (message_start, content_block_stop, ...).</summary>
public sealed record IgnoredStreamEvent(string EventType, string? RawJson) : AgentEvent(RawJson);

/// <summary>The final result message of a turn.</summary>
public sealed record TurnResult(
    string Subtype,
    bool IsError,
    string? ResultText,
    string? SessionId,
    decimal? TotalCostUsd,
    long? DurationMs,
    int? NumTurns,
    TokenUsage? Usage,
    JsonElement? PermissionDenials,
    JsonElement? StructuredOutput,
    string? RawJson) : AgentEvent(RawJson);

public sealed record TokenUsage(long InputTokens, long OutputTokens, long CacheReadInputTokens, long CacheCreationInputTokens);

/// <summary>A line with an unrecognised type. Logged, never fatal.</summary>
public sealed record UnknownEvent(string? Type, string? Subtype, string? RawJson) : AgentEvent(RawJson);

/// <summary>A line that was not valid JSON.</summary>
public sealed record ParseError(string Message, string? RawJson) : AgentEvent(RawJson);

/// <summary>Always the last event of a turn: the CLI process has exited.</summary>
public sealed record ProcessExited(int ExitCode, bool WasStopped, StopOutcome? StopOutcome, string StandardError)
    : AgentEvent(RawJson: null);

public enum StopOutcome
{
    /// <summary>The process exited by itself within the grace period after the interrupt.</summary>
    ExitedAfterInterrupt,
    /// <summary>The process did not exit in time and its process tree was killed.</summary>
    Killed,
}

public abstract record ContentBlock;
public sealed record TextBlock(string Text) : ContentBlock;
public sealed record ThinkingBlock(string Text) : ContentBlock;
public sealed record ToolUseBlock(string Id, string Name, JsonElement Input) : ContentBlock;
public sealed record ToolResultBlock(string ToolUseId, string Content, bool IsError) : ContentBlock;
public sealed record OtherBlock(string Type) : ContentBlock;
