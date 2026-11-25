using AgentFlow.ClaudeCli;
using AgentFlow.Core.Agents;
using AgentFlow.Permissions;

namespace AgentFlow.Spike;

/// <summary>
/// Phase 5 spike: does the real CLI send permission prompts to the app's MCP tool, what does it send,
/// and does it obey allow and deny? Runs in an empty temp folder with Haiku.
/// </summary>
internal static class ApprovalSpike
{
    public static async Task<int> RunAsync(string exe, SpikeArgs cli)
    {
        await using var server = new PermissionServer();
        server.Trace += line => Console.WriteLine("   [server] " + (line.Length > 400 ? line[..400] + "…" : line));
        await server.StartAsync();
        Console.WriteLine($"Permission server at {server.Endpoint}");

        var sandbox = Directory.CreateTempSubdirectory("agentflow-approval-spike-").FullName;
        var runner = new ClaudeProcessRunner(new ClaudeRunnerOptions { ExecutablePath = exe, ApprovalChannel = server });
        var model = cli.Get("model", "haiku");
        var results = new List<string>();

        foreach (var (label, answer) in new[]
                 {
                     ("allow", new ApprovalAnswer(ApprovalChoice.AllowOnce)),
                     ("deny", new ApprovalAnswer(ApprovalChoice.Deny, "The user said no: do not run commands.")),
                 })
        {
            Console.WriteLine($"\n=== {label} ===");
            var asked = new List<ApprovalRequest>();
            var request = new StageTurnRequest
            {
                WorkingDirectory = sandbox,
                // A command that writes a file, so it is never auto-approved as read-only.
                Prompt = $"Run exactly this shell command once: git init {label}-repo   Then reply with only DONE, or with DENIED if you were not allowed to run it. Do not use any other tool.",
                Model = model,
                PermissionMode = "default",
                MaxTurns = 4,
                ApprovalHandler = (req, ct) =>
                {
                    asked.Add(req);
                    Console.WriteLine($"   PROMPT tool={req.ToolName} id={req.ToolUseId} input={req.Input.GetRawText()}");
                    Console.WriteLine($"   raw arguments: {req.RawArguments}");
                    return Task.FromResult(answer);
                },
            };

            TurnResult? result = null;
            ProcessExited? exit = null;
            await foreach (var ev in runner.RunTurnAsync(request))
            {
                switch (ev)
                {
                    case SessionStarted s: Console.WriteLine($"   init: {s.Tools.Count} tools, mcp tool listed: {s.Tools.Any(t => t.Contains(PermissionServer.ToolName))}"); break;
                    case AssistantMessage a: foreach (var t in a.ToolUses) Console.WriteLine($"   tool_use {t.Name} {t.Input.GetRawText()}"); break;
                    case UserMessage u: foreach (var r in u.ToolResults) Console.WriteLine($"   tool_result error={r.IsError} {Trim(r.Content)}"); break;
                    case PermissionDenied d: Console.WriteLine($"   permission_denied {d.ToolName}"); break;
                    case TurnResult r: result = r; break;
                    case ProcessExited x: exit = x; break;
                }
            }
            Console.WriteLine($"   result: '{Trim(result?.ResultText ?? "")}' exit={exit?.ExitCode} stderr='{Trim(exit?.StandardError ?? "")}'");
            var created = Directory.Exists(Path.Combine(sandbox, $"{label}-repo", ".git"));
            results.Add($"{label}: prompts={asked.Count}, repo created={created}, result='{Trim(result?.ResultText ?? "")}'");
        }

        Console.WriteLine("\n=== summary ===");
        results.ForEach(Console.WriteLine);
        try { Directory.Delete(sandbox, true); } catch (IOException) { }
        return 0;
    }

    private static string Trim(string s) => s.Length > 160 ? s[..160].ReplaceLineEndings(" ") + "…" : s.ReplaceLineEndings(" ");
}
