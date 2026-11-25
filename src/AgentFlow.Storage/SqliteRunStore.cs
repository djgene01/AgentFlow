using System.Globalization;
using AgentFlow.Core.Workflows;
using Dapper;

namespace AgentFlow.Storage;

/// <summary>Workflow runs and stage runs. Each run keeps a snapshot of its workflow JSON.</summary>
public sealed partial class SqliteChatStore
{
    public void SaveRun(RunRecord r)
    {
        lock (_gate)
        {
            _db.Execute("""
                INSERT INTO Run (Id, ProjectId, WorkflowName, WorkflowJson, Task, Status, BaseCommit, CurrentStageKey, StartedAt, FinishedAt, StateJson)
                VALUES (@Id, @ProjectId, @WorkflowName, @WorkflowJson, @Task, @Status, @BaseCommit, @CurrentStageKey, @StartedAt, @FinishedAt, @StateJson)
                ON CONFLICT(Id) DO UPDATE SET Status = excluded.Status, BaseCommit = excluded.BaseCommit,
                    CurrentStageKey = excluded.CurrentStageKey, FinishedAt = excluded.FinishedAt, StateJson = excluded.StateJson
                """,
                new
                {
                    r.Id, r.ProjectId, r.WorkflowName, r.WorkflowJson, r.Task, Status = r.Status.ToString(), r.BaseCommit, r.CurrentStageKey,
                    StartedAt = Iso(r.StartedAt), FinishedAt = r.FinishedAt is { } f ? Iso(f) : null, r.StateJson,
                });
        }
    }

    public void SaveStageRun(StageRunRecord s)
    {
        lock (_gate)
        {
            _db.Execute("""
                INSERT INTO StageRun (Id, RunId, StageKey, StageName, Attempt, Status, Model, Effort, SessionId, ResultText,
                    StructuredOutputJson, CostUsd, InputTokens, OutputTokens, DurationMs, StartedAt, FinishedAt)
                VALUES (@Id, @RunId, @StageKey, @StageName, @Attempt, @Status, @Model, @Effort, @SessionId, @ResultText,
                    @StructuredOutputJson, @CostUsd, @InputTokens, @OutputTokens, @DurationMs, @StartedAt, @FinishedAt)
                ON CONFLICT(Id) DO UPDATE SET Status = excluded.Status, Model = excluded.Model, SessionId = excluded.SessionId,
                    ResultText = excluded.ResultText, StructuredOutputJson = excluded.StructuredOutputJson, CostUsd = excluded.CostUsd,
                    InputTokens = excluded.InputTokens, OutputTokens = excluded.OutputTokens, DurationMs = excluded.DurationMs,
                    FinishedAt = excluded.FinishedAt
                """,
                new
                {
                    s.Id, s.RunId, s.StageKey, s.StageName, s.Attempt, Status = s.Status.ToString(), s.Model, s.Effort, s.SessionId,
                    s.ResultText, s.StructuredOutputJson, CostUsd = s.CostUsd?.ToString(CultureInfo.InvariantCulture),
                    s.InputTokens, s.OutputTokens, s.DurationMs,
                    StartedAt = Iso(s.StartedAt), FinishedAt = s.FinishedAt is { } f ? Iso(f) : null,
                });
        }
    }

    public IReadOnlyList<RunRecord> GetRuns(string projectId, int limit = 20)
    {
        lock (_gate)
        {
            return _db.Query<RunRow>("SELECT * FROM Run WHERE ProjectId = @projectId ORDER BY StartedAt DESC LIMIT @limit", new { projectId, limit })
                .Select(r => new RunRecord(r.Id, r.ProjectId, r.WorkflowName, r.WorkflowJson, r.Task, Enum.Parse<RunStatus>(r.Status),
                    r.BaseCommit, r.CurrentStageKey, Parse(r.StartedAt), r.FinishedAt is null ? null : Parse(r.FinishedAt), r.StateJson))
                .ToList();
        }
    }

    public IReadOnlyList<StageRunRecord> GetStageRuns(string runId)
    {
        lock (_gate)
        {
            return _db.Query<StageRunRow>("SELECT * FROM StageRun WHERE RunId = @runId ORDER BY StartedAt", new { runId })
                .Select(s => new StageRunRecord(s.Id, s.RunId, s.StageKey, s.StageName, (int)s.Attempt, Enum.Parse<StageRunStatus>(s.Status),
                    s.Model, s.Effort, s.SessionId, s.ResultText, s.StructuredOutputJson,
                    s.CostUsd is null ? null : decimal.Parse(s.CostUsd, CultureInfo.InvariantCulture),
                    s.InputTokens, s.OutputTokens, s.DurationMs, Parse(s.StartedAt), s.FinishedAt is null ? null : Parse(s.FinishedAt)))
                .ToList();
        }
    }

    private sealed class RunRow
    {
        public string Id { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string WorkflowName { get; set; } = "";
        public string WorkflowJson { get; set; } = "";
        public string Task { get; set; } = "";
        public string Status { get; set; } = "";
        public string? BaseCommit { get; set; }
        public string? CurrentStageKey { get; set; }
        public string StartedAt { get; set; } = "";
        public string? FinishedAt { get; set; }
        public string? StateJson { get; set; }
    }

    private sealed class StageRunRow
    {
        public string Id { get; set; } = "";
        public string RunId { get; set; } = "";
        public string StageKey { get; set; } = "";
        public string StageName { get; set; } = "";
        public long Attempt { get; set; }
        public string Status { get; set; } = "";
        public string? Model { get; set; }
        public string? Effort { get; set; }
        public string? SessionId { get; set; }
        public string? ResultText { get; set; }
        public string? StructuredOutputJson { get; set; }
        public string? CostUsd { get; set; }
        public long? InputTokens { get; set; }
        public long? OutputTokens { get; set; }
        public long? DurationMs { get; set; }
        public string StartedAt { get; set; } = "";
        public string? FinishedAt { get; set; }
    }
}
