using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentFlow.Core.Agents;
using AgentFlow.Core.Chat;
using AgentFlow.Core.Workflows;
using AgentFlow.Storage;

namespace AgentFlow.ClaudeCli.Tests.Workflows;

/// <summary>Phase 4: retry, skip, run again from a stage, recovery after the app closed, and queued messages.</summary>
public class ResumeAndQueueTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("agentflow-resume-").FullName;
    private readonly SqliteChatStore _store;
    private readonly Project _project;

    public ResumeAndQueueTests()
    {
        _store = new SqliteChatStore(Path.Combine(_dir, "app.db"));
        _project = new Project("p1", "Demo", _dir, null, null, null, "acceptEdits", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        _store.SaveProject(_project);
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeGit : IGitProbe
    {
        public Task<string?> GetHeadAsync(string folder, CancellationToken ct = default) => Task.FromResult<string?>("abc123");
        public Task<string?> GetDiffAsync(string folder, string baseCommit, CancellationToken ct = default) => Task.FromResult<string?>("the diff");
    }

    private sealed class Agent : IAgentRunner
    {
        public List<StageTurnRequest> Requests { get; } = [];
        public string? FailModel { get; set; }
        public string Verdict { get; set; } = "pass";

        public async IAsyncEnumerable<AgentEvent> RunTurnAsync(StageTurnRequest r, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(r);
            await Task.Yield();
            var sid = r.Session is SessionSpec.Resume { Fork: false } res ? res.SessionId : Guid.NewGuid().ToString("D");
            yield return new SessionStarted(sid, r.Model, [], [], r.PermissionMode, null);
            if (r.Model == FailModel)
            {
                yield return new TurnResult("error_during_execution", true, "boom", sid, 0.01m, 100, 1, null, null, null, null);
                yield return new ProcessExited(1, false, null, "");
                yield break;
            }
            JsonElement? so = r.JsonSchema is null ? null
                : JsonDocument.Parse($$"""{"verdict":"{{Verdict}}","summary":"ok","findings":[]}""").RootElement.Clone();
            var text = $"{Stage(r)} result #{Requests.Count}";
            yield return new AssistantMessage("m", [new TextBlock(text)], null, null);
            yield return new TurnResult("success", false, text, sid, 0.01m, 100, 1, new TokenUsage(10, 5, 0, 0), null, so, null);
            yield return new ProcessExited(0, false, null, "");
        }
    }

    private static string Stage(StageTurnRequest r) => r.Model switch
    {
        "claude-fable-5-2" => "plan",
        "claude-sonnet-5-5" => "review",
        "claude-opus-5-5" when r.Effort == "medium" => "develop",
        "claude-opus-5-5" => "refine",
        _ => r.Model ?? "?",
    };

    private ProjectChat Chat(Agent agent, Action<ProjectChat, ChatEntry>? onEntry = null, Func<GateData, GateDecision>? gate = null)
    {
        var chat = new ProjectChat(_project, _store, agent, _store, new FakeGit());
        chat.EntryAdded += e =>
        {
            onEntry?.Invoke(chat, e);
            if (e.Data is GateData { Status: GateStatus.Pending } g)
                chat.ResolveGate(e.Id, (gate ?? (_ => new GateDecision(GateDecisionKind.Approve)))(g));
        };
        return chat;
    }

    private static readonly WorkflowDefinition Default = DefaultWorkflows.PlanDevelopReviewRefine();

    [Fact]
    public async Task Run_again_from_a_stage_reuses_earlier_results()
    {
        var agent = new Agent();
        var chat = Chat(agent);
        await chat.RunWorkflowAsync(Default, "task");
        var runId = chat.LatestRun()!.Id;
        var planText = _store.GetStageRuns(runId).Single(s => s.StageKey == "plan").ResultText!;

        var status = await chat.ResumeWorkflowAsync(runId, ResumeMode.RunFrom, "review");

        Assert.Equal(RunStatus.Completed, status);
        Assert.Equal(["plan", "develop", "review", "review"], agent.Requests.Select(Stage));
        Assert.Contains(planText, agent.Requests[^1].Prompt);
        Assert.Equal(2, _store.GetStageRuns(runId).Count(s => s.StageKey == "review"));
        Assert.Contains(chat.LoadHistory().Select(e => e.Data).OfType<RunStatusData>(), r => r.Status == "resumed" && r.Text.Contains("from Review"));
    }

    [Fact]
    public async Task A_failed_stage_can_be_retried()
    {
        var agent = new Agent { FailModel = "claude-opus-5-5" };
        var chat = Chat(agent);
        Assert.Equal(RunStatus.Failed, await chat.RunWorkflowAsync(Default, "task"));
        var failed = chat.LatestRun()!;
        Assert.Equal("develop", failed.CurrentStageKey);

        agent.FailModel = null;
        var status = await chat.ResumeWorkflowAsync(failed.Id, ResumeMode.RunFrom, failed.CurrentStageKey!);

        Assert.Equal(RunStatus.Completed, status);
        Assert.Equal(["plan", "develop", "develop", "review"], agent.Requests.Select(Stage));
        var develops = _store.GetStageRuns(failed.Id).Where(s => s.StageKey == "develop").ToList();
        Assert.Equal([StageRunStatus.Failed, StageRunStatus.Succeeded], develops.Select(d => d.Status));
        Assert.Equal([1, 2], develops.Select(d => d.Attempt));
    }

    [Fact]
    public async Task A_failed_stage_can_be_skipped()
    {
        var agent = new Agent { FailModel = "claude-opus-5-5" };
        var chat = Chat(agent);
        await chat.RunWorkflowAsync(Default, "task");
        var failed = chat.LatestRun()!;

        agent.FailModel = null;
        var status = await chat.ResumeWorkflowAsync(failed.Id, ResumeMode.Skip, "develop");

        Assert.Equal(RunStatus.Completed, status);
        Assert.Equal(["plan", "develop", "review"], agent.Requests.Select(Stage));
        Assert.Contains(_store.GetStageRuns(failed.Id), s => s.StageKey == "develop" && s.Status == StageRunStatus.Skipped);
    }

    /// <summary>Records exactly what the app leaves behind when it is killed during a stage.</summary>
    private RunRecord SimulateCrash(StageRunStatus planStatus, StageRunStatus? developStatus)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new RunRecord("run-1", _project.Id, Default.Name, WorkflowJson.Serialize(Default), "Add login",
            planStatus == StageRunStatus.AwaitingApproval ? RunStatus.AwaitingApproval : RunStatus.Running,
            "abc123", developStatus is null ? "plan" : "develop", now.AddMinutes(-5), null,
            JsonSerializer.Serialize(new RunState(["keep it small"], [@"C:\proj\Login.cs"]), WorkflowJson.Options));
        _store.SaveRun(run);
        _store.SaveStageRun(new StageRunRecord("s1", run.Id, "plan", "Plan", 1, planStatus, "claude-fable-5-2", "high", "plan-session",
            "THE SAVED PLAN", null, 0.4m, 10, 10, 1000, now.AddMinutes(-5), planStatus == StageRunStatus.AwaitingApproval ? now.AddMinutes(-4) : now.AddMinutes(-4)));
        if (developStatus is { } ds)
            _store.SaveStageRun(new StageRunRecord("s2", run.Id, "develop", "Develop", 1, ds, "claude-opus-5-5", "medium", "develop-session",
                null, null, null, null, null, null, now.AddMinutes(-3), null));
        return run;
    }

    [Fact]
    public async Task After_a_crash_mid_stage_the_interrupted_session_continues()
    {
        var run = SimulateCrash(StageRunStatus.Succeeded, StageRunStatus.Running);
        var agent = new Agent();
        var chat = Chat(agent);

        var interrupted = chat.FindInterruptedRun();
        Assert.Equal(run.Id, interrupted!.Id);

        var status = await chat.ResumeWorkflowAsync(run.Id, ResumeMode.Recover, interrupted.CurrentStageKey!);

        Assert.Equal(RunStatus.Completed, status);
        Assert.Equal(["develop", "review"], agent.Requests.Select(Stage));
        var develop = agent.Requests[0];
        Assert.Equal("develop-session", Assert.IsType<SessionSpec.Resume>(develop.Session).SessionId);
        Assert.Contains("interrupted", develop.Prompt);
        Assert.Contains("THE SAVED PLAN", develop.Prompt);
        Assert.Contains("keep it small", develop.Prompt);          // notes survived
        Assert.Contains(@"C:\proj\Login.cs", agent.Requests[1].Prompt); // edited files survived
        var develops = _store.GetStageRuns(run.Id).Where(s => s.StageKey == "develop").ToList();
        Assert.Equal([StageRunStatus.Cancelled, StageRunStatus.Succeeded], develops.Select(d => d.Status));
        Assert.Null(chat.FindInterruptedRun());
    }

    [Fact]
    public async Task After_a_crash_at_the_gate_the_gate_comes_back_without_rerunning_the_stage()
    {
        var run = SimulateCrash(StageRunStatus.AwaitingApproval, null);
        var agent = new Agent();
        GateData? shown = null;
        var chat = Chat(agent, gate: g => { shown = g; return new GateDecision(GateDecisionKind.Approve); });

        var status = await chat.ResumeWorkflowAsync(run.Id, ResumeMode.Recover, "plan");

        Assert.Equal(RunStatus.Completed, status);
        Assert.Equal("THE SAVED PLAN", shown!.Text);
        Assert.Equal(["develop", "review"], agent.Requests.Select(Stage));
        Assert.Contains("THE SAVED PLAN", agent.Requests[0].Prompt);
    }

    [Fact]
    public void Discarding_an_interrupted_run_closes_it()
    {
        var run = SimulateCrash(StageRunStatus.Succeeded, StageRunStatus.Running);
        var chat = Chat(new Agent());

        chat.DiscardRun(run.Id);

        Assert.Null(chat.FindInterruptedRun());
        Assert.All(_store.GetStageRuns(run.Id), s => Assert.NotEqual(StageRunStatus.Running, s.Status));
    }

    [Fact]
    public async Task A_message_typed_while_a_stage_works_becomes_its_next_turn()
    {
        var agent = new Agent();
        var chat = Chat(agent, (c, e) =>
        {
            if (e.Data is StageDividerData { StageKey: "develop" }) c.Enqueue("Also add XML docs.");
        });

        await chat.RunWorkflowAsync(Default, "task");

        Assert.Equal(["plan", "develop", "develop", "review"], agent.Requests.Select(Stage));
        var extra = agent.Requests[2];
        Assert.Equal("Also add XML docs.", extra.Prompt);
        Assert.IsType<SessionSpec.Resume>(extra.Session);
        Assert.Contains(chat.LoadHistory().Select(e => e.Data).OfType<UserMessageData>(), u => u.QueuedFor == "Develop");
        // The develop stage's result is the extra turn's answer, handed to the reviewer as usual.
        Assert.Single(_store.GetStageRuns(chat.LatestRun()!.Id), s => s.StageKey == "develop");
    }

    [Fact]
    public async Task A_message_typed_at_the_gate_becomes_a_note_for_the_next_stage()
    {
        var agent = new Agent();
        ProjectChat? self = null;
        var chat = Chat(agent, gate: g =>
        {
            self!.Enqueue("Use the existing logger.");
            return new GateDecision(GateDecisionKind.Approve);
        });
        self = chat;

        await chat.RunWorkflowAsync(Default, "task");

        Assert.Contains("Use the existing logger.", agent.Requests[1].Prompt);
        Assert.Contains(chat.LoadHistory().Select(e => e.Data).OfType<UserMessageData>(), u => u.QueuedFor == "notes");
    }

    [Fact]
    public async Task Direct_chat_sends_queued_messages_after_the_turn_in_the_same_session()
    {
        var agent = new Agent();
        var chat = Chat(agent, (c, e) =>
        {
            if (e.Data is UserMessageData { QueuedFor: null, Text: "first" }) c.Enqueue("second");
        });

        await chat.SendAsync("first", new DirectChatOptions("claude-haiku-5-5", null, "plan"));

        Assert.Equal(["first", "second"], agent.Requests.Select(r => r.Prompt));
        Assert.IsType<SessionSpec.Resume>(agent.Requests[1].Session);
    }

    [Fact]
    public async Task The_finish_card_carries_the_run_totals()
    {
        var chat = Chat(new Agent());
        await chat.RunWorkflowAsync(Default, "task");

        var done = chat.LoadHistory().Select(e => e.Data).OfType<RunStatusData>().Last();
        Assert.Equal("completed", done.Status);
        Assert.Contains("3 stage turns", done.Text);
        Assert.Contains("45 tokens", done.Text);
        Assert.Contains("est. $0.03", done.Text.Replace(',', '.'));
    }
}
