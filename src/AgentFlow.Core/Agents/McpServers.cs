using System.Text.Json.Nodes;

namespace AgentFlow.Core.Agents;

/// <summary>
/// An MCP server the app adds to a turn with --mcp-config: a stdio server (Command + Args) or, when
/// <see cref="Url"/> is set, a Streamable HTTP server that is already running (Command is then unused).
/// </summary>
public sealed record McpServerSpec(string Name, string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Env = null, string? Url = null)
{
    public static McpServerSpec Http(string name, string url) => new(name, "", [], null, url);

    public bool IsHttp => !string.IsNullOrWhiteSpace(Url);
}

/// <summary>MCP servers and tool rules that AgentFlow adds to every turn (for example the video studio).</summary>
public interface IMcpIntegrations
{
    /// <summary>Servers to add to this turn.</summary>
    IReadOnlyList<McpServerSpec> Servers();

    /// <summary>Tool rules allowed without asking (passed with --allowedTools).</summary>
    IReadOnlyList<string> AllowedTools();

    /// <summary>True for tools that must always ask you, even in unattended workflow runs (for example ones that cost money).</summary>
    bool MustAsk(string toolName);

    /// <summary>
    /// Called before each turn, before <see cref="Servers"/>: an integration whose server lives inside a desktop app
    /// can start that app here. It must not throw; a problem only means the server is left out of the turn.
    /// </summary>
    Task PrepareAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Several integrations as one (the video studio, Pixel Art Studio, SfxForge).</summary>
public sealed class CompositeMcpIntegrations(IEnumerable<IMcpIntegrations> parts) : IMcpIntegrations
{
    private readonly IReadOnlyList<IMcpIntegrations> _parts = parts.ToList();

    public IReadOnlyList<McpServerSpec> Servers() => _parts.SelectMany(p => p.Servers()).ToList();

    // Only the allow rules of integrations that add a server this turn.
    public IReadOnlyList<string> AllowedTools() =>
        _parts.Where(p => p.Servers().Count > 0).SelectMany(p => p.AllowedTools()).Distinct().ToList();

    public bool MustAsk(string toolName) => _parts.Any(p => p.MustAsk(toolName));

    public Task PrepareAsync(CancellationToken ct) => Task.WhenAll(_parts.Select(async p =>
    {
        try { await p.PrepareAsync(ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { /* that integration is left out of this turn */ }
    }));
}

public static class McpConfig
{
    /// <summary>
    /// One --mcp-config JSON: the permission server's config (if any) plus the extra servers.
    /// An extra server never replaces one already in the base config.
    /// </summary>
    public static string Merge(string? baseJson, IEnumerable<McpServerSpec> servers)
    {
        var root = (string.IsNullOrWhiteSpace(baseJson) ? null : JsonNode.Parse(baseJson) as JsonObject) ?? new JsonObject();
        if (root["mcpServers"] is not JsonObject list)
            root["mcpServers"] = list = new JsonObject();

        foreach (var s in servers)
        {
            if (list.ContainsKey(s.Name)) continue;
            if (s.IsHttp)
            {
                list[s.Name] = new JsonObject { ["type"] = "http", ["url"] = s.Url };
                continue;
            }
            var entry = new JsonObject
            {
                ["type"] = "stdio",
                ["command"] = s.Command,
                ["args"] = new JsonArray(s.Args.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
            };
            if (s.Env is { Count: > 0 } env)
                entry["env"] = new JsonObject(env.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));
            list[s.Name] = entry;
        }
        return root.ToJsonString();
    }
}
