using System.Text.Json;
using AgentFlow.Core.Agents;
using AgentFlow.Core.Workflows;

namespace AgentFlow.Core.Chat;

/// <summary>Options chosen in the composer for one direct-chat message.</summary>
public sealed record DirectChatOptions(string? Model, string? Effort, string PermissionMode);

/// <summary>
/// A project's single chat. Direct messages and workflow runs share one timeline. Each turn
/// resumes or starts a Claude Code session; the project remembers the latest one, so after a
/// workflow your next direct message continues the last stage's session.
/// </summary>
public sealed class ProjectChat : IWorkflowHost
{
    private static readonly HashSet<string> EditTools = new(StringComparer.Ordinal) { "Edit", "Write", "MultiEdit", "NotebookEdit" };

    private readonly IChatStore _store;
    private readonly IAgentRunner _runner;
    private readonly IRunStore? _runs;
    private readonly IGitProbe _git;
    private readonly TimeProvider _clock;
    private readonly Dictionary<Guid, TaskCompletionSource<GateDecision>> _gates = [];
    private CancellationTokenSource? _busyCts;

    public Project Project { get; private set; }
    public bool IsRunning => _busyCts is not null;
    public WorkflowEngine? ActiveRun { get; private set; }

    public event Action<ChatEntry>? EntryAdded;
    public event Action<ChatEntry>? EntryUpdated;
    public event Action<RateLimitStatus>? UsageReported;
    public event Action? RunningChanged;
    public event Action<Project>? ProjectChanged;

    /// <summary>Raised when a workflow run starts; subscribe to its events for the run panel.</summary>
    public event Action<WorkflowEngine>? WorkflowStarted;

    private readonly Func<ApprovalPolicy> _approvalPolicy;
    private readonly IMcpIntegrations? _integrations;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource<ApprovalAnswer>> _approvals = new();

    /// <param name="approvalPolicy">Read before each turn and each prompt: ask in the chat, deny, or allow during workflow runs.</param>
    public ProjectChat(Project project, IChatStore store, IAgentRunner runner, IRunStore? runs = null, IGitProbe? git = null,
        TimeProvider? clock = null, Func<ApprovalPolicy>? approvalPolicy = null, IMcpIntegrations? integrations = null)
    {
        _integrations = integrations;
        Project = project;
        _store = store;
        _runner = runner;
        _runs = runs;
        _git = git ?? new GitCli();
        _clock = clock ?? TimeProvider.System;
        _approvalPolicy = approvalPolicy ?? (() => ApprovalPolicy.Deny);
    }

    /// <summary>True when permission prompts reach the app (to ask you or to allow them) instead of being denied by the CLI.</summary>
    public bool AsksForApproval => _approvalPolicy() != ApprovalPolicy.Deny;

    public ApprovalPolicy ApprovalPolicy => _approvalPolicy();

    /// <summary>True when prompts in the current or next workflow run are allowed without asking.</summary>
    public bool AllowsUnattended => _approvalPolicy() is ApprovalPolicy.AllowInWorkflows or ApprovalPolicy.AllowEverywhere;

    /// <summary>True when prompts in direct chat are allowed without asking too.</summary>
    public bool AllowsInDirectChat => _approvalPolicy() == ApprovalPolicy.AllowEverywhere;

    // ---- approvals ----

    /// <summary>Rules you chose "Always allow" for in this project. Passed to every turn as --allowedTools.</summary>
    public IReadOnlyList<string> AllowRules => ApprovalRules.Load(_store, Project.Id);

    public void RemoveAllowRule(string rule) =>
        ApprovalRules.Save(_store, Project.Id, AllowRules.Where(r => r != rule));

    public bool IsApprovalPending(Guid entryId) =>
        _approvals.TryGetValue(entryId, out var tcs) && !tcs.Task.IsCompleted;

    /// <summary>Answers an approval card. The answer is kept until the CLI's request collects it.</summary>
    public bool ResolveApproval(Guid entryId, ApprovalAnswer answer) =>
        _approvals.TryGetValue(entryId, out var tcs) && tcs.TrySetResult(answer);

    /// <summary>Called by the permission server, on its own thread, for each prompt in this chat's turn.</summary>
    private async Task<ApprovalAnswer> HandleApprovalAsync(ApprovalRequest request, string? stageName, CancellationToken ct)
    {
        if (request.ToolName == "AskUserQuestion")
            return new ApprovalAnswer(ApprovalChoice.Deny,
                "Questions are not shown as forms here. Ask your question as normal text in your reply; the user answers in the chat.");

        // A rule you saved during this turn: the CLI started before it existed.
        if (AllowRules.Any(rule => ApprovalRules.Matches(rule, request)))
            return new ApprovalAnswer(ApprovalChoice.AllowOnce);

        var data = new ApprovalData(request.ToolName, ApprovalRules.Describe(request), request.Input.GetRawText(),
            request.Input.ValueKind == JsonValueKind.Object && request.Input.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
            ApprovalRules.Suggest(request), stageName, ApprovalStatus.Pending);

        // Auto-approve (unattended workflow, or direct chat too): allow it now, but still show what was allowed.
        // Tools that cost money always ask.
        var autoAllow = ActiveRun is not null ? AllowsUnattended : AllowsInDirectChat;
        if (autoAllow && _integrations?.MustAsk(request.ToolName) != true)
        {
            AddEntry(data with { Status = ApprovalStatus.AutoAllowed });
            return new ApprovalAnswer(ApprovalChoice.AllowOnce);
        }
        if (_approvalPolicy() == ApprovalPolicy.Deny)
            return new ApprovalAnswer(ApprovalChoice.Deny, "Permission prompts are set to deny automatically in AgentFlow.");

        var entry = AddEntry(data);
        var tcs = _approvals[entry.Id];

        ApprovalAnswer answer;
        try
        {
            await using (ct.Register(() => tcs.TrySetCanceled(ct)))
                answer = await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            UpdateEntry(entry, data with { Status = ApprovalStatus.NotAnswered });
            throw;
        }
        finally
        {
            _approvals.TryRemove(entry.Id, out _);
        }

        if (answer.Choice == ApprovalChoice.AlwaysAllow && data.SuggestedRule is { } rule)
            ApprovalRules.Save(_store, Project.Id, AllowRules.Append(rule));

        UpdateEntry(entry, data with
        {
            Status = answer.Choice switch
            {
                ApprovalChoice.AlwaysAllow => ApprovalStatus.AlwaysAllowed,
                ApprovalChoice.AllowOnce => ApprovalStatus.Allowed,
                _ => ApprovalStatus.Denied,
            },
            DenyMessage = answer.Message,
        });
        return answer.Choice == ApprovalChoice.Deny && string.IsNullOrWhiteSpace(answer.Message)
            ? answer with { Message = "The user denied this action. Do not try to get around it; ask the user if you need it." }
            : answer;
    }

    public IReadOnlyList<ChatEntry> LoadHistory() => _store.GetEntries(Project.Id);

    /// <summary>The next message starts a new Claude Code session instead of resuming.</summary>
    public void StartNewSession()
    {
        if (IsRunning) throw new InvalidOperationException("A turn is running.");
        if (Project.LastSessionId is null) return;
        SetProject(Project with { LastSessionId = null });
        AddEntry(new SessionDividerData("New session"));
    }

    public async Task SendAsync(string text, DirectChatOptions options)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var ct = BeginBusy();
        try
        {
            AddEntry(new UserMessageData(text, options.Model, options.Effort, options.PermissionMode));
            SetProject(Project with { LastModel = options.Model, LastEffort = options.Effort, LastPermissionMode = options.PermissionMode });

            // Messages queued while a turn runs are sent one after another in the same session.
            for (var next = text; next is not null && !ct.IsCancellationRequested; next = TakeDirectQueue())
            {
                var outcome = await RunTurnCoreAsync(new StageTurnRequest
                {
                    WorkingDirectory = Project.FolderPath,
                    Prompt = next,
                    Model = options.Model,
                    Effort = options.Effort,
                    PermissionMode = options.PermissionMode,
                    Session = Project.LastSessionId is { } sid ? new SessionSpec.Resume(sid) : SessionSpec.NewSession(),
                }, ct);
                if (outcome.WasStopped) break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddEntry(new NoticeData(NoticeLevel.Error, ex.Message));
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>Runs a workflow in this chat. Returns when the run ends (completed, failed or stopped).</summary>
    public async Task<RunStatus> RunWorkflowAsync(WorkflowDefinition workflow, string task)
    {
        if (_runs is null) throw new InvalidOperationException("No run store is configured.");
        if (string.IsNullOrWhiteSpace(task)) throw new ArgumentException("The task is empty.", nameof(task));

        var ct = BeginBusy();
        try
        {
            AddEntry(new UserMessageData(task, null, null, null));
            var engine = new WorkflowEngine(workflow, Project, task, this, _runs, _git, _clock);
            ActiveRun = engine;
            WorkflowStarted?.Invoke(engine);
            return await engine.RunAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddEntry(new NoticeData(NoticeLevel.Error, ex.Message));
            return RunStatus.Failed;
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>Continues a saved run: from a stage, skipping a stage, or recovering after the app closed.</summary>
    public async Task<RunStatus> ResumeWorkflowAsync(string runId, ResumeMode mode, string stageKey)
    {
        if (_runs is null) throw new InvalidOperationException("No run store is configured.");
        var run = _runs.GetRuns(Project.Id, 200).FirstOrDefault(r => r.Id == runId)
                  ?? throw new InvalidOperationException("That run no longer exists.");

        var ct = BeginBusy();
        try
        {
            var engine = WorkflowEngine.Continue(run, Project, this, _runs, _git, _clock);
            ActiveRun = engine;
            WorkflowStarted?.Invoke(engine);
            return await engine.ResumeAsync(mode, stageKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddEntry(new NoticeData(NoticeLevel.Error, ex.Message));
            return RunStatus.Failed;
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>The newest run, if any.</summary>
    public RunRecord? LatestRun() => _runs?.GetRuns(Project.Id, 1).FirstOrDefault();

    /// <summary>A run the app was in the middle of when it closed.</summary>
    public RunRecord? FindInterruptedRun() => !IsRunning && LatestRun() is { IsOpen: true } run ? run : null;

    /// <summary>Gives up on an interrupted run.</summary>
    public void DiscardRun(string runId)
    {
        if (_runs?.GetRuns(Project.Id, 200).FirstOrDefault(r => r.Id == runId) is not { } run) return;
        _runs.SaveRun(run with { Status = RunStatus.Cancelled, FinishedAt = _clock.GetUtcNow() });
        foreach (var s in _runs.GetStageRuns(runId).Where(s => s.Status is StageRunStatus.Running or StageRunStatus.AwaitingApproval))
            _runs.SaveStageRun(s with { Status = StageRunStatus.Cancelled, FinishedAt = _clock.GetUtcNow() });
    }

    /// <summary>
    /// A message typed while something runs. In a workflow it goes to the working stage (or to the
    /// notes while waiting at a gate); in direct chat it is sent after the current turn.
    /// </summary>
    public bool Enqueue(string text)
    {
        if (!IsRunning || string.IsNullOrWhiteSpace(text)) return false;
        string target;
        if (ActiveRun is { } run)
        {
            target = run.Enqueue(text);
        }
        else
        {
            lock (_directQueue) _directQueue.Enqueue(text);
            target = "the next turn";
        }
        AddEntry(new UserMessageData(text, null, null, null, QueuedFor: target));
        return true;
    }

    private readonly Queue<string> _directQueue = new();

    private string? TakeDirectQueue()
    {
        lock (_directQueue) return _directQueue.TryDequeue(out var t) ? t : null;
    }

    /// <summary>Stops the running turn, or a run waiting at a gate.</summary>
    public void Stop()
    {
        lock (_directQueue) _directQueue.Clear();
        _busyCts?.Cancel();
    }

    /// <summary>Answers a pending gate card.</summary>
    /// <remarks>The answer is kept until the engine collects it, so answering before it starts waiting is fine.</remarks>
    public bool ResolveGate(Guid gateEntryId, GateDecision decision) =>
        _gates.TryGetValue(gateEntryId, out var tcs) && tcs.TrySetResult(decision);

    public bool IsGatePending(Guid gateEntryId) =>
        _gates.TryGetValue(gateEntryId, out var tcs) && !tcs.Task.IsCompleted;

    // ---- IWorkflowHost ----

    Task<TurnOutcome> IWorkflowHost.RunTurnAsync(StageTurnRequest request, CancellationToken ct, Action<string>? onSessionStarted) =>
        RunTurnCoreAsync(request, ct, onSessionStarted);

    public ChatEntry AddEntry(ChatEntryData data)
    {
        var e = NewEntry(data);
        _store.SaveEntry(e);

        // Register a pending gate or approval before anyone sees the entry, so its card knows it can be answered.
        if (data is GateData { Status: GateStatus.Pending })
            _gates[e.Id] = new TaskCompletionSource<GateDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (data is ApprovalData { Status: ApprovalStatus.Pending })
            _approvals[e.Id] = new TaskCompletionSource<ApprovalAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);

        EntryAdded?.Invoke(e);
        return e;
    }

    public void UpdateEntry(ChatEntry entry, ChatEntryData data)
    {
        entry.Data = data;
        _store.SaveEntry(entry);
        EntryUpdated?.Invoke(entry);
    }

    async Task<GateDecision> IWorkflowHost.WaitForGateAsync(ChatEntry gateEntry, CancellationToken ct)
    {
        if (!_gates.TryGetValue(gateEntry.Id, out var tcs))
            _gates[gateEntry.Id] = tcs = new TaskCompletionSource<GateDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try
        {
            return await tcs.Task;
        }
        finally
        {
            _gates.Remove(gateEntry.Id);
        }
    }

    // ---- turns ----

    private async Task<TurnOutcome> RunTurnCoreAsync(StageTurnRequest request, CancellationToken ct, Action<string>? onSessionStarted = null)
    {
        // Your always-allow rules apply to every turn; prompts reach you when approvals are on.
        var stageName = ActiveRun?.Workflow.Stages.FirstOrDefault(s => s.Key == ActiveRun.Run.CurrentStageKey)?.Name;
        // Integrations (the video studio, Pixel Art Studio, SfxForge): their MCP servers are added, their safe
        // tools allowed. A stage's own deny rules still win over these allows. Prepare starts an app whose
        // server lives in its window (Pixel Art Studio), so the server is there when Claude Code connects.
        if (_integrations is not null) await _integrations.PrepareAsync(ct);
        var servers = _integrations?.Servers() ?? [];
        request = request with
        {
            McpServers = request.McpServers.Concat(servers).ToList(),
            AllowedTools = request.AllowedTools.Concat(AllowRules).Concat(servers.Count > 0 ? _integrations!.AllowedTools() : []).Distinct().ToList(),
            ApprovalHandler = AsksForApproval ? (r, c) => HandleApprovalAsync(r, stageName, c) : null,
        };

        var turnId = Guid.NewGuid();
        var projector = new TurnProjector(NewEntry);
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        projector.Added += e =>
        {
            if (!IsStreamingText(e)) _store.SaveEntry(e);
            EntryAdded?.Invoke(e);
        };
        projector.Updated += e =>
        {
            if (!IsStreamingText(e)) _store.SaveEntry(e);
            EntryUpdated?.Invoke(e);
        };
        projector.UsageReported += rl => UsageReported?.Invoke(rl);

        TurnResult? result = null;
        ProcessExited? exit = null;

        await foreach (var ev in _runner.RunTurnAsync(request, ct))
        {
            if (ev.RawJson is { } raw)
                _store.AppendRawEvent(Project.Id, turnId, projector.SessionId, raw);

            projector.Apply(ev);

            switch (ev)
            {
                case SessionStarted s when s.SessionId.Length > 0:
                    onSessionStarted?.Invoke(s.SessionId);
                    break;
                case AssistantMessage a:
                    foreach (var t in a.ToolUses.Where(t => EditTools.Contains(t.Name)))
                        if (FilePath(t.Input) is { } path) changed.Add(path);
                    break;
                case TurnResult r:
                    result = r;
                    break;
                case ProcessExited x:
                    exit = x;
                    break;
            }

            // Save the session as soon as it is known, so a crash mid-turn can still resume it.
            if (ev is SessionStarted or TurnResult && projector.SessionId is { Length: > 0 } id && id != Project.LastSessionId)
                SetProject(Project with { LastSessionId = id });
        }

        return new TurnOutcome(
            result?.SessionId ?? projector.SessionId,
            projector.Model,
            result?.Subtype ?? (exit?.WasStopped == true ? "stopped" : "no_result"),
            result?.IsError ?? true,
            exit?.WasStopped ?? false,
            result?.ResultText,
            result?.StructuredOutput,
            result?.TotalCostUsd,
            result?.Usage?.InputTokens,
            result?.Usage?.OutputTokens,
            result?.DurationMs,
            changed);
    }

    private static string? FilePath(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object
        && (input.TryGetProperty("file_path", out var p) || input.TryGetProperty("notebook_path", out p))
        && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private CancellationToken BeginBusy()
    {
        if (IsRunning) throw new InvalidOperationException("A turn is already running.");
        _busyCts = new CancellationTokenSource();
        RunningChanged?.Invoke();
        return _busyCts.Token;
    }

    private void EndBusy()
    {
        _busyCts?.Dispose();
        _busyCts = null;
        ActiveRun = null;
        SetProject(Project with { LastActivityAt = _clock.GetUtcNow() });
        RunningChanged?.Invoke();
    }

    private static bool IsStreamingText(ChatEntry e) => e.Data is AssistantTextData { IsStreaming: true };

    private ChatEntry NewEntry(ChatEntryData data) =>
        new(Guid.NewGuid(), Project.Id, _store.NextSeq(Project.Id), data, _clock.GetUtcNow());

    private void SetProject(Project p)
    {
        Project = p;
        _store.SaveProject(p);
        ProjectChanged?.Invoke(p);
    }
}
