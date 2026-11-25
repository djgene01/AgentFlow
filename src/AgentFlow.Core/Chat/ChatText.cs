using System.Globalization;
using System.Text;

namespace AgentFlow.Core.Chat;

/// <summary>The searchable text of a chat entry, and Markdown export of a chat or one workflow run.</summary>
public static class ChatText
{
    /// <summary>All text a person could search for in this entry.</summary>
    public static string Of(ChatEntryData d) => d switch
    {
        UserMessageData u => u.Text,
        AssistantTextData a => a.Text,
        ToolCallData t => $"{t.Name} {t.InputJson} {t.Result}",
        NoticeData n => n.Text,
        TurnSummaryData s => $"{s.Model} {s.Subtype}",
        SessionDividerData s => s.Text,
        StageDividerData s => $"{s.StageName} {s.Model} {s.Effort}",
        GateData g => $"{g.StageName} {g.Text} {g.Feedback}",
        VerdictData v => $"{v.Verdict} {v.Summary} {string.Join(" ", v.Findings.Select(f => $"{f.File} {f.Issue} {f.Fix}"))}",
        RunStatusData r => r.Text,
        RecoveryData r => $"{r.WorkflowName} {r.StageName}",
        ApprovalData a => $"{a.ToolName} {a.Summary} {a.Description}",
        _ => "",
    };

    public static bool Matches(ChatEntryData d, string query) =>
        query.Length > 0 && Of(d).Contains(query, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>The entries of one workflow run: from its "started" card to its last status card.</summary>
    public static IReadOnlyList<ChatEntry> RunEntries(IReadOnlyList<ChatEntry> all, string runId)
    {
        var first = -1;
        var last = -1;
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].Data is RunStatusData r && r.RunId == runId)
            {
                if (first < 0) first = i;
                last = i;
            }
        }
        if (first < 0) return [];
        // Include the task message just before the "started" card.
        if (first > 0 && all[first - 1].Data is UserMessageData) first--;
        return all.Skip(first).Take(last - first + 1).ToList();
    }

    /// <summary>A readable Markdown transcript. Tool calls are summarised; streaming leftovers are skipped.</summary>
    public static string ToMarkdown(string title, string projectFolder, IEnumerable<ChatEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}").AppendLine();
        sb.AppendLine($"Project: `{projectFolder}`  ").AppendLine($"Exported: {DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}").AppendLine();

        var inToolList = false;
        foreach (var e in entries)
        {
            // Tool calls form a list; anything else after them needs a blank line first.
            if (inToolList && e.Data is not ToolCallData) sb.AppendLine();
            inToolList = e.Data is ToolCallData;
            var time = e.CreatedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            switch (e.Data)
            {
                case UserMessageData u:
                    sb.AppendLine($"### You ({time}{(u.Model is null ? "" : ", " + u.Model)}{(u.QueuedFor is null ? "" : ", queued for " + u.QueuedFor)})").AppendLine();
                    sb.AppendLine(Quote(u.Text)).AppendLine();
                    break;
                case AssistantTextData a when !string.IsNullOrWhiteSpace(a.Text):
                    sb.AppendLine(a.Text.Trim()).AppendLine();
                    break;
                case ToolCallData t:
                    var summary = Summary(t.InputJson);
                    sb.AppendLine($"- {(t.IsError ? "✕" : "✓")} **{t.Name}**{(summary.Length > 0 ? $" `{summary}`" : "")}");
                    break;
                case NoticeData n:
                    sb.AppendLine($"> **{n.Level}:** {n.Text}").AppendLine();
                    break;
                case TurnSummaryData s:
                    sb.AppendLine().AppendLine($"_{s.Model} · {(s.DurationMs ?? 0) / 1000.0:0.0} s · {s.InputTokens ?? 0:N0} in / {s.OutputTokens ?? 0:N0} out tokens · est. ${s.CostUsd ?? 0:0.0000}_").AppendLine();
                    break;
                case SessionDividerData s:
                    sb.AppendLine("---").AppendLine().AppendLine($"_{s.Text}_").AppendLine();
                    break;
                case StageDividerData s:
                    sb.AppendLine($"## Stage {s.StageNumber}: {s.StageName}{(s.Attempt > 1 ? $" (round {s.Attempt})" : "")}").AppendLine();
                    sb.AppendLine($"_{s.Model ?? "default model"}{(s.Effort is null ? "" : " · " + s.Effort)} · {s.Permissions} · {s.SessionMode}_").AppendLine();
                    break;
                case GateData g:
                    sb.AppendLine($"**{(g.GateKind == GateData.LoopLimitKind ? "Decision" : "Approval")}: {g.Status}**{(string.IsNullOrWhiteSpace(g.Feedback) ? "" : " — " + g.Feedback)}").AppendLine();
                    if (g.WasEdited) sb.AppendLine("Approved text (edited by you):").AppendLine().AppendLine(Quote(g.Text)).AppendLine();
                    break;
                case VerdictData v:
                    sb.AppendLine($"**Verdict: {v.Verdict}**{(string.IsNullOrWhiteSpace(v.Summary) ? "" : " — " + v.Summary)}").AppendLine();
                    foreach (var f in v.Findings)
                        sb.AppendLine($"1. [{f.Severity}]{(f.File is null ? "" : $" `{f.File}{(f.Line is { } l ? ":" + l : "")}`")} {f.Issue}{(string.IsNullOrWhiteSpace(f.Fix) ? "" : $" Fix: {f.Fix}")}");
                    if (v.Findings.Count > 0) sb.AppendLine();
                    break;
                case RunStatusData r:
                    sb.AppendLine($"**{r.Text}**").AppendLine();
                    break;
                case ApprovalData a:
                    sb.AppendLine($"> Permission: {a.ToolName} `{a.Summary}` → {a.Status}{(a.DenyMessage is null ? "" : " (" + a.DenyMessage + ")")}").AppendLine();
                    break;
            }
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string Quote(string text) => string.Join(Environment.NewLine, text.Trim().Split('\n').Select(l => "> " + l.TrimEnd('\r')));

    private static string Summary(string inputJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(inputJson);
            foreach (var key in new[] { "file_path", "command", "pattern", "path", "url", "description" })
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = (v.GetString() ?? "").ReplaceLineEndings(" ").Replace('`', '\'');
                    return s.Length > 120 ? s[..120] + "…" : s;
                }
        }
        catch (System.Text.Json.JsonException) { }
        return "";
    }
}
