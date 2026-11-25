using System.Text.Json;
using System.Text.RegularExpressions;
using AgentFlow.Core.Agents;

namespace AgentFlow.Core.Chat;

/// <summary>
/// "Always allow" rules: what rule to offer for a request, whether a request matches a saved rule,
/// and how the rules are stored per project. Rules use Claude Code's permission rule syntax, so
/// the same strings are passed to later turns as --allowedTools.
/// </summary>
public static partial class ApprovalRules
{
    private static readonly HashSet<string> Shells = new(StringComparer.Ordinal) { "Bash", "PowerShell" };

    /// <summary>Describes the request in one line for the approval card.</summary>
    public static string Describe(ApprovalRequest r) => r.ToolName switch
    {
        "Bash" or "PowerShell" => Str(r.Input, "command") ?? "",
        "Edit" or "Write" or "MultiEdit" or "Read" or "NotebookEdit" => Str(r.Input, "file_path") ?? Str(r.Input, "notebook_path") ?? "",
        "WebFetch" => Str(r.Input, "url") ?? "",
        "WebSearch" => Str(r.Input, "query") ?? "",
        _ => r.Input.ValueKind == JsonValueKind.Object ? r.Input.GetRawText() : "",
    };

    /// <summary>
    /// The rule "Always allow" would save, or null when none should be offered: compound shell
    /// commands (anything with &amp;&amp;, |, ; or redirection) are approved one at a time only.
    /// </summary>
    public static string? Suggest(ApprovalRequest r)
    {
        if (Shells.Contains(r.ToolName))
        {
            var command = (Str(r.Input, "command") ?? "").Trim();
            if (command.Length == 0 || CompoundPattern().IsMatch(command)) return null;
            var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var prefix = words[0];
            // A second word that is a subcommand or flag (git init, node --test) is part of the rule; paths and values are not.
            if (words.Length > 1 && !LooksLikeValue(words[1])) prefix += " " + words[1];
            return $"{r.ToolName}({prefix} *)";
        }
        if (r.ToolName == "WebFetch" && Uri.TryCreate(Str(r.Input, "url"), UriKind.Absolute, out var uri))
            return $"WebFetch(domain:{uri.Host})";
        return r.ToolName is "AskUserQuestion" or "" ? null : r.ToolName;
    }

    /// <summary>True when a saved rule covers the request (used for rules saved after the turn started).</summary>
    public static bool Matches(string rule, ApprovalRequest r)
    {
        var m = RulePattern().Match(rule);
        if (!m.Success) return rule == r.ToolName;
        if (m.Groups[1].Value != r.ToolName) return false;
        var pattern = m.Groups[2].Value;
        if (r.ToolName == "WebFetch" && pattern.StartsWith("domain:", StringComparison.Ordinal))
            return Uri.TryCreate(Str(r.Input, "url"), UriKind.Absolute, out var u) && u.Host.Equals(pattern[7..], StringComparison.OrdinalIgnoreCase);
        if (!Shells.Contains(r.ToolName)) return false;
        var command = (Str(r.Input, "command") ?? "").Trim();
        if (CompoundPattern().IsMatch(command)) return false;
        return pattern.EndsWith(" *", StringComparison.Ordinal)
            ? command == pattern[..^2] || command.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : command.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    public static string SettingKey(string projectId) => "allow-rules:" + projectId;

    public static List<string> Load(IChatStore store, string projectId) =>
        store.GetSetting(SettingKey(projectId)) is { } json ? JsonSerializer.Deserialize<List<string>>(json) ?? [] : [];

    public static void Save(IChatStore store, string projectId, IEnumerable<string> rules) =>
        store.SetSetting(SettingKey(projectId), JsonSerializer.Serialize(rules.Distinct().ToList()));

    private static bool LooksLikeValue(string w) =>
        w.IndexOfAny(['/', '\\', ':', '.', '"', '\'', '$', '=']) >= 0 && !w.StartsWith('-');

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"&&|\|\||[|;&<>`]|\$\(")]
    private static partial Regex CompoundPattern();

    [GeneratedRegex(@"^([A-Za-z_]+)\((.*)\)$")]
    private static partial Regex RulePattern();
}
