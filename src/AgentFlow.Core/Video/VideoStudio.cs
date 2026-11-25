using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFlow.Core.Agents;

namespace AgentFlow.Core.Video;

/// <summary>A VMCP-Studio install AgentFlow can start: the folder, the node to run, the MCP server script.</summary>
public sealed record VideoStudioInstall(string Folder, string Node, string ServerScript, bool IsBundled);

/// <summary>Result of starting the studio's MCP server and listing its tools.</summary>
public sealed record McpProbeResult(bool Ok, string ServerName, string? Version, IReadOnlyList<string> Tools, string? Instructions, string? Error);

/// <summary>
/// The VMCP-Studio video creation tool. AgentFlow does not change it: it starts the studio's own
/// stdio MCP server (mcp/server.js) for each Claude Code turn, so Claude can plan, render and list videos.
/// The MCP server starts the studio's web server itself when a tool needs it.
/// </summary>
public static class VideoStudio
{
    public const string ServerName = "vmcp-studio";
    public const string Prefix = $"mcp__{ServerName}__";
    public const int DefaultPort = 4600;

    /// <summary>Where VMCP-Studio.exe unpacks itself (it carries its own node.exe).</summary>
    public static string BundledFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VMCP-Studio", "app");

    /// <summary>The copy installed with AgentFlow (build-installer.ps1 puts the studio's packaged app here).</summary>
    public static string BuiltInFolder => Path.Combine(AppContext.BaseDirectory, "VideoStudio");

    /// <summary>
    /// The built-in copy's own port, so it never talks to an older VMCP-Studio.exe server left running on 4600.
    /// </summary>
    public const int BuiltInPort = 4610;

    /// <summary>The built-in copy's library: the install folder is read-only, so the studio's data lives here.</summary>
    public static string BuiltInDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentFlow", "VideoStudio");

    public static bool IsBuiltIn(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim().Trim('"'))),
            Path.TrimEndingDirectorySeparator(BuiltInFolder), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tools that only read, plan, search or render locally: allowed without asking.
    /// search_stock only searches Openverse; import_stock downloads files, so it asks like the other imports.
    /// </summary>
    public static IReadOnlyList<string> SafeTools { get; } =
    [
        "get_guide", "start_video", "post_update", "render_video", "get_status", "list_videos", "get_spec",
        "list_assets", "list_voices", "list_music", "list_sfx", "search_stock", "get_music_job",
    ];

    /// <summary>Tools that copy files into the studio or design voices: they go through the normal permission prompt.</summary>
    public static IReadOnlyList<string> AskTools { get; } =
        ["import_asset", "import_music", "import_stock", "create_voice"];

    /// <summary>Costs credits on sunoapi.org: always asks, even in unattended workflow runs.</summary>
    public static IReadOnlyList<string> PaidTools { get; } = ["generate_music"];

    public static string ToolRule(string tool) => Prefix + tool;

    /// <summary>Checks a folder and finds the node to run it with. Returns null and a reason when it cannot be used.</summary>
    public static VideoStudioInstall? Detect(string? folder, out string problem)
    {
        problem = "";
        if (string.IsNullOrWhiteSpace(folder))
        {
            problem = "Choose the VMCP-Studio folder.";
            return null;
        }
        folder = Path.GetFullPath(folder.Trim().Trim('"'));
        var script = Path.Combine(folder, "mcp", "server.js");
        if (!File.Exists(script))
        {
            problem = $"No mcp\\server.js in {folder}. Choose the VMCP-Studio folder (the one with mcp, server and web in it).";
            return null;
        }

        var bundledNode = Path.Combine(folder, "node.exe");
        if (File.Exists(bundledNode))
            return new VideoStudioInstall(folder, bundledNode, script, IsBundled: true);

        if (!Directory.Exists(Path.Combine(folder, "node_modules", "@modelcontextprotocol")))
        {
            problem = $"The studio's packages are not installed. Run \"npm install\" in {folder}.";
            return null;
        }
        var node = FindOnPath("node.exe");
        if (node is null)
        {
            problem = "Node.js is not installed (node.exe is not on PATH). Install Node.js, or use the folder VMCP-Studio.exe unpacks to.";
            return null;
        }
        return new VideoStudioInstall(folder, node, script, IsBundled: false);
    }

    /// <summary>Folders to try in order: the chosen one, the copy installed with AgentFlow, then the EXE's unpacked copy.</summary>
    public static IEnumerable<string> Candidates(string? chosen)
    {
        if (!string.IsNullOrWhiteSpace(chosen)) yield return chosen;
        yield return BuiltInFolder;
        yield return BundledFolder;
    }

    /// <param name="dataFolder">The studio's library folder (VMCP_DATA); null keeps the studio's default.</param>
    public static McpServerSpec ToMcpServer(VideoStudioInstall install, int port, string? dataFolder)
    {
        var env = new Dictionary<string, string> { ["VMCP_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(dataFolder)) env["VMCP_DATA"] = dataFolder;
        return new McpServerSpec(ServerName, install.Node, [install.ServerScript], env);
    }

    /// <summary>Starts the MCP server, sends initialize and tools/list over stdio, and stops it again.</summary>
    public static async Task<McpProbeResult> ProbeAsync(McpServerSpec server, string workingDirectory, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(server.Command)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in server.Args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in server.Env ?? new Dictionary<string, string>()) psi.Environment[k] = v;

        using var cts = new CancellationTokenSource(timeout);
        using var p = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        try
        {
            p.Start();
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
            p.BeginErrorReadLine();

            async Task SendAsync(object msg)
            {
                await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(msg).AsMemory(), cts.Token);
                await p.StandardInput.FlushAsync(cts.Token);
            }

            async Task<JsonObject> ReplyAsync(int id)
            {
                while (await p.StandardOutput.ReadLineAsync(cts.Token) is { } line)
                {
                    if (JsonNode.Parse(line) is JsonObject o && o["id"]?.GetValue<int>() == id) return o;
                }
                throw new IOException("The MCP server closed without answering.");
            }

            await SendAsync(new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "agentflow-probe", version = "1.0" } },
            });
            var init = await ReplyAsync(1);
            if (init["error"] is { } initError) return Fail($"initialize failed: {initError}");
            await SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
            await SendAsync(new { jsonrpc = "2.0", id = 2, method = "tools/list" });
            var list = await ReplyAsync(2);
            if (list["error"] is { } listError) return Fail($"tools/list failed: {listError}");

            var tools = (list["result"]?["tools"] as JsonArray ?? []).Select(t => t?["name"]?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToList();
            var info = init["result"]?["serverInfo"];
            return new McpProbeResult(true, info?["name"]?.GetValue<string>() ?? "", info?["version"]?.GetValue<string>(),
                tools, init["result"]?["instructions"]?.GetValue<string>(), null);
        }
        catch (OperationCanceledException)
        {
            return Fail($"The MCP server did not answer within {timeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }

        McpProbeResult Fail(string error)
        {
            string err;
            lock (stderr) err = stderr.ToString().Trim();
            return new McpProbeResult(false, "", null, [], null, err.Length > 0 ? $"{error}\n{err}" : error);
        }
    }

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(d => { try { return Path.Combine(d.Trim('"'), exe); } catch { return null; } })
        .FirstOrDefault(p => p is not null && File.Exists(p));
}
