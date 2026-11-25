using System.Text.Json;
using AgentFlow.Core.Agents;
using AgentFlow.Core.Chat;

namespace AgentFlow.Core.Workflows;

/// <summary>
/// Runs one workflow in a project's chat: one stage turn at a time, a divider per stage, gates
/// that wait for you, and transitions decided by the stage's result. Everything it shows goes
/// through <see cref="IWorkflowHost"/>, so the whole run appears in the project's single chat.
/// A run can be continued later from its saved records: from a stage, skipping one, or after a crash.
/// </summary>
public sealed class WorkflowEngine
{
    private readonly WorkflowDefinition _wf;
    private readonly Project _project;
    private readonly IWorkflowHost _host;
    private readonly IRunStore _runs;
    private readonly IGitProbe _git;
    private readonly TimeProvider _clock;

    private readonly Dictionary<string, StageResult> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Stage, int Transition), int> _loops = [];
    private readonly Dictionary<(string Stage, int Transition), int> _extraLoops = [];
    private readonly HashSet<string> _changedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _notes = [];
    private readonly Queue<string> _queued = new();
    private readonly List<StageRunRecord> _history = [];

    private sealed record StageResult(string? SessionId, string? Text, Verdict? Verdict);

    public RunRecord Run { get; private set; }
    public WorkflowDefinition Workflow => _wf;

    public event Action<RunRecord>? RunChanged;
    public event Action<StageRunRecord>? StageRunChanged;

    private WorkflowEngine(WorkflowDefinition wf, Project project, RunRecord run, IWorkflowHost host, IRunStore runs, IGitProbe git, TimeProvider? clock)
    {
        var errors = WorkflowJson.Validate(wf);
        if (errors.Count > 0)
            throw new InvalidOperationException("The workflow is not valid:\n" + string.Join("\n", errors));
        _wf = wf;
        _project = project;
        Run = run;
        _host = host;
        _runs = runs;
        _git = git;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>A new run of <paramref name="wf"/>.</summary>
    public WorkflowEngine(WorkflowDefinition wf, Project project, string task, IWorkflowHost host, IRunStore runs, IGitProbe git, TimeProvider? clock = null)
        : this(wf, project, new RunRecord(Guid.NewGuid().ToString("D"), project.Id, wf.Name, WorkflowJson.Serialize(wf), task,
            RunStatus.Running, null, null, (clock ?? TimeProvider.System).GetUtcNow(), null), host, runs, git, clock)
    {
    }

    /// <summary>Continues a saved run using its own workflow snapshot and stage records.</summary>
    public static WorkflowEngine Continue(RunRecord run, Project project, IWorkflowHost host, IRunStore runs, IGitProbe git, TimeProvider? clock = null)
    {
        var engine = new WorkflowEngine(WorkflowJson.Parse(run.WorkflowJson), project, run, host, runs, git, clock);
        engine.LoadHistory(runs.GetStageRuns(run.Id));
        return engine;
    }

    // ---- public API ----

    public Task<RunStatus> RunAsync(CancellationToken ct) => ExecuteAsync(_wf.Stages[0].Key, ResumeMode.RunFrom, isResume: false, ct);

    public Task<RunStatus> ResumeAsync(ResumeMode mode, string stageKey, CancellationToken ct)
    {
        if (_wf.Stages.All(s => s.Key != stageKey))
            throw new ArgumentException($"The run's workflow has no stage '{stageKey}'.", nameof(stageKey));
        return ExecuteAsync(stageKey, mode, isResume: true, ct);
    }

    /// <summary>
    /// A message you typed during the run. While a stage is working it becomes that stage's next turn;
    /// while the run waits at a gate it becomes a note for the next stage.
    /// </summary>
    /// <returns>Where it went: the stage name, or "notes".</returns>
    public string Enqueue(string text)
    {
        if (Run.Status == RunStatus.AwaitingApproval)
        {
            _notes.Add(text);
            SaveState();
            return "notes";
        }
        lock (_queued) _queued.Enqueue(text);
        return _wf.Stages.FirstOrDefault(s => s.Key == Run.CurrentStageKey)?.Name ?? "the next stage";
    }

    // ---- run loop ----

    private async Task<RunStatus> ExecuteAsync(string firstKey, ResumeMode firstMode, bool isResume, CancellationToken ct)
    {
        SetRun(Run with
        {
            Status = RunStatus.Running,
            FinishedAt = null,
            BaseCommit = Run.BaseCommit ?? await _git.GetHeadAsync(_project.FolderPath, ct),
        });

        var stageName = _wf.Stages.First(s => s.Key == firstKey).Name;
        _host.AddEntry(new RunStatusData(Run.Id, _wf.Name, isResume ? "resumed" : "started", isResume
            ? firstMode switch
            {
                ResumeMode.Skip => $"Workflow \"{_wf.Name}\" continues after skipping {stageName}.",
                ResumeMode.Recover => $"Workflow \"{_wf.Name}\" resumes at {stageName}.",
                _ => $"Workflow \"{_wf.Name}\" runs again from {stageName}.",
            }
            : $"Workflow \"{_wf.Name}\" started: {string.Join(" → ", _wf.Stages.Select(s => s.Name))}"));

        var status = RunStatus.Completed;
        try
        {
            string? key = firstKey;
            var mode = firstMode;

            // Closed while deciding what comes next (for example at a loop-limit question): the stage itself is done.
            if (mode == ResumeMode.Recover && LatestRun(firstKey)?.Status == StageRunStatus.Succeeded)
            {
                key = await NextStageAsync(_wf.Stages.First(s => s.Key == firstKey), ct);
                mode = ResumeMode.RunFrom;
            }

            if (mode == ResumeMode.Skip)
            {
                var skipped = _wf.Stages.First(s => s.Key == key);
                CloseInterrupted(skipped.Key);
                SetStageRun(NewStageRun(skipped, StageRunStatus.Skipped) with { FinishedAt = _clock.GetUtcNow() });
                _host.AddEntry(new NoticeData(NoticeLevel.Info, $"{skipped.Name} skipped."));
                key = await NextStageAsync(skipped, ct);
                mode = ResumeMode.RunFrom;
            }

            while (key is not null)
            {
                var stage = _wf.Stages.First(s => s.Key == key);
                var stop = await RunStageAsync(stage, mode, ct);
                mode = ResumeMode.RunFrom;
                if (stop is not null)
                {
                    status = stop.Value;
                    break;
                }
                key = await NextStageAsync(stage, ct);
            }
        }
        catch (OperationCanceledException)
        {
            status = RunStatus.Cancelled;
        }

        // Keep the stage it stopped at, so Retry and Skip know where to continue.
        SetRun(Run with
        {
            Status = status,
            CurrentStageKey = status == RunStatus.Completed ? null : Run.CurrentStageKey,
            FinishedAt = _clock.GetUtcNow(),
        });
        _host.AddEntry(new RunStatusData(Run.Id, _wf.Name, status.ToString().ToLowerInvariant(), FinishText(status)));
        return status;
    }

    /// <summary>Runs a stage, including gate revisions. Returns a final run status to stop the run, or null to continue.</summary>
    private async Task<RunStatus?> RunStageAsync(StageDefinition stage, ResumeMode mode, CancellationToken ct)
    {
        var stageNumber = _wf.Stages.IndexOf(stage) + 1;
        var (permissionMode, allowed, disallowed) = PermissionPresets.Resolve(stage, _host.AsksForApproval);
        var prompt = await RenderPromptAsync(stage, ct);
        var session = ResolveSession(stage);

        // After a crash: an unanswered gate comes back as it was; an interrupted turn continues its session.
        StageRunRecord? atGate = null;
        if (mode == ResumeMode.Recover && LatestRun(stage.Key) is { } last)
        {
            if (last.Status == StageRunStatus.AwaitingApproval && last.ResultText is not null)
            {
                atGate = last;
            }
            else if (last.Status == StageRunStatus.Running)
            {
                SetStageRun(last with { Status = StageRunStatus.Cancelled, FinishedAt = _clock.GetUtcNow() });
                if (last.SessionId is { } sid)
                {
                    session = new SessionSpec.Resume(sid);
                    prompt = $"""
                        Your previous turn was interrupted before it finished because the app closed.
                        Check what you had already done, then continue and complete the original request below.

                        {prompt}
                        """;
                }
            }
        }
        else
        {
            CloseInterrupted(stage.Key);
        }

        while (true)
        {
            StageRunRecord stageRun;
            TurnOutcome outcome;
            string? resultText;
            Verdict? verdict;

            if (atGate is not null)
            {
                stageRun = atGate;
                atGate = null;
                verdict = stage.Output == StageOutput.Verdict && stageRun.StructuredOutputJson is { } so
                    ? Verdict.FromStructuredOutput(JsonDocument.Parse(so).RootElement.Clone())
                    : null;
                resultText = stageRun.ResultText;
                outcome = new TurnOutcome(stageRun.SessionId, stageRun.Model, "success", false, false, resultText, null,
                    stageRun.CostUsd, stageRun.InputTokens, stageRun.OutputTokens, stageRun.DurationMs, []);
                _results[stage.Key] = new StageResult(stageRun.SessionId, resultText, verdict);
                SetRun(Run with { CurrentStageKey = stage.Key });
            }
            else
            {
                var attempt = _attempts[stage.Key] = _attempts.GetValueOrDefault(stage.Key) + 1;
                SetRun(Run with { Status = RunStatus.Running, CurrentStageKey = stage.Key });
                _host.AddEntry(new StageDividerData(Run.Id, stage.Key, stage.Name, stageNumber, attempt, stage.Model, stage.Effort,
                    PermissionPresets.Describe(stage.PermissionPreset), DescribeSession(session, stage.Session)));

                stageRun = NewStageRun(stage, StageRunStatus.Running) with { Attempt = attempt };
                SetStageRun(stageRun);

                var request = new StageTurnRequest
                {
                    WorkingDirectory = _project.FolderPath,
                    Prompt = prompt,
                    Model = stage.Model,
                    Effort = stage.Effort,
                    PermissionMode = permissionMode,
                    AllowedTools = allowed,
                    DisallowedTools = disallowed,
                    Session = session,
                    AppendSystemPrompt = stage.RolePrompt,
                    MaxTurns = stage.MaxTurns,
                    MaxBudgetUsd = stage.MaxBudgetUsd,
                    JsonSchema = stage.Output == StageOutput.Verdict ? Verdict.JsonSchema : null,
                };

                var current = stageRun;
                outcome = await _host.RunTurnAsync(request, ct, sid => SetStageRun(current = current with { SessionId = sid }));
                stageRun = current;

                // Messages you typed while this stage worked: one more turn in the same session.
                while (outcome.Succeeded && TakeQueued() is { } queued)
                {
                    var more = await _host.RunTurnAsync(request with
                    {
                        Prompt = queued,
                        Session = outcome.SessionId is { } s ? new SessionSpec.Resume(s) : request.Session,
                    }, ct);
                    outcome = Merge(outcome, more);
                }

                foreach (var f in outcome.ChangedFiles) _changedFiles.Add(f);
                SaveState();
                verdict = stage.Output == StageOutput.Verdict ? Verdict.FromStructuredOutput(outcome.StructuredOutput) : null;
                // With --json-schema the result text is the JSON itself, so a verdict's summary reads better.
                resultText = verdict?.Summary is { Length: > 0 } summary ? summary : outcome.ResultText;

                stageRun = stageRun with
                {
                    SessionId = outcome.SessionId ?? stageRun.SessionId,
                    Model = outcome.Model ?? stage.Model,
                    ResultText = resultText,
                    StructuredOutputJson = outcome.StructuredOutput?.GetRawText(),
                    CostUsd = outcome.CostUsd,
                    InputTokens = outcome.InputTokens,
                    OutputTokens = outcome.OutputTokens,
                    DurationMs = outcome.DurationMs,
                    FinishedAt = _clock.GetUtcNow(),
                };

                if (outcome.WasStopped)
                {
                    SetStageRun(stageRun with { Status = StageRunStatus.Cancelled });
                    return RunStatus.Cancelled;
                }
                if (!outcome.Succeeded)
                {
                    SetStageRun(stageRun with { Status = StageRunStatus.Failed });
                    _host.AddEntry(new NoticeData(NoticeLevel.Error, $"{stage.Name} did not finish ({outcome.Subtype}). The workflow stops here."));
                    return RunStatus.Failed;
                }

                _results[stage.Key] = new StageResult(outcome.SessionId, resultText, verdict);

                if (stage.Output == StageOutput.Verdict)
                {
                    if (verdict is null)
                        _host.AddEntry(new NoticeData(NoticeLevel.Warning, $"{stage.Name} returned no structured verdict."));
                    else
                        _host.AddEntry(new VerdictData(Run.Id, stage.Key, verdict.Value, verdict.Summary,
                            verdict.Findings.Select(f => new VerdictFinding(f.Severity, f.File, f.Line, f.Issue, f.Fix)).ToList()));
                }

                if (stage.Gate != StageGate.Approve)
                {
                    SetStageRun(stageRun with { Status = StageRunStatus.Succeeded });
                    return null;
                }
            }

            // Gate: wait for you.
            SetStageRun(stageRun with { Status = StageRunStatus.AwaitingApproval });
            SetRun(Run with { Status = RunStatus.AwaitingApproval });
            var gate = _host.AddEntry(new GateData(Run.Id, stage.Key, stage.Name, resultText ?? "", GateStatus.Pending));
            var decision = await _host.WaitForGateAsync(gate, ct);
            var gateData = (GateData)gate.Data;
            SetRun(Run with { Status = RunStatus.Running });

            switch (decision.Kind)
            {
                case GateDecisionKind.Approve:
                    var edited = !string.IsNullOrWhiteSpace(decision.EditedText) && decision.EditedText != resultText;
                    if (edited) _results[stage.Key] = _results[stage.Key] with { Text = decision.EditedText };
                    if (!string.IsNullOrWhiteSpace(decision.Feedback)) _notes.Add(decision.Feedback!);
                    SaveState();
                    _host.UpdateEntry(gate, gateData with
                    {
                        Status = GateStatus.Approved,
                        Text = edited ? decision.EditedText! : gateData.Text,
                        WasEdited = edited,
                        Feedback = decision.Feedback,
                    });
                    SetStageRun(stageRun with { Status = StageRunStatus.Succeeded, ResultText = _results[stage.Key].Text });
                    return null;

                case GateDecisionKind.RequestChanges:
                    _host.UpdateEntry(gate, gateData with { Status = GateStatus.ChangesRequested, Feedback = decision.Feedback });
                    SetStageRun(stageRun with { Status = StageRunStatus.Succeeded });
                    // Revise in the same session so the stage keeps its context.
                    session = outcome.SessionId is { } sid ? new SessionSpec.Resume(sid) : SessionSpec.NewSession();
                    prompt = $"""
                        Revise your result based on this feedback, then give the complete revised result
                        (not only the changes).

                        Feedback:
                        {decision.Feedback}
                        """;
                    continue;

                default:
                    _host.UpdateEntry(gate, gateData with { Status = GateStatus.Cancelled });
                    SetStageRun(stageRun with { Status = StageRunStatus.Cancelled });
                    return RunStatus.Cancelled;
            }
        }
    }

    private async Task<string?> NextStageAsync(StageDefinition stage, CancellationToken ct)
    {
        if (stage.Transitions.Count == 0)
        {
            var i = _wf.Stages.IndexOf(stage);
            return i + 1 < _wf.Stages.Count ? _wf.Stages[i + 1].Key : null;
        }

        var result = _results.GetValueOrDefault(stage.Key);
        for (var i = 0; i < stage.Transitions.Count; i++)
        {
            var t = stage.Transitions[i];
            if (t.When is not null && !string.Equals(t.When, result?.Verdict?.Value, StringComparison.Ordinal)) continue;

            var taken = _loops.GetValueOrDefault((stage.Key, i));
            if (t.MaxLoops is { } max && taken >= max + _extraLoops.GetValueOrDefault((stage.Key, i)))
            {
                // Limit reached: ask whether to go round once more.
                var target = t.Goto == "end" ? "the end" : _wf.Stages.First(s => s.Key == t.Goto).Name;
                var open = result?.Verdict is { Findings.Count: > 0 } v ? "\n\nStill open:\n" + v.FindingsMarkdown() : "";
                SetRun(Run with { Status = RunStatus.AwaitingApproval });
                var gate = _host.AddEntry(new GateData(Run.Id, stage.Key, stage.Name,
                    $"{stage.Name} still asks for changes after {taken} round{(taken == 1 ? "" : "s")} of {target}.{open}",
                    GateStatus.Pending, GateKind: GateData.LoopLimitKind));
                var decision = await _host.WaitForGateAsync(gate, ct);
                SetRun(Run with { Status = RunStatus.Running });
                if (decision.Kind == GateDecisionKind.Approve)
                {
                    _host.UpdateEntry(gate, (GateData)gate.Data with { Status = GateStatus.Approved });
                    _extraLoops[(stage.Key, i)] = _extraLoops.GetValueOrDefault((stage.Key, i)) + 1;
                }
                else
                {
                    _host.UpdateEntry(gate, (GateData)gate.Data with { Status = GateStatus.Cancelled });
                    continue;
                }
            }

            _loops[(stage.Key, i)] = taken + 1;
            return t.Goto == "end" ? null : t.Goto;
        }
        return null;
    }

    // ---- state ----

    private void LoadHistory(IReadOnlyList<StageRunRecord> history)
    {
        _history.AddRange(history);
        foreach (var s in history.OrderBy(h => h.StartedAt))
        {
            _attempts[s.StageKey] = Math.Max(_attempts.GetValueOrDefault(s.StageKey), s.Attempt);
            if (s.Status is StageRunStatus.Succeeded)
            {
                var def = _wf.Stages.FirstOrDefault(x => x.Key == s.StageKey);
                var verdict = def?.Output == StageOutput.Verdict && s.StructuredOutputJson is { } so
                    ? Verdict.FromStructuredOutput(JsonDocument.Parse(so).RootElement.Clone())
                    : null;
                _results[s.StageKey] = new StageResult(s.SessionId, s.ResultText, verdict);
            }
        }

        // Loop counts: a transition into a stage has been taken once for every round of that stage.
        foreach (var stage in _wf.Stages)
            for (var i = 0; i < stage.Transitions.Count; i++)
                if (stage.Transitions[i].Goto is var g && g != "end")
                    _loops[(stage.Key, i)] = history.Count(h => h.StageKey == g && h.Status != StageRunStatus.Skipped);

        if (Run.StateJson is { } json && JsonSerializer.Deserialize<RunState>(json, WorkflowJson.Options) is { } state)
        {
            _notes.AddRange(state.Notes);
            foreach (var f in state.ChangedFiles) _changedFiles.Add(f);
        }
    }

    private StageRunRecord? LatestRun(string stageKey) =>
        _history.Where(h => h.StageKey == stageKey).OrderBy(h => h.StartedAt).LastOrDefault();

    /// <summary>A stage record still marked Running belongs to a turn that will never finish.</summary>
    private void CloseInterrupted(string stageKey)
    {
        foreach (var h in _history.Where(h => h.StageKey == stageKey && h.Status is StageRunStatus.Running or StageRunStatus.AwaitingApproval).ToList())
            SetStageRun(h with { Status = StageRunStatus.Cancelled, FinishedAt = h.FinishedAt ?? _clock.GetUtcNow() });
    }

    private StageRunRecord NewStageRun(StageDefinition stage, StageRunStatus status) =>
        new(Guid.NewGuid().ToString("D"), Run.Id, stage.Key, stage.Name, _attempts.GetValueOrDefault(stage.Key), status,
            stage.Model, stage.Effort, null, null, null, null, null, null, null, _clock.GetUtcNow(), null);

    private string? TakeQueued()
    {
        lock (_queued)
        {
            if (_queued.Count == 0) return null;
            var all = string.Join("\n\n", _queued);
            _queued.Clear();
            return all;
        }
    }

    private static TurnOutcome Merge(TurnOutcome a, TurnOutcome b) => b with
    {
        SessionId = b.SessionId ?? a.SessionId,
        StructuredOutput = b.StructuredOutput ?? a.StructuredOutput,
        CostUsd = (a.CostUsd ?? 0) + (b.CostUsd ?? 0),
        InputTokens = (a.InputTokens ?? 0) + (b.InputTokens ?? 0),
        OutputTokens = (a.OutputTokens ?? 0) + (b.OutputTokens ?? 0),
        DurationMs = (a.DurationMs ?? 0) + (b.DurationMs ?? 0),
        ChangedFiles = a.ChangedFiles.Concat(b.ChangedFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
    };

    private string FinishText(RunStatus status)
    {
        var stages = _runs.GetStageRuns(Run.Id);
        var cost = stages.Sum(s => s.CostUsd ?? 0);
        var tokens = stages.Sum(s => (s.InputTokens ?? 0) + (s.OutputTokens ?? 0));
        var elapsed = (Run.FinishedAt ?? _clock.GetUtcNow()) - Run.StartedAt;
        var totals = $"{Duration(elapsed)} · {stages.Count(s => s.Status != StageRunStatus.Skipped)} stage turns · {tokens:N0} tokens · est. ${cost:0.00}";
        var stoppedAt = _wf.Stages.FirstOrDefault(s => s.Key == Run.CurrentStageKey)?.Name;
        return status switch
        {
            RunStatus.Completed => $"Workflow \"{_wf.Name}\" finished. {totals}",
            RunStatus.Cancelled => $"Workflow \"{_wf.Name}\" was stopped{(stoppedAt is null ? "" : " at " + stoppedAt)}. {totals}",
            _ => $"Workflow \"{_wf.Name}\" stopped because {stoppedAt ?? "a stage"} failed. {totals}",
        };

        static string Duration(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{t.Seconds} s";
    }

    private SessionSpec ResolveSession(StageDefinition stage)
    {
        var m = WorkflowJson.SessionPattern().Match(stage.Session);
        if (!m.Success) return SessionSpec.NewSession();

        if (_results.GetValueOrDefault(m.Groups[2].Value)?.SessionId is { } sid)
            return new SessionSpec.Resume(sid, Fork: m.Groups[1].Value == "fork");

        _host.AddEntry(new NoticeData(NoticeLevel.Warning, $"{stage.Name}: stage '{m.Groups[2].Value}' has no session yet, so a new one starts."));
        return SessionSpec.NewSession();
    }

    private static string DescribeSession(SessionSpec spec, string configured) => spec switch
    {
        SessionSpec.Resume { Fork: true } => $"forks {configured[5..]}'s session",
        SessionSpec.Resume when configured.StartsWith("continue:", StringComparison.Ordinal) => $"continues {configured[9..]}'s session",
        SessionSpec.Resume => "same session",
        _ => "fresh session",
    };

    private async Task<string> RenderPromptAsync(StageDefinition stage, CancellationToken ct)
    {
        string? diff = null;
        if (PromptTemplate.Variables(stage.PromptTemplate).Contains("git.diff"))
        {
            diff = Run.BaseCommit is { } baseCommit ? await _git.GetDiffAsync(_project.FolderPath, baseCommit, ct) : null;
            diff ??= "(not available: the folder is not a git repository git can read; use the list of edited files)";
            if (diff.Length == 0) diff = "(no changes)";
        }

        return PromptTemplate.Render(stage.PromptTemplate, v => v switch
        {
            "task" => Run.Task,
            "notes" => _notes.Count == 0 ? null : string.Join("\n\n", _notes),
            "changedFiles" => _changedFiles.Count == 0 ? null : string.Join("\n", _changedFiles.Order().Select(f => "- " + f)),
            "git.diff" => diff,
            "git.baseCommit" => Run.BaseCommit,
            _ when PromptTemplate.StageReference(v) is { } key => v.EndsWith(".findings", StringComparison.Ordinal)
                ? _results.GetValueOrDefault(key)?.Verdict?.FindingsMarkdown()
                : _results.GetValueOrDefault(key)?.Text,
            _ => null,
        });
    }

    private void SaveState() =>
        SetRun(Run with { StateJson = JsonSerializer.Serialize(new RunState(_notes.ToList(), _changedFiles.Order().ToList()), WorkflowJson.Options) });

    private void SetRun(RunRecord r)
    {
        Run = r;
        _runs.SaveRun(r);
        RunChanged?.Invoke(r);
    }

    private void SetStageRun(StageRunRecord s)
    {
        var i = _history.FindIndex(h => h.Id == s.Id);
        if (i >= 0) _history[i] = s; else _history.Add(s);
        _runs.SaveStageRun(s);
        StageRunChanged?.Invoke(s);
    }
}
