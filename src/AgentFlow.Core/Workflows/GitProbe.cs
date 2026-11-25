using System.Diagnostics;
using System.Text;

namespace AgentFlow.Core.Workflows;

/// <summary>Read-only git queries the engine uses for handoffs. The app runs git itself, never through Claude.</summary>
public interface IGitProbe
{
    /// <summary>HEAD commit, or null when the folder is not a usable git repository.</summary>
    Task<string?> GetHeadAsync(string folder, CancellationToken ct = default);

    /// <summary>The diff of the working tree against <paramref name="baseCommit"/>, plus untracked files.</summary>
    Task<string?> GetDiffAsync(string folder, string baseCommit, CancellationToken ct = default);
}

public sealed class GitCli : IGitProbe
{
    /// <summary>Diffs above this size are cut, so a large change does not flood the reviewer's prompt.</summary>
    public const int MaxDiffChars = 150_000;

    public async Task<string?> GetHeadAsync(string folder, CancellationToken ct = default)
    {
        var (code, output) = await RunAsync(folder, ct, "rev-parse", "HEAD");
        return code == 0 ? output.Trim() : null;
    }

    public async Task<string?> GetDiffAsync(string folder, string baseCommit, CancellationToken ct = default)
    {
        var (code, diff) = await RunAsync(folder, ct, "diff", "--no-color", "--no-ext-diff", baseCommit);
        if (code != 0) return null;

        var (code2, untracked) = await RunAsync(folder, ct, "ls-files", "--others", "--exclude-standard");
        var sb = new StringBuilder();
        if (diff.Length > MaxDiffChars)
            sb.Append(diff, 0, MaxDiffChars).AppendLine().AppendLine($"[diff cut at {MaxDiffChars:N0} characters; {diff.Length:N0} in total]");
        else
            sb.Append(diff);

        if (code2 == 0 && untracked.Trim().Length > 0)
        {
            sb.AppendLine().AppendLine("New files not yet tracked by git (read them directly):");
            foreach (var f in untracked.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                sb.Append("- ").AppendLine(f);
        }
        return sb.ToString().Trim();
    }

    private static async Task<(int Code, string Output)> RunAsync(string folder, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return (p.ExitCode, await output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, ""); // git not installed
        }
    }
}
