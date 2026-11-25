using System.Text.Json.Serialization;

namespace AgentFlow.Core.Chat;

/// <summary>One item in a project's chat timeline. <see cref="Data"/> is replaced, never mutated.</summary>
public sealed class ChatEntry(Guid id, string projectId, long seq, ChatEntryData data, DateTimeOffset createdAt)
{
    public Guid Id { get; } = id;
    public string ProjectId { get; } = projectId;
    public long Seq { get; } = seq;
    public DateTimeOffset CreatedAt { get; } = createdAt;
    public ChatEntryData Data { get; set; } = data;
}

/// <summary>The persisted content of a chat entry, stored as JSON with a kind discriminator.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(UserMessageData), "user")]
[JsonDerivedType(typeof(AssistantTextData), "assistant")]
[JsonDerivedType(typeof(ToolCallData), "tool")]
[JsonDerivedType(typeof(NoticeData), "notice")]
[JsonDerivedType(typeof(TurnSummaryData), "summary")]
[JsonDerivedType(typeof(SessionDividerData), "divider")]
[JsonDerivedType(typeof(StageDividerData), "stage")]
[JsonDerivedType(typeof(GateData), "gate")]
[JsonDerivedType(typeof(VerdictData), "verdict")]
[JsonDerivedType(typeof(RunStatusData), "run")]
[JsonDerivedType(typeof(RecoveryData), "recovery")]
[JsonDerivedType(typeof(ApprovalData), "approval")]
public abstract record ChatEntryData;

/// <summary>Start of a workflow stage in the timeline.</summary>
public sealed record StageDividerData(
    string RunId,
    string StageKey,
    string StageName,
    int StageNumber,
    int Attempt,
    string? Model,
    string? Effort,
    string Permissions,
    string SessionMode) : ChatEntryData;

public enum GateStatus { Pending, Approved, ChangesRequested, Cancelled }

/// <summary>A stage result waiting for your approval before the run continues.</summary>
public sealed record GateData(
    string RunId,
    string StageKey,
    string StageName,
    string Text,
    GateStatus Status,
    string? Feedback = null,
    bool WasEdited = false,
    string GateKind = GateData.ApprovalKind) : ChatEntryData
{
    /// <summary>Approve a stage's result before the run continues.</summary>
    public const string ApprovalKind = "approval";

    /// <summary>A review loop reached its limit: Approve = one more round, Cancel = finish the run.</summary>
    public const string LoopLimitKind = "loop_limit";
}

public enum ApprovalStatus { Pending, Allowed, AlwaysAllowed, Denied, NotAnswered, AutoAllowed }

/// <summary>Claude Code asks permission for a tool call that no rule allows.</summary>
public sealed record ApprovalData(
    string ToolName,
    string Summary,
    string InputJson,
    string? Description,
    string? SuggestedRule,
    string? StageName,
    ApprovalStatus Status,
    string? DenyMessage = null) : ChatEntryData;

public enum RecoveryStatus { Pending, Resumed, Discarded }

/// <summary>The app closed while a run was going; offers to resume it.</summary>
public sealed record RecoveryData(string RunId, string WorkflowName, string StageKey, string StageName, bool AtGate, RecoveryStatus Status) : ChatEntryData;

public sealed record VerdictFinding(string Severity, string? File, int? Line, string Issue, string? Fix);

/// <summary>A reviewer's structured verdict.</summary>
public sealed record VerdictData(string RunId, string StageKey, string Verdict, string? Summary, IReadOnlyList<VerdictFinding> Findings) : ChatEntryData;

/// <summary>Start or end of a workflow run.</summary>
public sealed record RunStatusData(string RunId, string WorkflowName, string Status, string Text) : ChatEntryData;

public sealed record UserMessageData(string Text, string? Model, string? Effort, string? PermissionMode, string? QueuedFor = null) : ChatEntryData;

public sealed record AssistantTextData(string Text, bool IsStreaming, string? ParentToolUseId = null) : ChatEntryData;

public sealed record ToolCallData(
    string ToolUseId,
    string Name,
    string InputJson,
    string? Result,
    bool IsError,
    string? ParentToolUseId) : ChatEntryData
{
    [JsonIgnore] public bool IsPending => Result is null;
}

public enum NoticeLevel { Info, Warning, Error }

public sealed record NoticeData(NoticeLevel Level, string Text) : ChatEntryData;

public sealed record TurnSummaryData(
    string? SessionId,
    string? Model,
    string Subtype,
    bool IsError,
    long? DurationMs,
    int? NumTurns,
    decimal? CostUsd,
    long? InputTokens,
    long? OutputTokens,
    bool WasStopped) : ChatEntryData;

/// <summary>Marks where a new Claude Code session starts in the timeline.</summary>
public sealed record SessionDividerData(string Text) : ChatEntryData;
