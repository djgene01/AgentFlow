using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentFlow.Core.Agents;
using AgentFlow.Core.Chat;
using AgentFlow.Core.Workflows;
using AgentFlow.Storage;

namespace AgentFlow.ClaudeCli.Tests.Workflows;

/// <summary>Runs workflows through the real ProjectChat and SQLite store, with a scripted agent.</summary>
public class WorkflowEngineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("agentflow-wf-").FullName;
    private readonly SqliteChatStore _store;
    private readonly Project _project;

    public WorkflowEngineTests()
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
        public Task<string?> GetDiffAsync(string folder, string baseCommit, CancellationToken ct = default) =>
            Task.FromResult<string?>("diff --git a/Login.cs b/Login.cs\n+public void Login() {}");
    }

    /// <summary>Answers each turn by model name. The review answers come from a queue of verdicts.</summary>
    private sealed class ScriptedAgent(Queue<string> verdicts) : IAgentRunner
    {
        public List<StageTurnRequest> Requests { get; } = [];
        public string? FailModel { get; set; }

        public async IAsyncEnumerable<AgentEvent> RunTurnAsync(StageTurnRequest r, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(r);
            await Task.Yield();
            var sid = r.Session is SessionSpec.Resume res && !res.Fork ? res.SessionId : Guid.NewGuid().ToString("D");
            yield return new SessionStarted(sid, r.Model, [], [], r.PermissionMode, null);

            if (r.Model == FailModel)
            {
                yield return Result(sid, "error_during_execution", true, "boom", null);
                yield return new ProcessExited(1, false, null, "");
                yield break;
            }

            JsonElement? so = null;
            string text;
            if (r.JsonSchema is not null)
            {
                var v = verdicts.Dequeue();
                so = JsonDocument.Parse(v == "pass"
                    ? """{"verdict":"pass","summary":"Looks good.","findings":[]}"""
                    : """{"verdict":"changes_requested","summary":"One bug.","findings":[{"severity":"major","file":"Login.cs","line":3,"issue":"No null check","fix":"Check for null"}]}""").RootElement.Clone();
                text = so.Value.GetRawText();
            }
            else
            {
                text = $"{r.Model} did its part.";
                yield return new AssistantMessage("m", [new TextBlock(text), new ToolUseBlock("t" + Requests.Count, "Edit",
                    JsonDocument.Parse("""{"file_path":"C:\\proj\\Login.cs"}""").RootElement.Clone())], null, null);
            }

            yield return Result(sid, "success", false, text, so);
            yield return new ProcessExited(0, false, null, "");
        }

        private static TurnResult Result(string sid, string subtype, bool isError, string text, JsonElement? so) =>
            new(subtype, isError, text, sid, 0.01m, 1000, 1, new TokenUsage(10, 20, 0, 0), null, so, null);
    }

    private ProjectChat Chat(ScriptedAgent agent, Func<GateData, GateDecision>? gate = null)
    {
        var chat = new ProjectChat(_project, _store, agent, _store, new FakeGit());
        chat.EntryAdded += e =>
        {
            if (e.Data is GateData { Status: GateStatus.Pending } g)
            {
                var decision = (gate ?? (_ => new GateDecision(GateDecisionKind.Approve)))(g);
                // Answer after the engine has started waiting.
                _ = Task.Run(async () =>
                {
                    while (!chat.IsGatePending(e.Id)) await Task.Delay(5);
                    chat.ResolveGate(e.Id, decision);
                });
            }
        };
        return chat;
    }

    private static string Stage(StageTurnRequest r) => r.Model switch
    {
        "claude-fable-5-2" => "plan",
        "claude-sonnet-5-5" => "review",
        "claude-opus-5-5" when r.Effort == "medium" => "develop",
        "claude-opus-5-5" => "refine",
        _ => "?",
    };

    [Fact]
    public async Task Default_workflow_runs_plan_develop_review_refine_review()
    {
        var agent = new ScriptedAgent(new Queue<string>(["changes_requested", "pass"]));
        var chat = Chat(agent);

        var status = await chat.RunWorkflowAsync(DefaultWorkflows.PlanDevelopReviewRefine(), "Add a login method");

        Assert.Equal(RunStatus.Completed, status);
        Assert.Equal(["plan", "develop", "review", "refine", "review"], agent.Requests.Select(Stage));

        var (plan, develop, review, refine) = (agent.Requests[0], agent.Requests[1], agent.Requests[2], agent.Requests[3]);
        Assert.Equal("plan", plan.PermissionMode);
        Assert.Equal("acceptEdits", develop.PermissionMode);
        Assert.Equal("dontAsk", review.PermissionMode);
        Assert.NotNull(review.JsonSchema);
        Assert.Null(develop.JsonSchema);
        Assert.Contains("Add a login method", plan.Prompt);
        Assert.Contains("claude-fable-5-2 did its part.", develop.Prompt);  // plan result handed on
        Assert.Contains("diff --git a/Login.cs", review.Prompt);           // git diff handed to the reviewer
        Assert.Contains(@"C:\proj\Login.cs", review.Prompt);                 // edited files too
        Assert.Contains("No null check", refine.Prompt);                     // findings handed to the refiner
        Assert.IsType<SessionSpec.New>(review.Session);                      // reviewer starts fresh

        // Refine continues the Develop session.
        var developSession = chat.LoadHistory().Select(e => e.Data).OfType<TurnSummaryData>().ElementAt(1).SessionId;
        Assert.Equal(developSession, Assert.IsType<SessionSpec.Resume>(refine.Session).SessionId);

        var entries = chat.LoadHistory().Select(e => e.Data).ToList();
        Assert.Equal(5, entries.OfType<StageDividerData>().Count());
        Assert.Equal(["changes_requested", "pass"], entries.OfType<VerdictData>().Select(v => v.Verdict));
        Assert.Equal(GateStatus.Approved, Assert.Single(entries.OfType<GateData>()).Status);
        Assert.Equal(["started", "completed"], entries.OfType<RunStatusData>().Select(r => r.Status));

        var run = Assert.Single(_store.GetRuns(_project.Id));
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal("abc123", run.BaseCommit);
        Assert.Equal(5, _store.GetStageRuns(run.Id).Count);
        Assert.All(_store.GetStageRuns(run.Id), s => Assert.Equal(StageRunStatus.Succeeded, s.Status));
    }

    [Fact]
    public async Task At_the_loop_limit_you_can_finish_the_run()
    {
        var agent = new ScriptedAgent(new Queue<string>(["changes_requested", "changes_requested", "changes_requested"]));
        var chat = Chat(agent, g => g.GateKind == GateData.LoopLimitKind ? new GateDecision(GateDecisionKind.Cancel) : new GateDecision(GateDecisionKind.Approve));

        var status = await chat.RunWorkflowAsync(DefaultWorkflows.PlanDevelopReviewRefine(), "task");

        Assert.Equal(RunStatus.Completed, status);
        Assert.Equal(["plan", "develop", "review", "refine", "review", "refine", "review"], agent.Requests.Select(Stage));
        var question = chat.LoadHistory().Select(e => e.Data).OfType<GateData>().Single(g => g.GateKind == GateData.LoopLimitKind);
        Assert.Contains("after 2 rounds", question.Text);
        Assert.Contains("No null check", question.Text);
        Assert.Equal(GateStatus.Cancelled, question.Status);
    }

    [Fact]
    public async Task At_the_loop_limit_you_can_run_one_more_round()
    {
        var agent = new ScriptedAgent(new Queue<string>(["changes_requested", "changes_requested", "changes_requested", "pass"]));
        var chat = Chat(agent); // approves everything, including "one more round"

        var status = await chat.RunWorkflowAsync(DefaultWorkflows.PlanDevelopReviewRefine(), "task");

        Assert.Equal(RunStatus.Completed, status);
        Assert.Equal(["plan", "develop", "review", "refine", "review", "refine", "review", "refine", "review"], agent.Requests.Select(Stage));
    }

    [Fact]
    public async Task Requesting_changes_at_the_gate_revises_the_plan_in_its_session_then_edits_are_used()
    {
        var agent = new ScriptedAgent(new Queue<string>(["pass"]));
        var answers = new Queue<GateDecision>([
            new(GateDecisionKind.RequestChanges, Feedback: "Use OAuth instead."),
            new(GateDecisionKind.Approve, EditedText: "My edited plan.", Feedback: "Keep it small."),
        ]);
        var chat = Chat(agent, _ => answers.Dequeue());

        await chat.RunWorkflowAsync(DefaultWorkflows.PlanDevelopReviewRefine(), "task");

        Assert.Equal(["plan", "plan", "develop", "review"], agent.Requests.Select(Stage));
        var revision = agent.Requests[1];
        Assert.Contains("Use OAuth instead.", revision.Prompt);
        Assert.IsType<SessionSpec.Resume>(revision.Session);
        Assert.Contains("My edited plan.", agent.Requests[2].Prompt);
        Assert.Contains("Keep it small.", agent.Requests[2].Prompt);

        var gates = chat.LoadHistory().Select(e => e.Data).OfType<GateData>().ToList();
        Assert.Equal([GateStatus.ChangesRequested, GateStatus.Approved], gates.Select(g => g.Status));
        Assert.True(gates[1].WasEdited);
    }

    [Fact]
    public async Task A_gate_card_can_be_answered_the_moment_it_appears()
    {
        // Regression: the UI builds the gate card inside EntryAdded and asks IsGatePending right then.
        var agent = new ScriptedAgent(new Queue<string>(["pass"]));
        var chat = new ProjectChat(_project, _store, agent, _store, new FakeGit());
        bool? pendingWhenShown = null;
        chat.EntryAdded += e =>
        {
            if (e.Data is not GateData) return;
            pendingWhenShown = chat.IsGatePending(e.Id);
            chat.ResolveGate(e.Id, new GateDecision(GateDecisionKind.Approve)); // answered synchronously, before the engine awaits
        };

        var status = await chat.RunWorkflowAsync(DefaultWorkflows.PlanDevelopReviewRefine(), "task");

        Assert.True(pendingWhenShown);
        Assert.Equal(RunStatus.Completed, status);
    }

    [Fact]
    public async Task Cancelling_at_the_gate_stops_before_development()
    {
        var agent = new ScriptedAgent(new Queue<string>());
        var chat = Chat(agent, _ => new GateDecision(GateDecisionKind.Cancel));

        var status = await chat.RunWorkflowAsync(DefaultWorkflows.PlanDevelopReviewRefine(), "task");

        Assert.Equal(RunStatus.Cancelled, status);
        Assert.Equal(["plan"], agent.Requests.Select(Stage));
    }

    [Fact]
    public async Task Stop_while_waiting_at_the_gate_cancels_the_run()
    {
        var agent = new ScriptedAgent(new Queue<string>());
        var chat = new ProjectChat(_project, _store, agent, _store, new FakeGit());
        chat.EntryAdded += e =>
        {
            if (e.Data is GateData)
                _ = Task.Run(async () => { while (!chat.IsGatePending(e.Id)) await Task.Delay(5); chat.Stop(); });
        };

        var status = await chat.RunWorkflowAsync(DefaultWorkflows.PlanDevelopReviewRefine(), "task");

        Assert.Equal(RunStatus.Cancelled, status);
        Assert.False(chat.IsRunning);
        Assert.Equal(RunStatus.Cancelled, _store.GetRuns(_project.Id).Single().Status);
    }

    [Fact]
    public async Task A_failing_stage_stops_the_run()
    {
        var agent = new ScriptedAgent(new Queue<string>()) { FailModel = "claude-opus-5-5" };
        var chat = Chat(agent);

        var status = await chat.RunWorkflowAsync(DefaultWorkflows.PlanDevelopReviewRefine(), "task");

        Assert.Equal(RunStatus.Failed, status);
        Assert.Equal(["plan", "develop"], agent.Requests.Select(Stage));
        Assert.Contains(chat.LoadHistory().Select(e => e.Data).OfType<NoticeData>(), n => n.Text.StartsWith("Develop did not finish"));
    }

    [Fact]
    public async Task After_a_run_a_direct_message_continues_the_last_stage_session()
    {
        var agent = new ScriptedAgent(new Queue<string>(["pass"]));
        var chat = Chat(agent);
        await chat.RunWorkflowAsync(DefaultWorkflows.PlanDevelopReviewRefine(), "task");
        var lastStageSession = chat.LoadHistory().Select(e => e.Data).OfType<TurnSummaryData>().Last().SessionId;

        await chat.SendAsync("Thanks, one more tweak", new DirectChatOptions("claude-sonnet-5-5", null, "plan"));

        Assert.Equal(lastStageSession, Assert.IsType<SessionSpec.Resume>(agent.Requests[^1].Session).SessionId);
    }
}
