using AgentFlow.ClaudeCli;
using AgentFlow.Core.Agents;

namespace AgentFlow.Spike;

internal static class SingleRun
{
    public static async Task<int> RunAsync(string exe, SpikeArgs cli)
    {
        var prompt = string.Join(" ", cli.Positional);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            Console.Error.WriteLine("run needs a prompt.");
            return 2;
        }

        StreamWriter? record = cli.Get("record") is { } path ? new StreamWriter(path) : null;
        var runner = new ClaudeProcessRunner(new ClaudeRunnerOptions
        {
            ExecutablePath = exe,
            RawLineObserver = record is null ? null : line => record.WriteLine(line),
        });

        var request = new StageTurnRequest
        {
            WorkingDirectory = Path.GetFullPath(cli.Get("cwd", Environment.CurrentDirectory)),
            Prompt = prompt,
            Model = cli.Get("model"),
            Effort = cli.Get("effort"),
            PermissionMode = cli.Get("mode", "dontAsk"),
            Session = cli.Get("resume") is { } id ? new SessionSpec.Resume(id) : SessionSpec.NewSession(),
            MaxTurns = 10,
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // First Ctrl+C stops the turn through the runner; the spike keeps running to report.
            e.Cancel = true;
            cts.Cancel();
        };

        var exitCode = 0;
        await foreach (var ev in runner.RunTurnAsync(request, cts.Token))
        {
            EventPrinter.Print(ev);
            if (ev is ProcessExited pe) exitCode = pe.ExitCode;
        }

        record?.Dispose();
        return exitCode;
    }
}
