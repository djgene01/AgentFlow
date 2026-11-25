using System.Text.RegularExpressions;

namespace AgentFlow.Core.Workflows;

/// <summary>
/// Fills {{variables}} in a stage prompt. Known variables: task, notes, changedFiles,
/// git.diff, git.baseCommit, stages.&lt;key&gt;.result and stages.&lt;key&gt;.findings.
/// A variable with no value yet becomes "(none)", so prompts never contain raw braces.
/// </summary>
public static partial class PromptTemplate
{
    public static readonly IReadOnlySet<string> KnownVariables =
        new HashSet<string>(StringComparer.Ordinal) { "task", "notes", "changedFiles", "git.diff", "git.baseCommit" };

    public static IEnumerable<string> Variables(string template) =>
        VariablePattern().Matches(template).Select(m => m.Groups[1].Value).Distinct();

    /// <summary>For "stages.plan.result" returns "plan"; null for other variables.</summary>
    public static string? StageReference(string variable)
    {
        var m = StagePattern().Match(variable);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string Render(string template, Func<string, string?> resolve) =>
        VariablePattern().Replace(template, m =>
        {
            var value = resolve(m.Groups[1].Value);
            return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
        });

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.\-]+)\s*\}\}")]
    private static partial Regex VariablePattern();

    [GeneratedRegex(@"^stages\.([A-Za-z0-9_-]+)\.(result|findings)$")]
    private static partial Regex StagePattern();
}
