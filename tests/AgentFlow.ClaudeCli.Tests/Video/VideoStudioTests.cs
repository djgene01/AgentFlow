using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AgentFlow.Core.Agents;
using AgentFlow.Core.Chat;
using AgentFlow.Core.Video;
using AgentFlow.Storage;
using Xunit;

namespace AgentFlow.ClaudeCli.Tests.Video;

public sealed class VideoStudioTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("agentflow-video-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly McpServerSpec Studio =
        new(VideoStudio.ServerName, @"C:\node\node.exe", [@"C:\studio\mcp\server.js"], new Dictionary<string, string> { ["VMCP_PORT"] = "4600" });

    [Fact]
    public void Merge_adds_servers_next_to_the_permission_server()
    {
        const string permission = """{"mcpServers":{"agentflow":{"type":"http","url":"http://127.0.0.1:1/mcp"}}}""";
        var json = JsonNode.Parse(McpConfig.Merge(permission, [Studio]))!["mcpServers"]!;

        Assert.Equal("http", json["agentflow"]!["type"]!.GetValue<string>());
        Assert.Equal("stdio", json["vmcp-studio"]!["type"]!.GetValue<string>());
        Assert.Equal(@"C:\node\node.exe", json["vmcp-studio"]!["command"]!.GetValue<string>());
        Assert.Equal(@"C:\studio\mcp\server.js", json["vmcp-studio"]!["args"]![0]!.GetValue<string>());
        Assert.Equal("4600", json["vmcp-studio"]!["env"]!["VMCP_PORT"]!.GetValue<string>());

        // Without approvals there is no base config; an extra server never replaces an existing one.
        Assert.NotNull(JsonNode.Parse(McpConfig.Merge(null, [Studio]))!["mcpServers"]!["vmcp-studio"]);
        var renamed = Studio with { Name = "agentflow" };
        Assert.Equal("http", JsonNode.Parse(McpConfig.Merge(permission, [renamed]))!["mcpServers"]!["agentflow"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Arguments_carry_the_config_with_or_without_approvals()
    {
        var r = new StageTurnRequest { WorkingDirectory = "C:\\p", Prompt = "hi" };
        var noApprovals = ClaudeArgumentBuilder.Build(r, mcpConfigFile: "cfg.json").ToList();
        Assert.Equal("cfg.json", noApprovals[noApprovals.IndexOf("--mcp-config") + 1]);
        Assert.DoesNotContain("--permission-prompt-tool", noApprovals);
        Assert.DoesNotContain("--strict-mcp-config", noApprovals);

        var withApprovals = ClaudeArgumentBuilder.Build(r, mcpConfigFile: "cfg.json", promptTool: "mcp__agentflow__approval_prompt").ToList();
        Assert.Equal("mcp__agentflow__approval_prompt", withApprovals[withApprovals.IndexOf("--permission-prompt-tool") + 1]);

        Assert.DoesNotContain("--mcp-config", ClaudeArgumentBuilder.Build(r));
    }

    private sealed class Integrations(bool on) : IMcpIntegrations
    {
        public IReadOnlyList<McpServerSpec> Servers() => on ? [Studio] : [];
        public IReadOnlyList<string> AllowedTools() => [VideoStudio.ToolRule("render_video")];
        public bool MustAsk(string toolName) => toolName == VideoStudio.ToolRule("generate_music");
    }

    /// <summary>Records requests; asks once for the given tool when approvals are on.</summary>
    private sealed class Agent(string? askFor = null) : IAgentRunner
    {
        public List<StageTurnRequest> Requests { get; } = [];
        public List<ApprovalAnswer> Answers { get; } = [];

        public async IAsyncEnumerable<AgentEvent> RunTurnAsync(StageTurnRequest r, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(r);
            var sid = Guid.NewGuid().ToString("D");
            yield return new SessionStarted(sid, r.Model, [], [], r.PermissionMode, null);
            if (askFor is not null && r.ApprovalHandler is { } ask)
                Answers.Add(await Task.Run(() => ask(new ApprovalRequest(askFor, System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone(), "t", "{}"), ct)));
            yield return new TurnResult("success", false, "ok", sid, 0.01m, 10, 1, null, null, null, null);
            yield return new ProcessExited(0, false, null, "");
        }
    }

    private ProjectChat Chat(SqliteChatStore store, IAgentRunner agent, IMcpIntegrations? integrations, ApprovalPolicy policy = ApprovalPolicy.Ask)
    {
        var project = new Project("p1", "Demo", _dir, null, null, null, "acceptEdits", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        store.SaveProject(project);
        return new ProjectChat(project, store, agent, store, approvalPolicy: () => policy, integrations: integrations);
    }

    [Fact]
    public async Task Turns_get_the_studio_and_its_safe_tools_only_when_it_is_on()
    {
        using var store = new SqliteChatStore(Path.Combine(_dir, "a.db"));
        var agent = new Agent();
        await Chat(store, agent, new Integrations(on: true)).SendAsync("make a video", new DirectChatOptions(null, null, "default"));
        Assert.Equal(VideoStudio.ServerName, Assert.Single(agent.Requests[0].McpServers).Name);
        Assert.Contains("mcp__vmcp-studio__render_video", agent.Requests[0].AllowedTools);

        var off = new Agent();
        await Chat(store, off, new Integrations(on: false)).SendAsync("hi", new DirectChatOptions(null, null, "default"));
        Assert.Empty(off.Requests[0].McpServers);
        Assert.DoesNotContain("mcp__vmcp-studio__render_video", off.Requests[0].AllowedTools);
    }

    [Fact]
    public void Paid_tools_are_never_allowed_up_front()
    {
        Assert.DoesNotContain("generate_music", VideoStudio.SafeTools);
        Assert.Contains("generate_music", VideoStudio.PaidTools);
        Assert.DoesNotContain(VideoStudio.SafeTools, VideoStudio.PaidTools.Contains);
        Assert.DoesNotContain(VideoStudio.SafeTools, VideoStudio.AskTools.Contains);
        Assert.Contains("import_stock", VideoStudio.AskTools);
        Assert.Contains("search_stock", VideoStudio.SafeTools);
    }

    [Fact]
    public void Detect_explains_what_is_missing()
    {
        Assert.Null(VideoStudio.Detect(_dir, out var problem));
        Assert.Contains("mcp\\server.js", problem);

        Directory.CreateDirectory(Path.Combine(_dir, "mcp"));
        File.WriteAllText(Path.Combine(_dir, "mcp", "server.js"), "");
        Assert.Null(VideoStudio.Detect(_dir, out problem));
        Assert.Contains("npm install", problem);

        File.WriteAllText(Path.Combine(_dir, "node.exe"), ""); // the EXE's unpacked copy carries its own node
        var install = VideoStudio.Detect(_dir, out _);
        Assert.NotNull(install);
        Assert.True(install!.IsBundled);
        var spec = VideoStudio.ToMcpServer(install, 4601, @"D:\lib");
        Assert.Equal("4601", spec.Env!["VMCP_PORT"]);
        Assert.Equal(@"D:\lib", spec.Env["VMCP_DATA"]);
    }

    [Fact]
    public void The_copy_installed_with_AgentFlow_comes_first_and_has_its_own_port_and_library()
    {
        Assert.Equal(new[] { VideoStudio.BuiltInFolder, VideoStudio.BundledFolder }, VideoStudio.Candidates(null));
        Assert.Equal(@"D:\mine", VideoStudio.Candidates(@"D:\mine").First());
        Assert.True(VideoStudio.IsBuiltIn(VideoStudio.BuiltInFolder + Path.DirectorySeparatorChar));
        Assert.False(VideoStudio.IsBuiltIn(VideoStudio.BundledFolder));
        Assert.False(VideoStudio.IsBuiltIn(""));
        Assert.NotEqual(VideoStudio.DefaultPort, VideoStudio.BuiltInPort);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), VideoStudio.BuiltInDataFolder);
    }

    /// <summary>The real studio on this PC, when it is there: starts its MCP server and lists the tools.</summary>
    [Fact]
    public async Task The_installed_studio_answers_over_stdio()
    {
        var folder = Environment.GetEnvironmentVariable("AGENTFLOW_VIDEO_STUDIO")
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Development Projects", "CV-Studio");
        if (VideoStudio.Detect(folder, out _) is not { } install) return; // not installed here

        var r = await VideoStudio.ProbeAsync(VideoStudio.ToMcpServer(install, VideoStudio.DefaultPort, null), install.Folder, TimeSpan.FromSeconds(30));

        Assert.True(r.Ok, r.Error);
        Assert.Equal("vmcp-studio", r.ServerName);
        Assert.Contains("render_video", r.Tools);
        Assert.All(VideoStudio.SafeTools.Concat(VideoStudio.PaidTools).Concat(VideoStudio.AskTools), t => Assert.Contains(t, r.Tools));
        // Every tool the studio offers is classified, so a new one is noticed here.
        Assert.All(r.Tools, t => Assert.True(VideoStudio.SafeTools.Contains(t) || VideoStudio.PaidTools.Contains(t) || VideoStudio.AskTools.Contains(t), $"Unclassified studio tool: {t}"));
        Assert.False(string.IsNullOrWhiteSpace(r.Instructions));
    }
}
