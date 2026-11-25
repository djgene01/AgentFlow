using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFlow.Core.Agents;
using AgentFlow.Core.Video;

namespace AgentFlow.Core.Studios;

/// <summary>How AgentFlow reaches a studio's MCP server.</summary>
public enum StudioTransport
{
    /// <summary>AgentFlow starts the studio's stdio MCP program for each turn (SfxForge.Mcp.exe).</summary>
    Stdio,

    /// <summary>The server lives inside the studio's window; AgentFlow starts the app and connects over HTTP (Pixel Art Studio).</summary>
    HttpInApp,
}

/// <summary>
/// A desktop studio with an MCP server that AgentFlow offers to Claude Code: Pixel Art Studio and SfxForge.
/// AgentFlow does not change the studios; it only starts them and points Claude Code at their servers.
/// </summary>
public sealed record StudioApp(
    string Key,
    string DisplayName,
    string ServerName,
    StudioTransport Transport,
    string AppExe,
    string? McpExe,
    string BuiltInFolderName,
    string InstalledFolderName,
    int DefaultPort,
    string? PortVariable,
    IReadOnlyList<string> SafeTools,
    IReadOnlyList<string> AskTools,
    string Blurb)
{
    public string Prefix => $"mcp__{ServerName}__";
    public string ToolRule(string tool) => Prefix + tool;

    /// <summary>The copy installed with AgentFlow (build-installer.ps1 puts it here).</summary>
    public string BuiltInFolder => Path.Combine(AppContext.BaseDirectory, BuiltInFolderName);

    /// <summary>Where the studio's own installer puts it.</summary>
    public string InstalledFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), InstalledFolderName);

    public bool IsBuiltIn(string? folder) => SameFolder(folder, BuiltInFolder);

    /// <summary>Folders to try in order: the chosen one, the copy installed with AgentFlow, then the studio's own install.</summary>
    public IEnumerable<string> Candidates(string? chosen)
    {
        if (!string.IsNullOrWhiteSpace(chosen)) yield return chosen;
        yield return BuiltInFolder;
        yield return InstalledFolder;
    }

    public string McpUrl(int port) => $"http://127.0.0.1:{port}/mcp";

    /// <summary>The MCP server for a turn.</summary>
    public McpServerSpec ToMcpServer(StudioInstall install, int port) => Transport switch
    {
        StudioTransport.HttpInApp => McpServerSpec.Http(ServerName, McpUrl(port)),
        _ => new McpServerSpec(ServerName, install.McpExe!, ["--studio-url", $"http://127.0.0.1:{port}"]),
    };

    /// <summary>Checks a folder. Returns null and a reason when it cannot be used.</summary>
    public StudioInstall? Detect(string? folder, out string problem)
    {
        problem = "";
        if (string.IsNullOrWhiteSpace(folder))
        {
            problem = $"Choose the {DisplayName} folder.";
            return null;
        }
        string full;
        try { full = Path.GetFullPath(folder.Trim().Trim('"')); }
        catch (Exception) { problem = $"Not a folder: {folder}"; return null; }

        var app = Path.Combine(full, AppExe);
        if (!File.Exists(app))
        {
            problem = $"No {AppExe} in {full}. Choose the folder {DisplayName} is installed in.";
            return null;
        }
        string? mcp = null;
        if (McpExe is not null)
        {
            mcp = Path.Combine(full, McpExe);
            if (!File.Exists(mcp))
            {
                problem = $"No {McpExe} (the MCP companion) in {full}. Reinstall {DisplayName} with its MCP companion.";
                return null;
            }
        }
        return new StudioInstall(full, app, mcp);
    }

    /// <summary>The port to use: the chosen one, or the studio's default.</summary>
    public int ParsePort(string? text) => int.TryParse(text, out var p) && p is > 0 and < 65536 ? p : DefaultPort;

    /// <summary>Starts the studio's window. Its MCP server (if it has one inside) listens on <paramref name="port"/>.</summary>
    public void Launch(StudioInstall install, int port)
    {
        var psi = new ProcessStartInfo(install.AppExe) { WorkingDirectory = install.Folder, UseShellExecute = false };
        if (PortVariable is not null) psi.Environment[PortVariable] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Not in AgentFlow's job: the studio stays open when AgentFlow closes, like any app you start.
        Process.Start(psi)?.Dispose();
    }

    private static bool SameFolder(string? a, string b)
    {
        if (string.IsNullOrWhiteSpace(a)) return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a.Trim().Trim('"'))),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }
}

/// <summary>A usable studio folder: the app to start and, for stdio studios, the MCP companion.</summary>
public sealed record StudioInstall(string Folder, string AppExe, string? McpExe);

public static class StudioApps
{
    /// <summary>
    /// Pixel Art Studio: its MCP server runs inside the app window (http://127.0.0.1:47821/mcp), so AgentFlow starts
    /// the app before a turn. Drawing tools are one undo step each in the open sprite (Ctrl+Z works), so they run
    /// without asking; tools that read or write files on disk, or replace the open sprite, ask.
    /// </summary>
    public static StudioApp PixelArtStudio { get; } = new(
        Key: "pixel",
        DisplayName: "Pixel Art Studio",
        ServerName: "pixel-art-studio",
        Transport: StudioTransport.HttpInApp,
        AppExe: "PixelArtStudio.exe",
        McpExe: null,
        BuiltInFolderName: "PixelArtStudio",
        InstalledFolderName: "Pixel Studio",
        DefaultPort: 47821,
        PortVariable: "PIXELARTSTUDIO_MCP_PORT",
        SafeTools:
        [
            "get_state", "draw", "draw_text", "gradient_fill", "outline", "drop_shadow", "paint_rows", "get_pixels",
            "view_frame", "view_animation", "add_frame", "delete_frame", "move_frame", "set_frame_duration",
            "link_cels", "unlink_cel", "set_tag", "remove_tag", "tween_layer", "copy_cel", "shift_cel",
            "add_layer", "duplicate_layer", "delete_layer", "move_layer", "merge_down", "set_layer",
            "resize_canvas", "flip", "rotate", "set_palette", "replace_color",
            "add_color_cycle", "set_color_cycle", "remove_color_cycle", "set_active", "undo", "redo",
        ],
        AskTools: ["new_sprite", "open_file", "save", "export", "add_reference_image"],
        Blurb: "Claude draws sprites and animations live in the Pixel Art Studio window, and every tool call is one undo step.");

    /// <summary>
    /// SfxForge: a stdio companion (SfxForge.Mcp.exe) bridges to the studio window when it is open and synthesizes
    /// headless when it is not, so nothing has to be started first. Sound design and playback run without asking;
    /// writing a .wav file or deleting a preset asks.
    /// </summary>
    public static StudioApp SfxForge { get; } = new(
        Key: "sfx",
        DisplayName: "SfxForge",
        ServerName: "sfxforge",
        Transport: StudioTransport.Stdio,
        AppExe: "SfxForge.exe",
        McpExe: "SfxForge.Mcp.exe",
        BuiltInFolderName: "SfxForge",
        InstalledFolderName: "SfxForge",
        DefaultPort: 5174,
        PortVariable: "SFXFORGE_PORT",
        SafeTools:
        [
            "get_synth_state", "set_synth_parameters", "set_arpeggio", "generate_preset", "mutate_patch", "reset_patch",
            "play_sound", "stop_sound", "list_presets", "load_preset", "save_preset", "launch_studio_ui", "get_parameter_guide",
        ],
        AskTools: ["export_wav", "delete_preset"],
        Blurb: "Claude designs 8-bit sound effects, plays them and exports .wav files. It works with the SfxForge window closed too.");

    public static IReadOnlyList<StudioApp> All { get; } = [PixelArtStudio, SfxForge];

    /// <summary>True when something accepts connections on the loopback port (the studio's server is up).</summary>
    public static async Task<bool> IsListeningAsync(int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await tcp.ConnectAsync("127.0.0.1", port, cts.Token);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Waits for the port to open, checking twice a second.</summary>
    public static async Task<bool> WaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsListeningAsync(port, TimeSpan.FromMilliseconds(400), ct)) return true;
            await Task.Delay(500, ct);
        }
        return false;
    }

    /// <summary>Lists the tools of a Streamable HTTP MCP server: initialize, then tools/list.</summary>
    public static async Task<McpProbeResult> ProbeHttpAsync(string url, TimeSpan timeout, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = timeout };
        string? session = null;
        try
        {
            var init = await PostAsync(1, "initialize", new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "agentflow-probe", version = "1.0" },
            });
            if (init["error"] is { } initError) return Fail($"initialize failed: {initError}");
            await PostAsync(null, "notifications/initialized", null);
            var list = await PostAsync(2, "tools/list", new { });
            if (list["error"] is { } listError) return Fail($"tools/list failed: {listError}");

            var tools = (list["result"]?["tools"] as JsonArray ?? []).Select(t => t?["name"]?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToList();
            var info = init["result"]?["serverInfo"];
            return new McpProbeResult(true, info?["name"]?.GetValue<string>() ?? "", info?["version"]?.GetValue<string>(),
                tools, init["result"]?["instructions"]?.GetValue<string>(), null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return Fail(ex is TaskCanceledException ? $"No answer within {timeout.TotalSeconds:0} seconds." : ex.Message);
        }

        async Task<JsonObject> PostAsync(int? id, string method, object? parameters)
        {
            var body = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
            if (id is { } i) body["id"] = i;
            if (parameters is not null) body["params"] = JsonSerializer.SerializeToNode(parameters);
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (session is not null) req.Headers.Add("Mcp-Session-Id", session);
            using var resp = await http.SendAsync(req, ct);
            if (resp.Headers.TryGetValues("Mcp-Session-Id", out var ids)) session = ids.FirstOrDefault() ?? session;
            if (id is null) return new JsonObject(); // a notification has no reply
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync(ct);
            return ParseReply(text, id.Value) ?? throw new IOException("The server's reply was not understood.");
        }

        static McpProbeResult Fail(string error) => new(false, "", null, [], null, error);
    }

    /// <summary>The JSON-RPC reply with this id, from a plain JSON body or a server-sent-events stream.</summary>
    public static JsonObject? ParseReply(string text, int id)
    {
        IEnumerable<string> candidates = text.TrimStart().StartsWith('{')
            ? [text]
            : text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim());
        foreach (var c in candidates)
        {
            try
            {
                if (JsonNode.Parse(c) is JsonObject o && o["id"] is JsonValue v && v.TryGetValue<int>(out var got) && got == id) return o;
            }
            catch (JsonException) { /* not JSON: skip */ }
        }
        return null;
    }
}
