using System.Text.Json;
using AgentFlow.Core.Agents;
using AgentFlow.Core.Chat;

namespace AgentFlow.Core.Workflows;

public enum RunStatus { Running, AwaitingApproval, Completed, Failed, Cancelled }

public enum StageRunStatus { Pending, Running, AwaitingApproval, Succeeded, Failed, Cancelled, Skipped }

public sealed record RunRecord(
    string Id,
    string ProjectId,
    string WorkflowName,
    string WorkflowJson,
    string Task,
    RunStatus Status,
    string? BaseCommit,
    string? CurrentStageKey,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? StateJson = null)
{
    /// <summary>Not finished: the app closed while it was running or waiting.</summary>
    public bool IsOpen => Status is RunStatus.Running or RunStatus.AwaitingApproval;
}

/// <summary>Run state that is not in the stage records: your notes and the files edited so far.</summary>
public sealed record RunState(List<string> Notes, List<string> ChangedFiles);

public enum ResumeMode
{
    /// <summary>Run this stage again, then carry on.</summary>
    RunFrom,
    /// <summary>Mark this stage skipped and carry on with what comes after it.</summary>
    Skip,
    /// <summary>After the app closed mid-run: re-show an unanswered gate, or continue the interrupted turn.</summary>
    Recover,
}

public sealed record StageRunRecord(
    string Id,
    string RunId,
    string StageKey,
    string StageName,
    int Attempt,
    StageRunStatus Status,
    string? Model,
    string? Effort,
    string? SessionId,
    string? ResultText,
    string? StructuredOutputJson,
    decimal? CostUsd,
    long? InputTokens,
    long? OutputTokens,
    long? DurationMs,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

public interface IRunStore
{
    void SaveRun(RunRecord run);
    void SaveStageRun(StageRunRecord stageRun);
    IReadOnlyList<RunRecord> GetRuns(string projectId, int limit = 20);
    IReadOnlyList<StageRunRecord> GetStageRuns(string runId);
}

/// <summary>What one turn produced, as the engine needs it.</summary>
public sealed record TurnOutcome(
    string? SessionId,
    string? Model,
    string Subtype,
    bool IsError,
    bool WasStopped,
    string? ResultText,
    JsonElement? StructuredOutput,
    decimal? CostUsd,
    long? InputTokens,
    long? OutputTokens,
    long? DurationMs,
    IReadOnlyCollection<string> ChangedFiles)
{
    public bool Succeeded => !IsError && !WasStopped && Subtype == "success";
}

public enum GateDecisionKind { Approve, RequestChanges, Cancel }

/// <summary>Your answer at a gate. EditedText replaces the stage's result; Feedback asks for a revision.</summary>
public sealed record GateDecision(GateDecisionKind Kind, string? EditedText = null, string? Feedback = null);

/// <summary>What the engine needs from the chat it runs in.</summary>
public interface IWorkflowHost
{
    /// <summary>True when permission prompts are shown to you instead of denied.</summary>
    bool AsksForApproval { get; }

    /// <param name="onSessionStarted">Called as soon as the session ID is known, so an interrupted stage can be resumed.</param>
    Task<TurnOutcome> RunTurnAsync(StageTurnRequest request, CancellationToken ct, Action<string>? onSessionStarted = null);
    ChatEntry AddEntry(ChatEntryData data);
    void UpdateEntry(ChatEntry entry, ChatEntryData data);
    Task<GateDecision> WaitForGateAsync(ChatEntry gateEntry, CancellationToken ct);
}
