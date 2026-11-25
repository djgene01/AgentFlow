namespace AgentFlow.Core.Workflows;

/// <summary>
/// The fixed v1 presets. Commands outside a preset are denied and reported in the chat;
/// a stage can add its own rules with allowedTools. In-chat approvals arrive in phase 5.
/// </summary>
public static class PermissionPresets
{
    /// <summary>Build, test, run and git read commands. Each is allowed in both shells: on Windows Claude
    /// Code usually runs commands through its PowerShell tool, elsewhere through Bash.
    /// A compound command runs only if every part matches (for example, `| tail` is not allowed).</summary>
    public static readonly IReadOnlyList<string> Commands =
    [
        "git status", "git diff", "git log", "git show",
        "dotnet build", "dotnet test", "dotnet run", "dotnet --version", "dotnet --list-sdks",
        "npm test", "npm run build", "npm run test", "node --test",
    ];

    public static readonly IReadOnlyList<string> InspectCommands =
        Commands.SelectMany(c => new[] { $"Bash({c} *)", $"PowerShell({c} *)" }).ToList();

    /// <param name="askUser">
    /// True when permission prompts reach you in the chat. The read-only-with-commands preset then uses
    /// the default mode, so a command outside its list is asked about instead of denied. Edits stay denied.
    /// </param>
    public static (string Mode, IReadOnlyList<string> Allowed, IReadOnlyList<string> Disallowed) Resolve(StageDefinition s, bool askUser = false)
    {
        var (mode, allowed) = s.PermissionPreset switch
        {
            PermissionPreset.ReadOnly => ("plan", Array.Empty<string>()),
            PermissionPreset.ReadOnlyWithCommands => (askUser ? "default" : "dontAsk", InspectCommands.ToArray()),
            PermissionPreset.Edit => ("acceptEdits", InspectCommands.ToArray()),
            PermissionPreset.Custom => (s.PermissionMode ?? "default", Array.Empty<string>()),
            _ => ("plan", Array.Empty<string>()),
        };

        // Edits are denied for read-only presets even if a custom rule tried to allow them.
        var disallowed = s.PermissionPreset is PermissionPreset.ReadOnly or PermissionPreset.ReadOnlyWithCommands
            ? s.DisallowedTools.Concat(["Edit", "Write", "MultiEdit", "NotebookEdit"]).Distinct().ToList()
            : s.DisallowedTools;

        return (mode, allowed.Concat(s.AllowedTools).Distinct().ToList(), disallowed);
    }

    public static string Describe(PermissionPreset p) => p switch
    {
        PermissionPreset.ReadOnly => "read only",
        PermissionPreset.ReadOnlyWithCommands => "read only + build/test/git",
        PermissionPreset.Edit => "can edit + build/test/git",
        _ => "custom permissions",
    };
}
