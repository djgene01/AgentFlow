using System.Diagnostics;
using System.Text.Json;

namespace AgentFlow.ClaudeCli;

/// <summary>
/// Read-only checks for the diagnostics page: `claude --version` and `claude auth status`.
/// The app only reads the auth status; sign-in itself always happens in Anthropic's own flow
/// (`claude auth login` in a terminal the user controls).
/// </summary>
public static class ClaudeCliProbe
{
    public sealed record AuthStatus(bool SignedIn, string? AuthMethod, string RawOutput);

    public static async Task<string?> GetVersionAsync(string exe, CancellationToken ct = default)
    {
        var (code, stdout, _) = await RunAsync(exe, ["--version"], ct);
        return code == 0 ? stdout.Trim() : null;
    }

    public static async Task<AuthStatus> GetAuthStatusAsync(string exe, CancellationToken ct = default)
    {
        var (code, stdout, stderr) = await RunAsync(exe, ["auth", "status"], ct);
        string? method = null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("authMethod", out var m) && m.ValueKind == JsonValueKind.String)
                method = m.GetString();
        }
        catch (JsonException) { }

        return new AuthStatus(code == 0, method, stdout.Length > 0 ? stdout : stderr);
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var isCmd = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo
        {
            FileName = isCmd ? "cmd.exe" : exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (isCmd) { psi.ArgumentList.Add("/d"); psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(exe); }
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            throw;
        }
        return (p.ExitCode, await outTask, await errTask);
    }
}
