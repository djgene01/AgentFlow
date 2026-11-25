using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using AgentFlow.ClaudeCli.Interop;
using AgentFlow.Core.Agents;

namespace AgentFlow.ClaudeCli;

/// <summary>
/// Runs one turn as one `claude -p` process: arguments from <see cref="ClaudeArgumentBuilder"/>,
/// the prompt on stdin, stream-json lines read from stdout.
/// </summary>
public sealed class ClaudeProcessRunner(ClaudeRunnerOptions options) : IAgentRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
        StageTurnRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var exe = options.ExecutablePath ?? ClaudeCliLocator.Find()
            ?? throw new ClaudeCliNotFoundException();

        string? promptFile = null;
        if (!string.IsNullOrWhiteSpace(request.AppendSystemPrompt))
        {
            promptFile = Path.Combine(Path.GetTempPath(), $"agentflow-role-{Guid.NewGuid():N}.md");
            await File.WriteAllTextAsync(promptFile, request.AppendSystemPrompt, Utf8NoBom, CancellationToken.None);
        }

        ApprovalRegistration? approvals = null;
        string? mcpFile = null;
        if (request.ApprovalHandler is { } handler && options.ApprovalChannel is { } channel)
            approvals = channel.Register(handler);
        if (approvals is not null || request.McpServers.Count > 0)
        {
            // One config file: the permission server (if approvals are on) plus any extra servers.
            mcpFile = Path.Combine(Path.GetTempPath(), $"agentflow-mcp-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(mcpFile, McpConfig.Merge(approvals?.McpConfigJson, request.McpServers), Utf8NoBom, CancellationToken.None);
        }

        var psi = CreateStartInfo(exe, request, promptFile, mcpFile, approvals?.PromptToolName);
        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        var stopRequested = false;
        StopOutcome? stopOutcome = null;
        Task? stopTask = null;

        try
        {
            process.Start();
            JobObject.Shared.TryAssign(process);

            var stderrTask = PumpStderrAsync(process, stderr);

            await process.StandardInput.WriteAsync(request.Prompt);
            await process.StandardInput.FlushAsync(CancellationToken.None);
            process.StandardInput.Close();

            // Cancelling does not abandon the stream: we interrupt the process and keep reading
            // until it closes stdout, so a final result (if any) still arrives.
            await using var reg = ct.Register(() =>
            {
                stopRequested = true;
                stopTask = Task.Run(async () => stopOutcome = await StopAsync(process));
            });

            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            {
                if (line.Length == 0) continue;
                options.RawLineObserver?.Invoke(line);
                yield return StreamJsonParser.Parse(line);
            }

            await process.WaitForExitAsync(CancellationToken.None);
            await stderrTask;
            if (stopTask is not null) await stopTask;

            yield return new ProcessExited(process.ExitCode, stopRequested, stopOutcome, stderr.ToString());
        }
        finally
        {
            if (!HasExited(process))
                KillTree(process); // the caller abandoned the enumeration
            if (promptFile is not null)
                TryDelete(promptFile);
            approvals?.Dispose(); // the turn's token stops working; an open prompt is answered with a deny
            if (mcpFile is not null)
                TryDelete(mcpFile);
        }
    }

    private ProcessStartInfo CreateStartInfo(string exe, StageTurnRequest request, string? promptFile, string? mcpFile, string? promptTool)
    {
        var isCmdShim = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                        || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        var psi = new ProcessStartInfo
        {
            FileName = isCmdShim ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : exe,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Its own hidden console, so a stop's Ctrl+C reaches only this process tree.
            CreateNoWindow = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (isCmdShim)
        {
            // npm installs a .cmd shim. Arguments pass through cmd.exe, which mangles some
            // characters; the native claude.exe install is preferred.
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(exe);
        }

        foreach (var p in options.PrefixArguments) psi.ArgumentList.Add(p);
        foreach (var a in ClaudeArgumentBuilder.Build(request, promptFile, mcpFile, promptTool)) psi.ArgumentList.Add(a);
        return psi;
    }

    private async Task<StopOutcome> StopAsync(Process process)
    {
        if (HasExited(process)) return StopOutcome.ExitedAfterInterrupt;

        var helper = options.TryInterruptFirst ? ConsoleInterrupt.FindHelper(options.InterruptHelperPath) : null;
        if (helper is not null && await ConsoleInterrupt.TrySendAsync(helper, process.Id))
        {
            using var grace = new CancellationTokenSource(options.InterruptGracePeriod);
            try
            {
                await process.WaitForExitAsync(grace.Token);
                return StopOutcome.ExitedAfterInterrupt;
            }
            catch (OperationCanceledException)
            {
                // fall through to kill
            }
        }

        KillTree(process);
        return StopOutcome.Killed;
    }

    private static async Task PumpStderrAsync(Process process, StringBuilder sink)
    {
        while (await process.StandardError.ReadLineAsync(CancellationToken.None) is { } line)
        {
            lock (sink) sink.AppendLine(line);
        }
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; }
        catch (InvalidOperationException) { return true; } // never started
    }

    private static void KillTree(Process p)
    {
        try { p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class ClaudeCliNotFoundException()
    : Exception("The claude CLI was not found. Install Claude Code, then run 'claude auth login' in a terminal.");
