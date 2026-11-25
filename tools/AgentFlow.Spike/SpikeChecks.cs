using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentFlow.ClaudeCli;
using AgentFlow.Core.Agents;

namespace AgentFlow.Spike;

/// <summary>
/// The five phase 0 checks from the development plan. Every turn runs in a new empty sandbox
/// folder, never in a real project, with tight turn and budget limits. Raw stdout of each turn
/// is saved as an NDJSON fixture for the parser tests.
/// </summary>
internal static class SpikeChecks
{
    private sealed record Capture(
        List<AgentEvent> Events, SessionStarted? Session, TurnResult? Result, ProcessExited Exit, string StreamedText)
    {
        public string ResultText => Result?.ResultText ?? "";
    }

    private sealed record Outcome(string Name, bool? Passed, string Detail);

    public static async Task<int> RunAsync(string exe, SpikeArgs cli)
    {
        var outDir = Path.GetFullPath(cli.Get("out", Path.Combine("spike-output", DateTime.Now.ToString("yyyyMMdd-HHmmss"))));
        var fixtures = Directory.CreateDirectory(Path.Combine(outDir, "fixtures")).FullName;
        var sandbox = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "agentflow-spike-" + Guid.NewGuid().ToString("N")[..8])).FullName;

        var modelA = cli.Get("model-a", "haiku");
        var modelB = cli.Get("model-b", "sonnet");
        var version = await ClaudeCliProbe.GetVersionAsync(exe) ?? "unknown";
        var auth = await ClaudeCliProbe.GetAuthStatusAsync(exe);

        Console.WriteLine($"claude {version} | signed in: {auth.SignedIn} ({auth.AuthMethod})");
        Console.WriteLine($"sandbox: {sandbox}");
        Console.WriteLine($"output:  {outDir}");
        if (!auth.SignedIn)
        {
            Console.Error.WriteLine("Not signed in. Run 'claude auth login' in a terminal first.");
            return 1;
        }

        var outcomes = new List<Outcome>();
        var only = cli.Get("only")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        async Task Check(string name, Func<Task<Outcome>> body)
        {
            if (only is not null && !only.Contains(name.Split('.')[0])) return;
            Console.WriteLine();
            Console.WriteLine($"=== {name} ===");
            Outcome o;
            try { o = await body(); }
            catch (Exception ex) { o = new Outcome(name, false, "Threw: " + ex.Message); }
            outcomes.Add(o);
            Console.WriteLine($"--> {(o.Passed switch { true => "PASS", false => "FAIL", null => "OBSERVED" })}: {o.Detail}");
        }

        StageTurnRequest Base(string prompt) => new()
        {
            WorkingDirectory = sandbox,
            Prompt = prompt,
            Model = modelA,
            PermissionMode = "dontAsk",
            MaxTurns = 3,
            MaxBudgetUsd = 0.50m,
        };

        Task<Capture> Turn(StageTurnRequest r, string fixture, Func<AgentEvent, bool>? stopWhen = null) =>
            RunTurnAsync(exe, r, Path.Combine(fixtures, fixture + ".ndjson"), stopWhen);

        // 1. Prompt on stdin with -p and no prompt argument.
        await Check("1. Prompt via stdin", async () =>
        {
            var c = await Turn(Base("Reply with exactly the word PONG and nothing else."), "01-stdin-prompt");
            var ok = c.ResultText.Contains("PONG", StringComparison.OrdinalIgnoreCase) && c.Session is not null;
            return new("1. Prompt via stdin", ok,
                $"result='{Short(c.ResultText)}', session={c.Session?.SessionId}, streamed {c.StreamedText.Length} chars, exit {c.Exit.ExitCode}");
        });

        // 2. --json-schema together with stream-json output.
        await Check("2. json-schema with stream-json", async () =>
        {
            const string schema = """{"type":"object","properties":{"answer":{"type":"integer"}},"required":["answer"]}""";
            var c = await Turn(Base("What is 2 + 3? Give the answer in the structured output.") with { JsonSchema = schema }, "02-json-schema");
            var so = c.Result?.StructuredOutput;
            var ok = so is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.Number && a.GetInt32() == 5;
            return new("2. json-schema with stream-json", ok,
                $"structured_output={so?.GetRawText() ?? "(absent)"}, result='{Short(c.ResultText)}'");
        });

        // 3. --resume with a different --model and --effort keeps the conversation.
        await Check("3. Resume on another model and effort", async () =>
        {
            var id = Guid.NewGuid();
            var first = await Turn(Base("Remember this codeword: ORCHID-42. Reply only with OK.") with { Session = new SessionSpec.New(id) }, "03a-resume-first");
            var second = await Turn(Base("What codeword did I give you? Reply with the codeword only.") with
            {
                Session = new SessionSpec.Resume(id.ToString("D")),
                Model = modelB,
                Effort = cli.Get("effort-b", "low"),
            }, "03b-resume-second");
            var ok = second.ResultText.Contains("ORCHID-42", StringComparison.OrdinalIgnoreCase);
            return new("3. Resume on another model and effort", ok,
                $"turn A model={first.Session?.Model}, turn B model={second.Session?.Model}, same session={first.Session?.SessionId == second.Session?.SessionId}, answer='{Short(second.ResultText)}', exit {second.Exit.ExitCode}, stderr='{Short(second.Exit.StandardError)}'");
        });

        // 4. Stopping mid-turn on Windows, then resuming the session.
        await Check("4. Stop mid-turn, then resume", async () =>
        {
            var id = Guid.NewGuid();
            var deltas = 0;
            var sw = Stopwatch.StartNew();
            var stopped = await Turn(
                Base("Count from 1 to 300, one number per line, no other text.") with { Session = new SessionSpec.New(id), MaxTurns = 1 },
                "04a-stop",
                stopWhen: ev => ev is TextDelta && ++deltas == 15);
            var stopMs = sw.ElapsedMilliseconds;

            var resumed = await Turn(Base("Reply with exactly the word RESUMED.") with { Session = new SessionSpec.Resume(id.ToString("D")) }, "04b-resume-after-stop");
            var ok = resumed.ResultText.Contains("RESUMED", StringComparison.OrdinalIgnoreCase);
            return new("4. Stop mid-turn, then resume", ok,
                $"stop outcome={stopped.Exit.StopOutcome}, exit code={stopped.Exit.ExitCode}, result event after stop={(stopped.Result is null ? "none" : stopped.Result.Subtype)}, total {stopMs} ms; resume answer='{Short(resumed.ResultText)}', exit {resumed.Exit.ExitCode}");
        });

        // 5. Plan mode in print mode: where does the plan arrive?
        await Check("5. Plan mode output", async () =>
        {
            File.WriteAllText(Path.Combine(sandbox, "notes.txt"), "Shopping: milk, eggs\n");
            var c = await Turn(Base("Plan how you would turn notes.txt into a Markdown checklist. Do not change any files.") with
            {
                PermissionMode = "plan",
                MaxTurns = 6,
            }, "05-plan-mode");
            var toolNames = c.Events.OfType<AssistantMessage>().SelectMany(a => a.ToolUses).Select(t => t.Name).ToList();
            var exitPlan = c.Events.OfType<AssistantMessage>().SelectMany(a => a.ToolUses).FirstOrDefault(t => t.Name == "ExitPlanMode");
            var planInTool = exitPlan?.Input.TryGetProperty("plan", out var p) == true ? p.GetString() : null;
            var fileUnchanged = File.ReadAllText(Path.Combine(sandbox, "notes.txt")) == "Shopping: milk, eggs\n";
            return new("5. Plan mode output", null,
                $"result text {c.ResultText.Length} chars ('{Short(c.ResultText)}'); ExitPlanMode called={exitPlan is not null}, plan in its input={planInTool?.Length ?? 0} chars; tools used=[{string.Join(", ", toolNames)}]; file unchanged={fileUnchanged}; result subtype={c.Result?.Subtype}");
        });

        var report = WriteReport(outDir, version, auth, modelA, modelB, outcomes);
        Console.WriteLine();
        Console.WriteLine($"Report:   {report}");
        Console.WriteLine($"Fixtures: {fixtures}");
        TryDeleteDirectory(sandbox);
        return outcomes.Any(o => o.Passed == false) ? 1 : 0;
    }

    private static async Task<Capture> RunTurnAsync(string exe, StageTurnRequest request, string fixturePath, Func<AgentEvent, bool>? stopWhen)
    {
        await using var fixture = new StreamWriter(fixturePath, append: false, new UTF8Encoding(false));
        var runner = new ClaudeProcessRunner(new ClaudeRunnerOptions
        {
            ExecutablePath = exe,
            RawLineObserver = line => { lock (fixture) fixture.WriteLine(line); },
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var events = new List<AgentEvent>();
        var text = new StringBuilder();
        ProcessExited? exit = null;

        await foreach (var ev in runner.RunTurnAsync(request, cts.Token))
        {
            events.Add(ev);
            EventPrinter.Print(ev);
            if (ev is TextDelta { ParentToolUseId: null } d) text.Append(d.Text);
            if (ev is ProcessExited pe) exit = pe;
            if (stopWhen is not null && !cts.IsCancellationRequested && stopWhen(ev))
            {
                Console.WriteLine();
                Console.WriteLine("[spike: requesting stop]");
                cts.Cancel();
            }
        }

        Console.WriteLine();
        return new Capture(events, events.OfType<SessionStarted>().FirstOrDefault(), events.OfType<TurnResult>().LastOrDefault(),
            exit ?? new ProcessExited(-1, false, null, "no exit event"), text.ToString());
    }

    private static string WriteReport(string outDir, string version, ClaudeCliProbe.AuthStatus auth, string modelA, string modelB, List<Outcome> outcomes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Phase 0 spike report");
        sb.AppendLine();
        sb.AppendLine($"- Date: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- Claude Code: {version}");
        sb.AppendLine($"- Auth method: {auth.AuthMethod}");
        sb.AppendLine($"- Models: A = {modelA}, B = {modelB}");
        sb.AppendLine();
        sb.AppendLine("| Check | Outcome | Detail |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (var o in outcomes)
            sb.AppendLine($"| {o.Name} | {(o.Passed switch { true => "Pass", false => "Fail", null => "Observed" })} | {o.Detail.Replace("|", "\\|").ReplaceLineEndings(" ")} |");
        var path = Path.Combine(outDir, "spike-report.md");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static string Short(string s) => s.Length <= 80 ? s.ReplaceLineEndings(" ").Trim() : s[..80].ReplaceLineEndings(" ").Trim() + "...";

    private static void TryDeleteDirectory(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
