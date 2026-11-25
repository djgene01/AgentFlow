using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentFlow.Core.Workflows;

/// <summary>Loads, saves and validates workflow JSON files.</summary>
public static partial class WorkflowJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static WorkflowDefinition Parse(string json) =>
        JsonSerializer.Deserialize<WorkflowDefinition>(json, Options) ?? throw new JsonException("The file is empty.");

    public static string Serialize(WorkflowDefinition w) => JsonSerializer.Serialize(w, Options);

    public static WorkflowDefinition Load(string path) => Parse(File.ReadAllText(path));

    public static void Save(WorkflowDefinition w, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Serialize(w));
    }

    /// <summary>Every problem found; an empty list means the workflow can run.</summary>
    public static IReadOnlyList<string> Validate(WorkflowDefinition w)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(w.Name)) errors.Add("The workflow needs a name.");
        if (w.Stages.Count == 0) errors.Add("The workflow needs at least one stage.");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < w.Stages.Count; i++)
        {
            var s = w.Stages[i];
            var label = string.IsNullOrWhiteSpace(s.Name) ? $"Stage {i + 1}" : $"Stage '{s.Name}'";

            if (!KeyPattern().IsMatch(s.Key))
                errors.Add($"{label}: the key must be letters, digits, '-' or '_' (got '{s.Key}').");
            else if (s.Key == "end")
                errors.Add($"{label}: 'end' is reserved.");
            else if (!keys.Add(s.Key))
                errors.Add($"{label}: the key '{s.Key}' is used twice.");

            if (s.PermissionPreset == PermissionPreset.Custom && string.IsNullOrWhiteSpace(s.PermissionMode))
                errors.Add($"{label}: a Custom preset needs a permission mode.");
            if (s.PermissionMode == "bypassPermissions")
                errors.Add($"{label}: bypassPermissions is not allowed.");
            if (s.Effort is not null && s.Effort is not ("low" or "medium" or "high" or "xhigh" or "max"))
                errors.Add($"{label}: unknown effort '{s.Effort}'.");
            if (s.MaxTurns is < 1) errors.Add($"{label}: maxTurns must be at least 1.");
            if (s.MaxBudgetUsd is <= 0) errors.Add($"{label}: maxBudgetUsd must be positive.");

            // Session references must point at a stage that runs earlier in the list.
            if (s.Session != "new")
            {
                var m = SessionPattern().Match(s.Session);
                if (!m.Success)
                    errors.Add($"{label}: session must be 'new', 'continue:<stage>' or 'fork:<stage>'.");
                else if (!w.Stages.Take(i).Any(p => p.Key == m.Groups[2].Value))
                    errors.Add($"{label}: session refers to '{m.Groups[2].Value}', which is not an earlier stage.");
            }

            foreach (var v in PromptTemplate.Variables(s.PromptTemplate))
            {
                if (PromptTemplate.StageReference(v) is { } refKey && !w.Stages.Any(p => p.Key == refKey))
                    errors.Add($"{label}: the prompt uses {{{{{v}}}}} but there is no stage '{refKey}'.");
                else if (PromptTemplate.StageReference(v) is null && !PromptTemplate.KnownVariables.Contains(v))
                    errors.Add($"{label}: unknown variable {{{{{v}}}}}.");
            }
        }

        foreach (var s in w.Stages)
        {
            foreach (var t in s.Transitions)
            {
                if (t.Goto != "end" && !w.Stages.Any(x => x.Key == t.Goto))
                    errors.Add($"Stage '{s.Name}': a transition goes to '{t.Goto}', which does not exist.");
                if (t.When is not null && s.Output != StageOutput.Verdict)
                    errors.Add($"Stage '{s.Name}': a 'when' transition needs the stage output to be 'verdict'.");
                if (t.MaxLoops is < 1)
                    errors.Add($"Stage '{s.Name}': maxLoops must be at least 1.");
            }
        }

        return errors;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^(continue|fork):([A-Za-z0-9_-]{1,40})$")]
    internal static partial Regex SessionPattern();
}
