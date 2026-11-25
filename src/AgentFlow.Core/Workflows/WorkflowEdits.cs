using System.Text.RegularExpressions;

namespace AgentFlow.Core.Workflows;

/// <summary>Edits that keep a workflow consistent, used by the builder.</summary>
public static partial class WorkflowEdits
{
    /// <summary>
    /// Renames a stage key and every reference to it: session modes, transition targets and
    /// {{stages.&lt;key&gt;.…}} variables in all prompts.
    /// </summary>
    public static WorkflowDefinition RenameKey(WorkflowDefinition wf, string oldKey, string newKey)
    {
        if (oldKey == newKey || string.IsNullOrWhiteSpace(newKey)) return wf;

        var variable = new Regex(@"\{\{\s*stages\." + Regex.Escape(oldKey) + @"\.(result|findings)\s*\}\}");
        return wf with
        {
            Stages = wf.Stages.Select(s => s with
            {
                Key = s.Key == oldKey ? newKey : s.Key,
                Session = s.Session == $"continue:{oldKey}" ? $"continue:{newKey}"
                    : s.Session == $"fork:{oldKey}" ? $"fork:{newKey}"
                    : s.Session,
                PromptTemplate = variable.Replace(s.PromptTemplate, m => $"{{{{stages.{newKey}.{m.Groups[1].Value}}}}}"),
                Transitions = s.Transitions.Select(t => t.Goto == oldKey ? t with { Goto = newKey } : t).ToList(),
            }).ToList(),
        };
    }

    /// <summary>A key based on the name that no other stage uses: "Code review" → "code-review", then "code-review-2".</summary>
    public static string UniqueKey(string name, IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.Ordinal);
        var slug = Slug(name, 32);
        if (slug.Length == 0 || slug == "end") slug = "stage";
        var key = slug;
        for (var i = 2; used.Contains(key); i++) key = $"{slug}-{i}";
        return key;
    }

    public static string Slug(string text, int max = 60)
    {
        var s = NonSlug().Replace(text.Trim().ToLowerInvariant(), "-").Trim('-');
        return s.Length > max ? s[..max].TrimEnd('-') : s;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
