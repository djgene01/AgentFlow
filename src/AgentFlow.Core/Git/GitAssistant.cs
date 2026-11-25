using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentFlow.Core.Git;

/// <summary>One git command's outcome.</summary>
public sealed record GitResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;

    /// <summary>Output and error text together, for the activity log.</summary>
    public string Text => string.Join("\n", new[] { Output.Trim(), Error.Trim() }.Where(s => s.Length > 0));

    public static GitResult NotInstalled { get; } = new(-1, "", "git was not found on this computer.");
}

/// <summary>Runs git. Split out so the assistant can be tested without a real git.</summary>
public interface IGitShell
{
    Task<GitResult> RunAsync(string? folder, IReadOnlyList<string> args, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? environment = null);
}

public sealed class GitShell : IGitShell
{
    public async Task<GitResult> RunAsync(string? folder, IReadOnlyList<string> args, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (folder is not null) psi.WorkingDirectory = folder;
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Never wait on a terminal prompt nobody can see; Git Credential Manager shows its own window instead.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "always";
        if (environment is not null)
            foreach (var (k, v) in environment) psi.Environment[k] = v;

        Process p;
        try { p = Process.Start(psi)!; }
        catch (System.ComponentModel.Win32Exception) { return GitResult.NotInstalled; }

        using (p)
        {
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var error = p.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await p.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw;
            }
            return new GitResult(p.ExitCode, await output, await error);
        }
    }
}

public sealed record GitToolsInfo(string? GitVersion, string? CredentialManagerVersion, string? CredentialHelper)
{
    public bool HasGit => GitVersion is not null;
    public bool HasCredentialManager => CredentialManagerVersion is not null;

    /// <summary>True when git hands passwords to Git Credential Manager (helper "manager" or "manager-core").</summary>
    public bool UsesCredentialManager =>
        CredentialHelper is { } h && (h.Equals("manager", StringComparison.OrdinalIgnoreCase)
                                      || h.Equals("manager-core", StringComparison.OrdinalIgnoreCase)
                                      || h.Contains("credential-manager", StringComparison.OrdinalIgnoreCase));
}

public sealed record GitIdentity(string? Name, string? Email)
{
    public bool IsComplete => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Email);
}

/// <summary>What GitHub has on a branch that this folder does not, after a rejected push.</summary>
public sealed record RemoteWork(string? RemoteSha, IReadOnlyList<string> Commits, GitResult Fetch)
{
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>
    /// False when this folder's commits and GitHub's have no common ancestor: typically the folder is a
    /// downloaded ZIP that was turned into a new repository.
    /// </summary>
    public bool SharesHistory { get; init; } = true;

    /// <summary>For unrelated histories: GitHub's commit whose files are closest to this folder's.</summary>
    public string? ClosestCommit { get; init; }
    public int ClosestDifferences { get; init; }
    public bool ClosestIsLatest { get; init; }

    /// <summary>Files that differ between this folder and GitHub's latest commit.</summary>
    public int DifferencesFromLatest { get; init; }
}

public enum RepoState{ NoFolder, NotARepository, Unsafe, Ready }

public sealed record RepoStatus(RepoState State)
{
    public string? Branch { get; init; }
    public bool HasCommits { get; init; }
    public string? Upstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public int ChangedFiles { get; init; }
    public string? OriginUrl { get; init; }

    /// <summary>For <see cref="RepoState.Unsafe"/>: the exact path git asks to be added to safe.directory.</summary>
    public string? SafeDirectoryPath { get; init; }

    public string? Problem { get; init; }
}

/// <summary>
/// Everything the Git page does. Sign-in goes through Git Credential Manager's own browser flow, and
/// git keeps the credential in Windows Credential Manager; AgentFlow never sees a password or token.
/// </summary>
public sealed class GitAssistant(IGitShell shell)
{
    public const string Remote = "origin";

    public async Task<GitToolsInfo> GetToolsAsync(CancellationToken ct = default)
    {
        var git = await shell.RunAsync(null, ["--version"], ct);
        if (!git.Ok) return new GitToolsInfo(null, null, null);

        var gcm = await shell.RunAsync(null, ["credential-manager", "--version"], ct);
        var helper = await shell.RunAsync(null, ["config", "--get", "credential.helper"], ct);
        return new GitToolsInfo(
            git.Output.Trim().Replace("git version ", ""),
            gcm.Ok ? gcm.Output.Trim().Split('+')[0] : null,
            helper.Ok ? helper.Output.Trim().Split('\n')[^1].Trim() : null);
    }

    public async Task<GitIdentity> GetGlobalIdentityAsync(CancellationToken ct = default)
    {
        var name = await shell.RunAsync(null, ["config", "--global", "--get", "user.name"], ct);
        var email = await shell.RunAsync(null, ["config", "--global", "--get", "user.email"], ct);
        return new GitIdentity(name.Ok ? name.Output.Trim() : null, email.Ok ? email.Output.Trim() : null);
    }

    /// <summary>The identity commits in <paramref name="folder"/> will use (project setting, else global).</summary>
    public async Task<GitIdentity> GetEffectiveIdentityAsync(string folder, CancellationToken ct = default)
    {
        var name = await shell.RunAsync(folder, ["config", "--get", "user.name"], ct);
        var email = await shell.RunAsync(folder, ["config", "--get", "user.email"], ct);
        return new GitIdentity(name.Ok ? name.Output.Trim() : null, email.Ok ? email.Output.Trim() : null);
    }

    public async Task<GitResult> SetGlobalIdentityAsync(string name, string email, CancellationToken ct = default)
    {
        var r = await shell.RunAsync(null, ["config", "--global", "user.name", name.Trim()], ct);
        if (!r.Ok) return r;
        return await shell.RunAsync(null, ["config", "--global", "user.email", email.Trim()], ct);
    }

    /// <summary>Makes git hand sign-ins to Git Credential Manager (global credential.helper = manager).</summary>
    public Task<GitResult> UseCredentialManagerAsync(CancellationToken ct = default) =>
        shell.RunAsync(null, ["config", "--global", "credential.helper", "manager"], ct);

    // ---- GitHub accounts (Git Credential Manager) ----

    public async Task<IReadOnlyList<string>> ListGitHubAccountsAsync(CancellationToken ct = default)
    {
        var r = await shell.RunAsync(null, ["credential-manager", "github", "list"], ct);
        return r.Ok ? ParseAccounts(r.Output) : [];
    }

    /// <summary>Opens GitHub's sign-in page in the browser; returns once the user finished or gave up.</summary>
    public Task<GitResult> SignInToGitHubAsync(CancellationToken ct = default) =>
        shell.RunAsync(null, ["credential-manager", "github", "login", "--browser"], ct);

    public Task<GitResult> SignOutOfGitHubAsync(string account, CancellationToken ct = default) =>
        shell.RunAsync(null, ["credential-manager", "github", "logout", account], ct);

    // ---- one project ----

    public async Task<RepoStatus> GetStatusAsync(string folder, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) return new RepoStatus(RepoState.NoFolder) { Problem = "The folder does not exist." };

        var status = await shell.RunAsync(folder, ["status", "--porcelain=v2", "--branch", "--untracked-files=all"], ct);
        if (!status.Ok)
        {
            if (ParseUnsafePath(status.Error) is { } safe)
                return new RepoStatus(RepoState.Unsafe) { SafeDirectoryPath = safe, Problem = "Git does not trust this folder yet because it belongs to another Windows account or is on a network drive." };
            if (status.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            {
                // A parent folder may be the repository; git only says so when it is unsafe, so check ours first.
                return new RepoStatus(RepoState.NotARepository);
            }
            return new RepoStatus(RepoState.NotARepository) { Problem = status.Text };
        }

        var parsed = ParseStatus(status.Output);
        var origin = await shell.RunAsync(folder, ["remote", "get-url", Remote], ct);
        return parsed with { OriginUrl = origin.Ok ? origin.Output.Trim() : null };
    }

    /// <summary>The repository's top folder, which may be a parent of the project folder.</summary>
    public async Task<string?> GetTopLevelAsync(string folder, CancellationToken ct = default)
    {
        var r = await shell.RunAsync(folder, ["rev-parse", "--show-toplevel"], ct);
        return r.Ok ? r.Output.Trim() : null;
    }

    public async Task<GitResult> InitAsync(string folder, CancellationToken ct = default)
    {
        var r = await shell.RunAsync(folder, ["init", "-b", "main"], ct);
        if (!r.Ok) return r;
        var ignore = Path.Combine(folder, ".gitignore");
        if (!File.Exists(ignore))
        {
            await File.WriteAllTextAsync(ignore, DefaultGitIgnore, ct);
            return r with { Output = r.Output.TrimEnd() + "\nCreated .gitignore (build output, editor folders, node_modules, .env files)." };
        }
        return r;
    }

    public Task<GitResult> TrustFolderAsync(string safeDirectoryPath, CancellationToken ct = default) =>
        shell.RunAsync(null, ["config", "--global", "--add", "safe.directory", safeDirectoryPath], ct);

    public async Task<GitResult> SetRemoteAsync(string folder, string url, CancellationToken ct = default)
    {
        url = url.Trim();
        if (!IsValidRemoteUrl(url)) return new GitResult(1, "", "That does not look like a repository address (https://… or git@…).");
        var existing = await shell.RunAsync(folder, ["remote", "get-url", Remote], ct);
        return existing.Ok
            ? await shell.RunAsync(folder, ["remote", "set-url", Remote, url], ct)
            : await shell.RunAsync(folder, ["remote", "add", Remote, url], ct);
    }

    public async Task<GitResult> CommitAllAsync(string folder, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return new GitResult(1, "", "Write a commit message first.");
        var add = await shell.RunAsync(folder, ["add", "-A"], ct);
        if (!add.Ok) return add;
        return await shell.RunAsync(folder, ["commit", "-m", message.Trim()], ct);
    }

    /// <summary>Pushes the current branch to origin and sets it as the upstream. Git Credential Manager signs in if needed.</summary>
    public Task<GitResult> PushAsync(string folder, string branch, CancellationToken ct = default) =>
        shell.RunAsync(folder, ["push", "-u", Remote, branch], ct);

    public Task<GitResult> PullAsync(string folder, CancellationToken ct = default) =>
        shell.RunAsync(folder, ["pull", "--ff-only"], ct);

    /// <summary>True when a push failed because GitHub has commits this folder lacks.</summary>
    public static bool IsRejectedForRemoteWork(GitResult push) =>
        !push.Ok && (push.Error.Contains("fetch first", StringComparison.OrdinalIgnoreCase)
                     || push.Error.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase));

    /// <summary>Fetches the branch from GitHub and lists the commits there that this folder does not have.</summary>
    public async Task<RemoteWork> GetRemoteOnlyWorkAsync(string folder, string branch, CancellationToken ct = default)
    {
        var fetch = await shell.RunAsync(folder, ["fetch", Remote, branch], ct);
        if (!fetch.Ok) return new RemoteWork(null, [], fetch);
        var sha = await shell.RunAsync(folder, ["rev-parse", $"{Remote}/{branch}"], ct);
        var log = await shell.RunAsync(folder, ["log", "--format=%h %an: %s", $"HEAD..{Remote}/{branch}"], ct);
        var files = await shell.RunAsync(folder, ["ls-tree", "-r", "--name-only", $"{Remote}/{branch}"], ct);
        var work = new RemoteWork(
            sha.Ok ? sha.Output.Trim() : null,
            log.Ok ? log.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [],
            fetch)
        {
            Files = files.Ok ? files.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [],
        };

        var mergeBase = await shell.RunAsync(folder, ["merge-base", "HEAD", $"{Remote}/{branch}"], ct);
        if (mergeBase.Ok || work.RemoteSha is null) return work; // exit code 1 = no common ancestor

        // Unrelated histories: find which GitHub commit this folder's files are closest to.
        var list = await shell.RunAsync(folder, ["rev-list", "--max-count=200", $"{Remote}/{branch}"], ct);
        string? closest = null;
        int best = int.MaxValue, latest = 0;
        foreach (var c in list.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var diff = await shell.RunAsync(folder, ["diff", "--name-only", c, "HEAD"], ct);
            if (!diff.Ok) continue;
            var n = diff.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            if (closest is null) latest = n;
            if (n < best) { best = n; closest = c; }
        }
        return work with
        {
            SharesHistory = false,
            ClosestCommit = closest,
            ClosestDifferences = closest is null ? 0 : best,
            ClosestIsLatest = closest is not null && closest == work.RemoteSha,
            DifferencesFromLatest = latest,
        };
    }

    /// <summary>
    /// For a folder whose history is unrelated to GitHub's (a downloaded copy turned into a new repository):
    /// keeps every file exactly as it is, and records it as one new commit on top of GitHub's
    /// <paramref name="expectedRemoteSha"/>, reusing the message and author of the folder's latest commit.
    /// The old commits stay on a backup branch. Afterwards a normal push works; nothing on GitHub is replaced.
    /// </summary>
    public async Task<GitResult> PlaceOnTopOfRemoteAsync(string folder, string branch, string expectedRemoteSha, CancellationToken ct = default)
    {
        var status = await shell.RunAsync(folder, ["status", "--porcelain"], ct);
        if (!status.Ok) return status;
        if (status.Output.Trim().Length > 0)
            return new GitResult(1, "", "Commit your changes first, so the new commit contains everything in the folder.");

        var head = await shell.RunAsync(folder, ["rev-parse", "HEAD"], ct);
        if (!head.Ok) return head;
        var oldHead = head.Output.Trim();
        var tree = (await shell.RunAsync(folder, ["rev-parse", "HEAD^{tree}"], ct)).Output.Trim();

        var backup = $"backup/{branch}-before-github-history-{DateTime.Now:yyyyMMdd-HHmmss}";
        var b = await shell.RunAsync(folder, ["branch", backup, oldHead], ct);
        if (!b.Ok) return b;

        var reset = await shell.RunAsync(folder, ["reset", "--soft", expectedRemoteSha], ct);
        if (!reset.Ok) return reset;

        var commit = await shell.RunAsync(folder, ["commit", "-q", "-C", oldHead], ct);
        var same = await shell.RunAsync(folder, ["diff", "--cached", "--quiet"], ct);
        if (!commit.Ok && !same.Ok)
        {
            // Put everything back exactly as it was.
            await shell.RunAsync(folder, ["reset", "--soft", oldHead], ct);
            return commit;
        }

        var newTree = (await shell.RunAsync(folder, ["rev-parse", "HEAD^{tree}"], ct)).Output.Trim();
        if (newTree != tree)
        {
            await shell.RunAsync(folder, ["reset", "--soft", oldHead], ct);
            return new GitResult(1, "", "The files did not come out identical, so nothing was changed.");
        }

        return new GitResult(0,
            (commit.Ok ? "Your files are now one new commit on top of GitHub's history. Press Push to upload it."
                       : "Your files already match GitHub's latest commit, so there is nothing new to push.") +
            $"\nYour previous commits are kept on branch {backup}.", "");
    }

    /// <summary>
    /// Merges GitHub's commits into this folder, also when the two have no shared history (a repository
    /// created with a README). On a conflict the merge is undone and the conflicting files are reported.
    /// </summary>
    public async Task<GitResult> CombineWithRemoteAsync(string folder, string branch, CancellationToken ct = default)
    {
        var merge = await shell.RunAsync(folder,
            ["pull", "--no-rebase", "--no-edit", "--allow-unrelated-histories", Remote, branch], ct);
        if (merge.Ok) return merge;

        var conflicts = await shell.RunAsync(folder, ["diff", "--name-only", "--diff-filter=U"], ct);
        var files = conflicts.Ok ? conflicts.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        if (files.Length == 0) return merge;

        await shell.RunAsync(folder, ["merge", "--abort"], ct);
        return new GitResult(merge.ExitCode, "",
            "Both this folder and GitHub changed the same file(s), so nothing was combined:\n" +
            string.Join("\n", files.Select(f => "  " + f)) +
            "\nYour folder is back as it was. Use one of the other choices in the yellow box instead.");
    }

    /// <summary>
    /// Pushes this folder's branch over GitHub's, but only if GitHub still has exactly
    /// <paramref name="expectedRemoteSha"/> (what the user was shown), so nothing newer is lost.
    /// </summary>
    public Task<GitResult> ReplaceRemoteAsync(string folder, string branch, string expectedRemoteSha, CancellationToken ct = default) =>
        shell.RunAsync(folder, ["push", "-u", $"--force-with-lease={branch}:{expectedRemoteSha}", Remote, branch], ct);

    // ---- parsing (static so it can be tested on its own) ----

    /// <summary>Reads `git status --porcelain=v2 --branch` output.</summary>
    public static RepoStatus ParseStatus(string output)
    {
        string? branch = null, upstream = null;
        bool hasCommits = true;
        int ahead = 0, behind = 0, changed = 0;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("# branch.oid ", StringComparison.Ordinal))
                hasCommits = line[13..] != "(initial)";
            else if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
                branch = line[14..] == "(detached)" ? null : line[14..];
            else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
                upstream = line[18..];
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var m = Regex.Match(line, @"\+(\d+) -(\d+)");
                if (m.Success) { ahead = int.Parse(m.Groups[1].Value); behind = int.Parse(m.Groups[2].Value); }
            }
            else if (!line.StartsWith('#'))
                changed++;
        }
        return new RepoStatus(RepoState.Ready)
        {
            Branch = branch, HasCommits = hasCommits, Upstream = upstream,
            Ahead = ahead, Behind = behind, ChangedFiles = changed,
        };
    }

    /// <summary>
    /// From git's "detected dubious ownership" error, the path it suggests for safe.directory
    /// (for a network share that is the %(prefix)/// form, which only git's own suggestion gets right).
    /// </summary>
    public static string? ParseUnsafePath(string error)
    {
        if (!error.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase)) return null;
        var m = Regex.Match(error, @"safe\.directory\s+'([^']+)'");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static IReadOnlyList<string> ParseAccounts(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.Contains(' ') && !l.EndsWith(':'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool IsValidRemoteUrl(string url) =>
        Regex.IsMatch(url.Trim(), @"^(https://[^\s/]+/\S+|ssh://\S+|file:///\S+|[\w.-]+@[\w.-]+:\S+)$");

    /// <summary>A GitHub repository name from a folder name: letters, digits, '.', '-' and '_'.</summary>
    public static string RepositoryName(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        name = Regex.Replace(name, @"[^A-Za-z0-9._-]+", "-").Trim('-', '.');
        return name.Length == 0 ? "my-project" : name;
    }

    /// <summary>GitHub's "new repository" page with the name filled in and Private selected.</summary>
    public static string NewGitHubRepositoryUrl(string name) =>
        $"https://github.com/new?name={Uri.EscapeDataString(name)}&visibility=private";

    public const string DefaultGitIgnore = """
        # Build output
        bin/
        obj/
        out/
        dist/
        build/
        artifacts/

        # Editors and tools
        .vs/
        .vscode/
        .idea/
        *.user
        *.suo

        # Dependencies
        node_modules/
        packages/

        # Local secrets and environment
        .env
        .env.*
        *.pfx

        # OS files
        Thumbs.db
        .DS_Store

        """;
}
