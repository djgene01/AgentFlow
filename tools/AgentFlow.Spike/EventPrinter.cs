using AgentFlow.Core.Agents;

namespace AgentFlow.Spike;

internal static class EventPrinter
{
    private static bool _midLine;

    public static void Print(AgentEvent ev)
    {
        switch (ev)
        {
            case SessionStarted s:
                Line($"[session {s.SessionId} | model {s.Model} | {s.Tools.Count} tools | mode {s.PermissionMode}]", ConsoleColor.DarkCyan);
                break;
            case TextDelta d when d.ParentToolUseId is null:
                Console.Write(d.Text);
                _midLine = !d.Text.EndsWith('\n');
                break;
            case AssistantMessage a:
                foreach (var t in a.ToolUses)
                    Line($"[tool {t.Name} {Trim(t.Input.GetRawText(), 120)}]", ConsoleColor.DarkYellow);
                break;
            case UserMessage u:
                foreach (var r in u.ToolResults)
                    Line($"[result{(r.IsError ? " ERROR" : "")} {Trim(r.Content, 120)}]", ConsoleColor.DarkGray);
                break;
            case ApiRetry r:
                Line($"[retry {r.Attempt}/{r.MaxRetries} in {r.RetryDelayMs} ms: {r.Error}]", ConsoleColor.Yellow);
                break;
            case PermissionDenied p:
                Line($"[permission denied: {p.ToolName}]", ConsoleColor.Red);
                break;
            case TurnResult r:
                Line($"[result {r.Subtype}{(r.IsError ? " (error)" : "")} | {r.NumTurns} turns | {r.DurationMs} ms | est. ${r.TotalCostUsd} | in {r.Usage?.InputTokens} out {r.Usage?.OutputTokens}]", ConsoleColor.Green);
                if (r.StructuredOutput is { } so)
                    Line($"[structured_output {so.GetRawText()}]", ConsoleColor.Green);
                break;
            case RateLimitStatus rl:
                Line($"[usage {rl.Status} | {string.Join(", ", rl.Utilization.Select(kv => $"{kv.Key} {kv.Value:P0}"))}]", ConsoleColor.DarkGray);
                break;
            case ParseError p:
                Line($"[parse error: {p.Message}]", ConsoleColor.Red);
                break;
            case UnknownEvent u:
                Line($"[unknown event {u.Type}/{u.Subtype}]", ConsoleColor.DarkMagenta);
                break;
            case ProcessExited x:
                Line($"[exit {x.ExitCode}{(x.WasStopped ? $" | stopped: {x.StopOutcome}" : "")}]", ConsoleColor.DarkCyan);
                if (x.StandardError.Length > 0)
                    Line($"[stderr] {x.StandardError.Trim()}", ConsoleColor.DarkRed);
                break;
        }
    }

    private static void Line(string text, ConsoleColor color)
    {
        if (_midLine) { Console.WriteLine(); _midLine = false; }
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = old;
    }

    private static string Trim(string s, int max)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Length <= max ? s : s[..max] + "...";
    }
}
