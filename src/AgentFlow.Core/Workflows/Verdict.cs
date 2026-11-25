using System.Text;
using System.Text.Json;

namespace AgentFlow.Core.Workflows;

public sealed record ReviewFinding(string Severity, string? File, int? Line, string Issue, string? Fix);

public sealed record Verdict(string Value, string? Summary, IReadOnlyList<ReviewFinding> Findings)
{
    public const string Pass = "pass";
    public const string ChangesRequested = "changes_requested";

    /// <summary>Passed to --json-schema for stages whose output is a verdict.</summary>
    public const string JsonSchema = """
        {"type":"object","properties":{
          "verdict":{"type":"string","enum":["pass","changes_requested"]},
          "summary":{"type":"string"},
          "findings":{"type":"array","items":{"type":"object","properties":{
            "severity":{"type":"string","enum":["critical","major","minor","nit"]},
            "file":{"type":"string"},
            "line":{"type":"integer"},
            "issue":{"type":"string"},
            "fix":{"type":"string"}},
            "required":["severity","issue"]}}},
         "required":["verdict","findings"]}
        """;

    public static Verdict? FromStructuredOutput(JsonElement? so)
    {
        if (so is not { ValueKind: JsonValueKind.Object } o) return null;
        if (!o.TryGetProperty("verdict", out var v) || v.ValueKind != JsonValueKind.String) return null;

        var findings = new List<ReviewFinding>();
        if (o.TryGetProperty("findings", out var f) && f.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in f.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                findings.Add(new ReviewFinding(
                    Str(item, "severity") ?? "minor",
                    Str(item, "file"),
                    item.TryGetProperty("line", out var l) && l.TryGetInt32(out var li) ? li : null,
                    Str(item, "issue") ?? "",
                    Str(item, "fix")));
            }
        }
        return new Verdict(v.GetString()!, Str(o, "summary"), findings);
    }

    /// <summary>Markdown list used for {{stages.&lt;key&gt;.findings}}.</summary>
    public string FindingsMarkdown()
    {
        if (Findings.Count == 0) return "(no findings)";
        var sb = new StringBuilder();
        var n = 1;
        foreach (var f in Findings)
        {
            var where = f.File is null ? "" : f.Line is { } line ? $" `{f.File}:{line}`" : $" `{f.File}`";
            sb.Append(n++).Append(". [").Append(f.Severity).Append(']').Append(where).Append(' ').AppendLine(f.Issue);
            if (!string.IsNullOrWhiteSpace(f.Fix)) sb.Append("   Suggested fix: ").AppendLine(f.Fix);
        }
        return sb.ToString().TrimEnd();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
