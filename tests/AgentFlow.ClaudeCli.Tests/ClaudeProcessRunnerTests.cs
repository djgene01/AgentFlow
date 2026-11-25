using System.Diagnostics;
using AgentFlow.ClaudeCli;
using AgentFlow.Core.Agents;

namespace AgentFlow.ClaudeCli.Tests;

/// <summary>Runs the real process runner against FakeClaude, which replays a fixture.</summary>
public class ClaudeProcessRunnerTests
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic-basic-turn.ndjson");
    private static readonly string FakeExe = Path.Combine(AppContext.BaseDirectory, "FakeClaude.exe");

    private static ClaudeProcessRunner Runner(params string[] fakeArgs) => new(new ClaudeRunnerOptions
    {
        ExecutablePath = FakeExe,
        PrefixArguments = [Fixture, .. fakeArgs],
        TryInterruptFirst = false, // console Ctrl+C is exercised by the spike, not in the test host
    });

    private static StageTurnRequest Req(string prompt = "hello") => new()
    {
        WorkingDirectory = AppContext.BaseDirectory,
        Prompt = prompt,
        Model = "haiku",
    };

    [Fact]
    public async Task Streams_parsed_events_and_ends_with_process_exited()
    {
        var events = new List<AgentEvent>();
        await foreach (var e in Runner().RunTurnAsync(Req()))
            events.Add(e);

        Assert.IsType<SessionStarted>(events[0]);
        Assert.Single(events.OfType<TurnResult>());
        var exit = Assert.IsType<ProcessExited>(events[^1]);
        Assert.Equal(0, exit.ExitCode);
        Assert.False(exit.WasStopped);
        Assert.Contains("fake stderr line", exit.StandardError);
    }

    [Fact]
    public async Task Writes_prompt_to_stdin_and_passes_generated_arguments()
    {
        var dir = Directory.CreateTempSubdirectory("agentflow-test-").FullName;
        var argsFile = Path.Combine(dir, "args.txt");
        var stdinFile = Path.Combine(dir, "stdin.txt");

        await foreach (var _ in Runner("--echo-args", argsFile, "--echo-stdin", stdinFile)
                           .RunTurnAsync(Req("Plan the change: ü ✓ \"quotes\"") with { AppendSystemPrompt = "You are the planner." })) { }

        Assert.Equal("Plan the change: ü ✓ \"quotes\"", File.ReadAllText(stdinFile));
        var args = File.ReadAllLines(argsFile);
        Assert.Contains("--model", args);
        Assert.Contains("haiku", args);
        Assert.Contains("--append-system-prompt-file", args);
        var roleFile = args[Array.IndexOf(args, "--append-system-prompt-file") + 1];
        Assert.False(File.Exists(roleFile), "the temporary role prompt file is deleted after the turn");
    }

    [Fact]
    public async Task Reports_non_zero_exit_code()
    {
        ProcessExited? exit = null;
        await foreach (var e in Runner("--exit-code", "7").RunTurnAsync(Req()))
            if (e is ProcessExited pe) exit = pe;

        Assert.Equal(7, exit!.ExitCode);
    }

    [Fact]
    public async Task Cancelling_kills_a_hung_process_and_still_completes()
    {
        using var cts = new CancellationTokenSource();
        var events = new List<AgentEvent>();
        var sw = Stopwatch.StartNew();

        await foreach (var e in Runner("--hang").RunTurnAsync(Req(), cts.Token))
        {
            events.Add(e);
            if (e is TurnResult) cts.Cancel();
        }

        var exit = Assert.IsType<ProcessExited>(events[^1]);
        Assert.True(exit.WasStopped);
        Assert.Equal(StopOutcome.Killed, exit.StopOutcome);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Ctrl_c_through_the_helper_ends_the_process_without_killing_it()
    {
        var runner = new ClaudeProcessRunner(new ClaudeRunnerOptions
        {
            ExecutablePath = FakeExe,
            PrefixArguments = [Fixture, "--hang"],
            TryInterruptFirst = true,
            InterruptGracePeriod = TimeSpan.FromSeconds(10),
        });
        using var cts = new CancellationTokenSource();
        ProcessExited? exit = null;

        await foreach (var e in runner.RunTurnAsync(Req(), cts.Token))
        {
            if (e is TurnResult) cts.Cancel();
            if (e is ProcessExited pe) exit = pe;
        }

        Assert.True(exit!.WasStopped);
        Assert.Equal(StopOutcome.ExitedAfterInterrupt, exit.StopOutcome);
    }

    [Fact]
    public async Task Missing_executable_throws_a_clear_error()
    {
        var runner = new ClaudeProcessRunner(new ClaudeRunnerOptions { ExecutablePath = @"C:\nope\claude.exe" });
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in runner.RunTurnAsync(Req())) { }
        });
    }
}
