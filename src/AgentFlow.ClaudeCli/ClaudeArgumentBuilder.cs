using System.Globalization;
using AgentFlow.Core.Agents;

namespace AgentFlow.ClaudeCli;

/// <summary>
/// Builds the argument list for one `claude -p` turn. Only documented CLI flags are used.
/// The prompt is not an argument: the runner writes it to stdin.
/// </summary>
public static class ClaudeArgumentBuilder
{
    private static readonly HashSet<string> AllowedPermissionModes =
        new(StringComparer.Ordinal) { "default", "manual", "acceptEdits", "plan", "auto", "dontAsk" };

    private static readonly HashSet<string> AllowedEfforts =
        new(StringComparer.Ordinal) { "low", "medium", "high", "xhigh", "max" };

    /// <param name="systemPromptFile">Path of a file holding <see cref="StageTurnRequest.AppendSystemPrompt"/>, if any.</param>
    /// <param name="mcpConfigFile">Per-turn MCP config that points at the app's permission server, if approvals are on.</param>
    /// <param name="promptTool">The permission prompt tool name, if approvals are on.</param>
    public static IReadOnlyList<string> Build(StageTurnRequest r, string? systemPromptFile = null, string? mcpConfigFile = null, string? promptTool = null)
    {
        var a = new List<string> { "-p", "--output-format", "stream-json", "--verbose" };

        if (r.IncludePartialMessages)
            a.Add("--include-partial-messages");

        if (!string.IsNullOrWhiteSpace(r.Model))
            a.AddRange(["--model", r.Model]);

        if (!string.IsNullOrWhiteSpace(r.Effort))
        {
            if (!AllowedEfforts.Contains(r.Effort))
                throw new ArgumentException($"Unknown effort '{r.Effort}'.", nameof(r));
            a.AddRange(["--effort", r.Effort]);
        }

        if (!string.IsNullOrWhiteSpace(r.PermissionMode))
        {
            if (!AllowedPermissionModes.Contains(r.PermissionMode))
                throw new ArgumentException(
                    $"Permission mode '{r.PermissionMode}' is not allowed. bypassPermissions is never offered.", nameof(r));
            a.AddRange(["--permission-mode", r.PermissionMode]);
        }

        if (r.AllowedTools.Count > 0)
            a.AddRange(["--allowedTools", string.Join(",", r.AllowedTools)]);

        if (r.DisallowedTools.Count > 0)
            a.AddRange(["--disallowedTools", string.Join(",", r.DisallowedTools)]);

        switch (r.Session)
        {
            case SessionSpec.New n:
                a.AddRange(["--session-id", n.SessionId.ToString("D")]);
                break;
            case SessionSpec.Resume res:
                a.AddRange(["--resume", res.SessionId]);
                if (res.Fork) a.Add("--fork-session");
                break;
        }

        if (systemPromptFile is not null)
            a.AddRange(["--append-system-prompt-file", systemPromptFile]);

        if (r.MaxTurns is { } turns)
            a.AddRange(["--max-turns", turns.ToString(CultureInfo.InvariantCulture)]);

        if (r.MaxBudgetUsd is { } budget)
            a.AddRange(["--max-budget-usd", budget.ToString(CultureInfo.InvariantCulture)]);

        if (!string.IsNullOrWhiteSpace(r.JsonSchema))
            a.AddRange(["--json-schema", r.JsonSchema]);

        // The app's MCP servers: the permission server (so the CLI asks the app instead of denying)
        // and any integrations. The project's own MCP servers still load; these are added.
        if (mcpConfigFile is not null)
            a.AddRange(["--mcp-config", mcpConfigFile]);
        if (mcpConfigFile is not null && promptTool is not null)
            a.AddRange(["--permission-prompt-tool", promptTool]);

        return a;
    }
}
