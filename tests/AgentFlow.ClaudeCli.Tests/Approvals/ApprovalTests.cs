using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFlow.Core.Agents;
using AgentFlow.Core.Chat;
using AgentFlow.Core.Workflows;
using AgentFlow.Permissions;
using AgentFlow.Storage;

namespace AgentFlow.ClaudeCli.Tests.Approvals;

public class ApprovalRuleTests
{
    private static ApprovalRequest Shell(string command, string tool = "PowerShell") =>
        new(tool, JsonDocument.Parse(JsonSerializer.Serialize(new { command })).RootElement.Clone(), "t1", "{}");

    [Theory]
    [InlineData("node --test", "PowerShell(node --test *)")]
    [InlineData("git init my-repo", "PowerShell(git init *)")]
    [InlineData("npm run lint", "PowerShell(npm run *)")]
    [InlineData("dotnet ./tools/x.dll", "PowerShell(dotnet *)")]
    [InlineData("node --test 2>&1 | tail -5", null)]
    [InlineData("cd src && npm test", null)]
    [InlineData("echo $(whoami)", null)]
    public void Shell_suggestions_cover_the_command_but_never_a_compound(string command, string? expected) =>
        Assert.Equal(expected, ApprovalRules.Suggest(Shell(command)));

    [Fact]
    public void Other_tools_suggest_their_name_or_domain()
    {
        var fetch = new ApprovalRequest("WebFetch", JsonDocument.Parse("""{"url":"https://learn.microsoft.com/x"}""").RootElement.Clone(), null, "{}");
        Assert.Equal("WebFetch(domain:learn.microsoft.com)", ApprovalRules.Suggest(fetch));
        Assert.Equal("Write", ApprovalRules.Suggest(new ApprovalRequest("Write", JsonDocument.Parse("{}").RootElement.Clone(), null, "{}")));
        Assert.Null(ApprovalRules.Suggest(new ApprovalRequest("AskUserQuestion", JsonDocument.Parse("{}").RootElement.Clone(), null, "{}")));
    }

    [Fact]
    public void Saved_rules_match_like_the_cli_does()
    {
        Assert.True(ApprovalRules.Matches("PowerShell(node --test *)", Shell("node --test")));
        Assert.True(ApprovalRules.Matches("PowerShell(node --test *)", Shell("node --test test/a.js")));
        Assert.False(ApprovalRules.Matches("PowerShell(node --test *)", Shell("node --testx")));
        Assert.False(ApprovalRules.Matches("PowerShell(node --test *)", Shell("node --test && rm -rf x")));
        Assert.False(ApprovalRules.Matches("PowerShell(node --test *)", Shell("node --test", "Bash")));
        Assert.True(ApprovalRules.Matches("Write", new ApprovalRequest("Write", JsonDocument.Parse("{}").RootElement.Clone(), null, "{}")));
    }
}

/// <summary>The MCP permission server over real HTTP, the way the CLI talks to it.</summary>
public class PermissionServerTests : IAsyncLifetime
{
    private readonly PermissionServer _server = new();
    private readonly HttpClient _http = new();

    public Task InitializeAsync() => _server.StartAsync();

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Post(string token, string json)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _server.Endpoint) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using var res = await _http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static string TokenOf(ApprovalRegistration r) =>
        JsonNode.Parse(r.McpConfigJson)!["mcpServers"]![PermissionServer.ServerName]!["headers"]!["Authorization"]!.GetValue<string>()[7..];

    private const string Call = """
        {"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"approval_prompt","arguments":{"tool_name":"Bash","input":{"command":"git init x"},"tool_use_id":"toolu_1"}}}
        """;

    [Fact]
    public async Task Speaks_the_handshake_and_lists_the_prompt_tool()
    {
        using var reg = _server.Register((_, _) => Task.FromResult(new ApprovalAnswer(ApprovalChoice.AllowOnce)));
        var token = TokenOf(reg);

        var init = await Post(token, """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}""");
        Assert.Equal("2025-11-25", init.Body!["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Accepted, (await Post(token, """{"jsonrpc":"2.0","method":"notifications/initialized"}""")).Status);
        var list = await Post(token, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        Assert.Equal(PermissionServer.ToolName, list.Body!["result"]!["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(PermissionServer.PromptToolName, reg.PromptToolName);
    }

    [Fact]
    public async Task Allow_echoes_the_input_and_deny_carries_the_message()
    {
        ApprovalRequest? seen = null;
        var answer = new ApprovalAnswer(ApprovalChoice.AllowOnce);
        using var reg = _server.Register((r, _) => { seen = r; return Task.FromResult(answer); });
        var token = TokenOf(reg);

        var allow = JsonNode.Parse((await Post(token, Call)).Body!["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal("allow", allow["behavior"]!.GetValue<string>());
        Assert.Equal("git init x", allow["updatedInput"]!["command"]!.GetValue<string>());
        Assert.Equal("Bash", seen!.ToolName);
        Assert.Equal("toolu_1", seen.ToolUseId);

        answer = new ApprovalAnswer(ApprovalChoice.Deny, "No thanks.");
        var deny = JsonNode.Parse((await Post(token, Call)).Body!["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal("deny", deny["behavior"]!.GetValue<string>());
        Assert.Equal("No thanks.", deny["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_or_expired_tokens_are_rejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("nope", Call)).Status);
        var reg = _server.Register((_, _) => Task.FromResult(new ApprovalAnswer(ApprovalChoice.AllowOnce)));
        var token = TokenOf(reg);
        reg.Dispose();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(token, Call)).Status);
    }

    [Fact]
    public async Task A_prompt_still_open_when_the_turn_ends_is_denied()
    {
        var started = new TaskCompletionSource();
        var reg = _server.Register(async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return new ApprovalAnswer(ApprovalChoice.AllowOnce); });
        var token = TokenOf(reg);

        var pending = Post(token, Call);
        await started.Task;
        reg.Dispose();
        var decision = JsonNode.Parse((await pending).Body!["result"]!["content"]![0]!["text"]!.GetValue<string>())!;

        Assert.Equal("deny", decision["behavior"]!.GetValue<string>());
    }
}

/// <summary>The chat side: cards, always-allow rules, and what turns are started with.</summary>
public class ChatApprovalTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("agentflow-approvals-").FullName;
    private readonly SqliteChatStore _store;
    private readonly Project _project;

    public ChatApprovalTests()
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

    /// <summary>Asks for permission once per turn, like the CLI would, and records the answers.</summary>
    private sealed class AskingAgent(string command, string tool = "PowerShell") : IAgentRunner
    {
        public List<StageTurnRequest> Requests { get; } = [];
        public List<ApprovalAnswer> Answers { get; } = [];

        public async IAsyncEnumerable<AgentEvent> RunTurnAsync(StageTurnRequest r, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(r);
            var sid = Guid.NewGuid().ToString("D");
            yield return new SessionStarted(sid, r.Model, [], [], r.PermissionMode, null);
            if (r.ApprovalHandler is { } ask)
            {
                // The permission server calls the handler on another thread, and turns a cancelled wait into a deny.
                var input = JsonDocument.Parse(JsonSerializer.Serialize(new { command })).RootElement.Clone();
                ApprovalAnswer? answer = null;
                try { answer = await Task.Run(() => ask(new ApprovalRequest(tool, input, "toolu_1", "{}"), ct), CancellationToken.None); }
                catch (OperationCanceledException) { }
                if (answer is not null) Answers.Add(answer);
                if (ct.IsCancellationRequested)
                {
                    yield return new ProcessExited(0, true, StopOutcome.ExitedAfterInterrupt, "");
                    yield break;
                }
            }
            yield return new TurnResult("success", false, "ok", sid, 0.01m, 10, 1, null, null, null, null);
            yield return new ProcessExited(0, false, null, "");
        }
    }

    private ProjectChat Chat(IAgentRunner agent, bool approvals, Func<ApprovalData, ApprovalAnswer>? answer = null)
    {
        var chat = new ProjectChat(_project, _store, agent, _store, approvalPolicy: () => approvals ? ApprovalPolicy.Ask : ApprovalPolicy.Deny);
        chat.EntryAdded += e =>
        {
            if (e.Data is ApprovalData { Status: ApprovalStatus.Pending } a)
            {
                Assert.True(chat.IsApprovalPending(e.Id)); // the card can be answered the moment it appears
                chat.ResolveApproval(e.Id, (answer ?? (_ => new ApprovalAnswer(ApprovalChoice.AllowOnce)))(a));
            }
        };
        return chat;
    }

    private static readonly DirectChatOptions Options = new("claude-haiku-5-5", null, "default");

    [Fact]
    public async Task With_approvals_off_turns_get_no_handler()
    {
        var agent = new AskingAgent("node --test");
        await Chat(agent, approvals: false).SendAsync("hi", Options);

        Assert.Null(agent.Requests[0].ApprovalHandler);
        Assert.Empty(agent.Answers);
    }

    [Fact]
    public async Task A_prompt_becomes_a_card_and_your_answer_goes_back()
    {
        var agent = new AskingAgent("node --test");
        var chat = Chat(agent, approvals: true, _ => new ApprovalAnswer(ApprovalChoice.Deny, "Use npm test."));

        await chat.SendAsync("run the tests", Options);

        Assert.Equal(ApprovalChoice.Deny, agent.Answers.Single().Choice);
        Assert.Equal("Use npm test.", agent.Answers[0].Message);
        var card = chat.LoadHistory().Select(e => e.Data).OfType<ApprovalData>().Single();
        Assert.Equal(ApprovalStatus.Denied, card.Status);
        Assert.Equal("node --test", card.Summary);
        Assert.Equal("PowerShell(node --test *)", card.SuggestedRule);
    }

    [Fact]
    public async Task Always_allow_saves_a_rule_that_later_turns_receive_and_that_skips_the_card()
    {
        var agent = new AskingAgent("node --test test/math.js");
        var cards = 0;
        var chat = Chat(agent, approvals: true, _ => { cards++; return new ApprovalAnswer(ApprovalChoice.AlwaysAllow); });

        await chat.SendAsync("one", Options);
        await chat.SendAsync("two", Options);

        Assert.Equal(["PowerShell(node --test *)"], chat.AllowRules);
        Assert.Contains("PowerShell(node --test *)", agent.Requests[1].AllowedTools);
        Assert.Equal(1, cards); // the second prompt matched the saved rule
        Assert.All(agent.Answers, a => Assert.NotEqual(ApprovalChoice.Deny, a.Choice));

        chat.RemoveAllowRule("PowerShell(node --test *)");
        Assert.Empty(chat.AllowRules);
    }

    [Fact]
    public async Task Clarifying_questions_are_redirected_to_plain_text()
    {
        var agent = new AskingAgent("", tool: "AskUserQuestion");
        var chat = Chat(agent, approvals: true);

        await chat.SendAsync("hi", Options);

        Assert.Equal(ApprovalChoice.Deny, agent.Answers.Single().Choice);
        Assert.Contains("normal text", agent.Answers[0].Message);
        Assert.Empty(chat.LoadHistory().Select(e => e.Data).OfType<ApprovalData>());
    }

    [Fact]
    public async Task Stopping_while_a_card_waits_denies_it_and_marks_it_unanswered()
    {
        var agent = new AskingAgent("node --test");
        var chat = new ProjectChat(_project, _store, agent, _store, approvalPolicy: () => ApprovalPolicy.Ask);
        chat.EntryAdded += e => { if (e.Data is ApprovalData) _ = Task.Run(chat.Stop); };

        await chat.SendAsync("hi", Options);

        Assert.Empty(agent.Answers); // the handler was cancelled, so the server answers with a deny
        Assert.Equal(ApprovalStatus.NotAnswered, chat.LoadHistory().Select(e => e.Data).OfType<ApprovalData>().Single().Status);
    }

    [Fact]
    public async Task Unattended_workflows_allow_prompts_without_a_card_to_answer_but_still_record_them()
    {
        var agent = new AskingAgent("npm install");
        var chat = new ProjectChat(_project, _store, agent, _store, new NoGit(), approvalPolicy: () => ApprovalPolicy.AllowInWorkflows);
        var waited = false;
        chat.EntryAdded += e => { if (e.Data is ApprovalData { Status: ApprovalStatus.Pending }) waited = true; };
        var oneStage = new WorkflowDefinition
        {
            Name = "One",
            Stages = [new StageDefinition { Key = "do", Name = "Do", Model = "claude-haiku-5-5", PermissionPreset = PermissionPreset.Edit, PromptTemplate = "{{task}}" }],
        };

        var status = await chat.RunWorkflowAsync(oneStage, "install deps");

        Assert.Equal(RunStatus.Completed, status);
        Assert.False(waited);
        Assert.Equal(ApprovalChoice.AllowOnce, agent.Answers.Single().Choice);
        var card = chat.LoadHistory().Select(e => e.Data).OfType<ApprovalData>().Single();
        Assert.Equal(ApprovalStatus.AutoAllowed, card.Status);
        Assert.Equal("Do", card.StageName);
        Assert.Empty(chat.AllowRules); // nothing is saved as an always-allow rule
    }

    [Fact]
    public async Task Unattended_setting_still_asks_in_direct_chat()
    {
        var agent = new AskingAgent("npm install");
        var cards = 0;
        var chat = new ProjectChat(_project, _store, agent, _store, approvalPolicy: () => ApprovalPolicy.AllowInWorkflows);
        chat.EntryAdded += e =>
        {
            if (e.Data is ApprovalData { Status: ApprovalStatus.Pending }) { cards++; chat.ResolveApproval(e.Id, new ApprovalAnswer(ApprovalChoice.Deny)); }
        };

        await chat.SendAsync("install deps", Options);

        Assert.Equal(1, cards);
        Assert.Equal(ApprovalChoice.Deny, agent.Answers.Single().Choice);
    }

    [Fact]
    public async Task Allow_everywhere_allows_direct_chat_prompts_and_records_them()
    {
        var agent = new AskingAgent("npm install");
        var chat = new ProjectChat(_project, _store, agent, _store, approvalPolicy: () => ApprovalPolicy.AllowEverywhere);
        var waited = false;
        chat.EntryAdded += e => { if (e.Data is ApprovalData { Status: ApprovalStatus.Pending }) waited = true; };

        await chat.SendAsync("install deps", Options);

        Assert.False(waited);
        Assert.Equal(ApprovalChoice.AllowOnce, agent.Answers.Single().Choice);
        Assert.Equal(ApprovalStatus.AutoAllowed, chat.LoadHistory().Select(e => e.Data).OfType<ApprovalData>().Single().Status);
        Assert.Empty(chat.AllowRules);
        Assert.True(chat.AllowsUnattended); // workflows too
    }

    [Fact]
    public void Unattended_keeps_read_only_stages_read_only()
    {
        // Prompts reach the app, but the reviewer's edit deny rules and the planner's plan mode are applied by the CLI first.
        var review = PermissionPresets.Resolve(StageTemplates.Reviewer(), askUser: true);
        Assert.Contains("Edit", review.Disallowed);
        Assert.Contains("Write", review.Disallowed);
        Assert.Equal("plan", PermissionPresets.Resolve(StageTemplates.Planner(), askUser: true).Mode);
    }

    private sealed class NoGit : IGitProbe
    {
        public Task<string?> GetHeadAsync(string folder, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetDiffAsync(string folder, string baseCommit, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    [Fact]
    public void With_approvals_the_reviewer_preset_asks_instead_of_denying()
    {
        var reviewer = StageTemplates.Reviewer();
        Assert.Equal("dontAsk", PermissionPresets.Resolve(reviewer, askUser: false).Mode);
        Assert.Equal("default", PermissionPresets.Resolve(reviewer, askUser: true).Mode);
        Assert.Contains("Edit", PermissionPresets.Resolve(reviewer, askUser: true).Disallowed); // still read only
    }
}
