using System.Text.Json.Nodes;
using AgentFlow.Core.Agents;
using AgentFlow.Core.Studios;
using AgentFlow.Core.Video;

namespace AgentFlow.ClaudeCli.Tests.Studios;

/// <summary>Pixel Art Studio and SfxForge as MCP servers for Claude Code turns.</summary>
public sealed class StudioAppTests
{
    [Fact]
    public void An_http_server_is_written_as_type_http_with_its_url()
    {
        var pixel = StudioApps.PixelArtStudio.ToMcpServer(new StudioInstall(@"C:\x", @"C:\x\PixelArtStudio.exe", null), 47821);
        var json = JsonNode.Parse(McpConfig.Merge(null, [pixel]))!["mcpServers"]!["pixel-art-studio"]!;
        Assert.Equal("http", json["type"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:47821/mcp", json["url"]!.GetValue<string>());
        Assert.Null(json["command"]);
    }

    [Fact]
    public void SfxForge_runs_its_stdio_companion_pointed_at_the_studio_port()
    {
        var sfx = StudioApps.SfxForge.ToMcpServer(new StudioInstall(@"C:\s", @"C:\s\SfxForge.exe", @"C:\s\SfxForge.Mcp.exe"), 5174);
        Assert.False(sfx.IsHttp);
        Assert.Equal(@"C:\s\SfxForge.Mcp.exe", sfx.Command);
        Assert.Equal(["--studio-url", "http://127.0.0.1:5174"], sfx.Args);
    }

    private sealed class Part(bool on, string server, string safe, string paid) : IMcpIntegrations
    {
        public int Prepared;
        public IReadOnlyList<McpServerSpec> Servers() => on ? [McpServerSpec.Http(server, "http://127.0.0.1:1/mcp")] : [];
        public IReadOnlyList<string> AllowedTools() => [safe];
        public bool MustAsk(string toolName) => toolName == paid;
        public Task PrepareAsync(CancellationToken ct) { Prepared++; return server == "broken" ? throw new InvalidOperationException() : Task.CompletedTask; }
    }

    [Fact]
    public async Task Integrations_combine_and_only_allow_tools_of_servers_in_the_turn()
    {
        var video = new Part(true, "vmcp-studio", "mcp__vmcp-studio__render_video", "mcp__vmcp-studio__generate_music");
        var pixel = new Part(false, "pixel-art-studio", "mcp__pixel-art-studio__draw", "");
        var broken = new Part(true, "broken", "mcp__broken__x", "");
        var all = new CompositeMcpIntegrations([video, pixel, broken]);

        await all.PrepareAsync(CancellationToken.None); // one failing part does not stop the others
        Assert.Equal(1, video.Prepared);
        Assert.Equal(1, pixel.Prepared);
        Assert.Equal(["vmcp-studio", "broken"], all.Servers().Select(s => s.Name));
        Assert.Contains("mcp__vmcp-studio__render_video", all.AllowedTools());
        Assert.DoesNotContain("mcp__pixel-art-studio__draw", all.AllowedTools()); // its app is not up: no server, no rule
        Assert.True(all.MustAsk("mcp__vmcp-studio__generate_music"));
        Assert.False(all.MustAsk("mcp__vmcp-studio__render_video"));
    }

    [Fact]
    public void Replies_are_read_from_json_or_an_event_stream()
    {
        Assert.Equal("a", StudioApps.ParseReply("""{"jsonrpc":"2.0","id":1,"result":{"x":"a"}}""", 1)!["result"]!["x"]!.GetValue<string>());
        const string sse = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\"}\n\nevent: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"tools\":[]}}\n\n";
        Assert.NotNull(StudioApps.ParseReply(sse, 2)!["result"]);
        Assert.Null(StudioApps.ParseReply(sse, 3));
    }

    [Fact]
    public void A_folder_needs_the_app_and_for_sfxforge_its_mcp_companion()
    {
        var dir = Directory.CreateTempSubdirectory("agentflow-studio-").FullName;
        try
        {
            Assert.Null(StudioApps.SfxForge.Detect(dir, out var problem));
            Assert.Contains("SfxForge.exe", problem);
            File.WriteAllText(Path.Combine(dir, "SfxForge.exe"), "");
            Assert.Null(StudioApps.SfxForge.Detect(dir, out problem));
            Assert.Contains("SfxForge.Mcp.exe", problem);
            File.WriteAllText(Path.Combine(dir, "SfxForge.Mcp.exe"), "");
            Assert.NotNull(StudioApps.SfxForge.Detect(dir, out _));

            File.WriteAllText(Path.Combine(dir, "PixelArtStudio.exe"), "");
            Assert.NotNull(StudioApps.PixelArtStudio.Detect(dir, out _));
            Assert.Null(StudioApps.PixelArtStudio.Detect("", out _));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void No_tool_is_both_safe_and_asking()
    {
        foreach (var app in StudioApps.All)
        {
            Assert.Empty(app.SafeTools.Intersect(app.AskTools));
            Assert.Equal(app.SafeTools.Count, app.SafeTools.Distinct().Count());
        }
    }

    [Fact]
    public async Task A_closed_port_is_reported_as_not_listening()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        Assert.True(await StudioApps.IsListeningAsync(port, TimeSpan.FromSeconds(2)));
        l.Stop();
        Assert.False(await StudioApps.IsListeningAsync(port, TimeSpan.FromMilliseconds(500)));
    }

    /// <summary>Against a real SfxForge build: set AGENTFLOW_SFXFORGE to its folder (with SfxForge.Mcp.exe).</summary>
    [Fact]
    public async Task Real_sfxforge_companion_lists_exactly_the_classified_tools()
    {
        if (Environment.GetEnvironmentVariable("AGENTFLOW_SFXFORGE") is not { Length: > 0 } folder) return;
        var app = StudioApps.SfxForge;
        var install = app.Detect(folder, out var problem) ?? throw new Xunit.Sdk.XunitException(problem);
        var r = await VideoStudio.ProbeAsync(app.ToMcpServer(install, 5174), install.Folder, TimeSpan.FromSeconds(30));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(app.SafeTools.Concat(app.AskTools).Order(), r.Tools.Order());
    }

    /// <summary>Against a running Pixel Art Studio: set AGENTFLOW_PIXELSTUDIO_PORT to its MCP port.</summary>
    [Fact]
    public async Task Real_pixel_art_studio_lists_exactly_the_classified_tools()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("AGENTFLOW_PIXELSTUDIO_PORT"), out var port)) return;
        var app = StudioApps.PixelArtStudio;
        var r = await StudioApps.ProbeHttpAsync(app.McpUrl(port), TimeSpan.FromSeconds(15));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(app.SafeTools.Concat(app.AskTools).Order(), r.Tools.Order());
    }
}
