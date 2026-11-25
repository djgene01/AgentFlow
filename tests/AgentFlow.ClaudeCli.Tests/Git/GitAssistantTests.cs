using AgentFlow.Core.Git;

namespace AgentFlow.ClaudeCli.Tests.Git;

public class GitAssistantTests
{
    [Fact]
    public void ParseStatus_reads_branch_upstream_and_changes()
    {
        var s = GitAssistant.ParseStatus("""
            # branch.oid 3132be9a
            # branch.head main
            # branch.upstream origin/main
            # branch.ab +2 -1
            1 .M N... 100644 100644 100644 abc abc src/a.cs
            ? new.txt
            """);
        Assert.Equal(RepoState.Ready, s.State);
        Assert.Equal("main", s.Branch);
        Assert.True(s.HasCommits);
        Assert.Equal("origin/main", s.Upstream);
        Assert.Equal(2, s.Ahead);
        Assert.Equal(1, s.Behind);
        Assert.Equal(2, s.ChangedFiles);
    }

    [Fact]
    public void ParseStatus_new_repository_has_no_commits_or_upstream()
    {
        var s = GitAssistant.ParseStatus("# branch.oid (initial)\n# branch.head main\n");
        Assert.False(s.HasCommits);
        Assert.Null(s.Upstream);
        Assert.Equal(0, s.ChangedFiles);
    }

    [Fact]
    public void ParseUnsafePath_takes_gits_own_suggestion()
    {
        const string error = """
            fatal: detected dubious ownership in repository at '//server/share/proj'
            To add an exception for this directory, call:

            	git config --global --add safe.directory '%(prefix)///server/share/proj'
            """;
        Assert.Equal("%(prefix)///server/share/proj", GitAssistant.ParseUnsafePath(error));
        Assert.Null(GitAssistant.ParseUnsafePath("fatal: not a git repository"));
    }

    [Theory]
    [InlineData("https://github.com/me/app.git", true)]
    [InlineData("git@github.com:me/app.git", true)]
    [InlineData("ssh://git@host/me/app.git", true)]
    [InlineData("github.com/me/app", false)]
    [InlineData("https://github.com", false)]
    [InlineData("", false)]
    public void IsValidRemoteUrl(string url, bool valid) => Assert.Equal(valid, GitAssistant.IsValidRemoteUrl(url));

    [Theory]
    [InlineData(@"C:\work\My App!", "My-App")]
    [InlineData(@"Z:\x\AgentFlow\", "AgentFlow")]
    [InlineData(@"C:\work\###", "my-project")]
    public void RepositoryName_is_safe_for_github(string folder, string expected) =>
        Assert.Equal(expected, GitAssistant.RepositoryName(folder));

    [Fact]
    public void ParseAccounts_lists_one_account_per_line()
    {
        Assert.Equal(["octocat", "other"], GitAssistant.ParseAccounts("octocat\r\nother\r\n\r\n"));
        Assert.Empty(GitAssistant.ParseAccounts(""));
    }

    [Fact]
    public void Tools_report_credential_manager_helper()
    {
        Assert.True(new GitToolsInfo("2.54", "2.7.3", "manager").UsesCredentialManager);
        Assert.False(new GitToolsInfo("2.54", "2.7.3", "store").UsesCredentialManager);
        Assert.False(new GitToolsInfo("2.54", null, null).UsesCredentialManager);
    }

    [Fact]
    public async Task SignIn_uses_credential_manager_browser_flow()
    {
        var shell = new RecordingShell();
        await new GitAssistant(shell).SignInToGitHubAsync();
        Assert.Equal("credential-manager github login --browser", shell.Calls.Single());
    }

    [Fact]
    public async Task SetRemote_rejects_bad_address_without_running_git()
    {
        var shell = new RecordingShell();
        var r = await new GitAssistant(shell).SetRemoteAsync(Path.GetTempPath(), "not a url");
        Assert.False(r.Ok);
        Assert.Empty(shell.Calls);
    }

    [Fact]
    public async Task Real_git_init_commit_connect_and_push()
    {
        var shell = new GitShell();
        if (!(await shell.RunAsync(null, ["--version"])).Ok) return; // git not installed

        var root = Path.Combine(Path.GetTempPath(), "agentflow-git-" + Guid.NewGuid().ToString("N")[..8]);
        var work = Path.Combine(root, "work");
        var remote = Path.Combine(root, "remote.git");
        Directory.CreateDirectory(work);
        try
        {
            var git = new GitAssistant(shell);
            Assert.Equal(RepoState.NotARepository, (await git.GetStatusAsync(work)).State);

            Assert.True((await git.InitAsync(work)).Ok);
            Assert.True(File.Exists(Path.Combine(work, ".gitignore")));
            await shell.RunAsync(work, ["config", "user.name", "Test"]);
            await shell.RunAsync(work, ["config", "user.email", "test@example.com"]);
            await File.WriteAllTextAsync(Path.Combine(work, "a.txt"), "hello");

            var before = await git.GetStatusAsync(work);
            Assert.Equal(RepoState.Ready, before.State);
            Assert.False(before.HasCommits);
            Assert.Equal(2, before.ChangedFiles); // a.txt and .gitignore

            Assert.True((await git.CommitAllAsync(work, "First")).Ok);
            Assert.True((await shell.RunAsync(null, ["init", "--bare", "-b", "main", remote])).Ok);
            var remoteUrl = new Uri(remote).AbsoluteUri; // file:///…
            Assert.True((await git.SetRemoteAsync(work, remoteUrl)).Ok);
            Assert.True((await git.SetRemoteAsync(work, remoteUrl)).Ok); // second time updates instead of failing

            var push = await git.PushAsync(work, "main");
            Assert.True(push.Ok, push.Text);

            var after = await git.GetStatusAsync(work);
            Assert.Equal("main", after.Branch);
            Assert.Equal("origin/main", after.Upstream);
            Assert.Equal(0, after.Ahead);
            Assert.Equal(0, after.ChangedFiles);
            Assert.Equal(remoteUrl, after.OriginUrl);
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A "GitHub" repository created with a README, and a local folder with its own unrelated first commit.</summary>
    private static async Task<(GitAssistant Git, GitShell Shell, string Root, string Work, string RemoteUrl)?> RepoWithReadmeOnRemoteAsync(string localReadme)
    {
        var shell = new GitShell();
        if (!(await shell.RunAsync(null, ["--version"])).Ok) return null;
        var root = Path.Combine(Path.GetTempPath(), "agentflow-git-" + Guid.NewGuid().ToString("N")[..8]);
        var work = Path.Combine(root, "work");
        var seed = Path.Combine(root, "seed");
        var remote = Path.Combine(root, "remote.git");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(seed);
        async Task Git(string? dir, params string[] a) => Assert.True((await shell.RunAsync(dir, a)).Ok, string.Join(' ', a));

        await Git(null, "init", "--bare", "-b", "main", remote);
        var remoteUrl = new Uri(remote).AbsoluteUri;
        await Git(seed, "init", "-b", "main");
        await Git(seed, "config", "user.name", "Web");
        await Git(seed, "config", "user.email", "web@example.com");
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "# From GitHub\n");
        await Git(seed, "add", "-A");
        await Git(seed, "commit", "-m", "Initial commit");
        await Git(seed, "push", remoteUrl, "main");

        var git = new GitAssistant(shell);
        Assert.True((await git.InitAsync(work)).Ok);
        await Git(work, "config", "user.name", "Test");
        await Git(work, "config", "user.email", "test@example.com");
        await File.WriteAllTextAsync(Path.Combine(work, "app.cs"), "class App {}");
        if (localReadme.Length > 0) await File.WriteAllTextAsync(Path.Combine(work, "README.md"), localReadme);
        Assert.True((await git.CommitAllAsync(work, "Local first")).Ok);
        Assert.True((await git.SetRemoteAsync(work, remoteUrl)).Ok);
        return (git, shell, root, work, remoteUrl);
    }

    private static void DeleteTree(string root)
    {
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Rejected_push_then_combine_then_push_succeeds()
    {
        if (await RepoWithReadmeOnRemoteAsync("") is not var (git, _, root, work, _)) return;
        try
        {
            var push = await git.PushAsync(work, "main");
            Assert.True(GitAssistant.IsRejectedForRemoteWork(push), push.Text);

            var remoteWork = await git.GetRemoteOnlyWorkAsync(work, "main");
            Assert.NotNull(remoteWork.RemoteSha);
            Assert.Contains(remoteWork.Commits, c => c.Contains("Initial commit"));
            Assert.Contains("README.md", remoteWork.Files);

            var combine = await git.CombineWithRemoteAsync(work, "main");
            Assert.True(combine.Ok, combine.Text);
            Assert.True(File.Exists(Path.Combine(work, "README.md")));
            Assert.True(File.Exists(Path.Combine(work, "app.cs")));

            Assert.True((await git.PushAsync(work, "main")).Ok);
            Assert.Equal(0, (await git.GetStatusAsync(work)).Ahead);
        }
        finally { DeleteTree(root); }
    }

    [Fact]
    public async Task Combine_with_conflict_is_undone_and_names_the_file()
    {
        if (await RepoWithReadmeOnRemoteAsync("# Local readme\n") is not var (git, shell, root, work, _)) return;
        try
        {
            await git.PushAsync(work, "main");
            var combine = await git.CombineWithRemoteAsync(work, "main");
            Assert.False(combine.Ok);
            Assert.Contains("README.md", combine.Error);
            Assert.Equal("# Local readme\n", (await File.ReadAllTextAsync(Path.Combine(work, "README.md"))).Replace("\r\n", "\n"));
            Assert.False(File.Exists(Path.Combine(work, ".git", "MERGE_HEAD")));
        }
        finally { DeleteTree(root); }
    }

    [Fact]
    public async Task Replace_only_overwrites_the_version_that_was_shown()
    {
        if (await RepoWithReadmeOnRemoteAsync("") is not var (git, shell, root, work, remoteUrl)) return;
        try
        {
            await git.PushAsync(work, "main");
            var shown = await git.GetRemoteOnlyWorkAsync(work, "main");

            Assert.False((await git.ReplaceRemoteAsync(work, "main", new string('0', 40))).Ok); // stale: refused
            var replace = await git.ReplaceRemoteAsync(work, "main", shown.RemoteSha!);
            Assert.True(replace.Ok, replace.Text);

            var remoteHead = await shell.RunAsync(null, ["ls-remote", remoteUrl, "main"]);
            var localHead = await shell.RunAsync(work, ["rev-parse", "HEAD"]);
            Assert.StartsWith(localHead.Output.Trim(), remoteHead.Output.Trim());
        }
        finally { DeleteTree(root); }
    }

    /// <summary>GitHub has v1 then v2; the folder is a "ZIP download" of <paramref name="downloaded"/> plus one local edit, made into a new repository.</summary>
    private static async Task<(GitAssistant Git, GitShell Shell, string Root, string Work, string RemoteUrl)?> DownloadedCopyAsync(string downloaded)
    {
        var shell = new GitShell();
        if (!(await shell.RunAsync(null, ["--version"])).Ok) return null;
        var root = Path.Combine(Path.GetTempPath(), "agentflow-git-" + Guid.NewGuid().ToString("N")[..8]);
        var work = Path.Combine(root, "work");
        var seed = Path.Combine(root, "seed");
        var remote = Path.Combine(root, "remote.git");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(seed);
        async Task Git(string? dir, params string[] a) => Assert.True((await shell.RunAsync(dir, a)).Ok, string.Join(' ', a));
        async Task Write(string dir, string version)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, ".gitignore"), "bin/\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "a " + version);
            await File.WriteAllTextAsync(Path.Combine(dir, "b.txt"), "b " + version);
            await File.WriteAllTextAsync(Path.Combine(dir, "c.txt"), "c " + version);
        }

        await Git(null, "init", "--bare", "-b", "main", remote);
        var remoteUrl = new Uri(remote).AbsoluteUri;
        await Git(seed, "init", "-b", "main");
        await Git(seed, "config", "user.name", "Web");
        await Git(seed, "config", "user.email", "web@example.com");
        foreach (var v in new[] { "v1", "v2" })
        {
            await Write(seed, v);
            await Git(seed, "add", "-A");
            await Git(seed, "commit", "-m", v);
        }
        await Git(seed, "push", remoteUrl, "main");

        await Write(work, downloaded);
        await File.WriteAllTextAsync(Path.Combine(work, "a.txt"), "a edited locally");
        var git = new GitAssistant(shell);
        Assert.True((await git.InitAsync(work)).Ok);
        await Git(work, "config", "user.name", "Test");
        await Git(work, "config", "user.email", "test@example.com");
        Assert.True((await git.CommitAllAsync(work, "My improvements")).Ok);
        Assert.True((await git.SetRemoteAsync(work, remoteUrl)).Ok);
        return (git, shell, root, work, remoteUrl);
    }

    [Fact]
    public async Task Downloaded_copy_is_detected_and_put_on_top_of_github_history()
    {
        if (await DownloadedCopyAsync("v2") is not var (git, shell, root, work, _)) return;
        try
        {
            Assert.True(GitAssistant.IsRejectedForRemoteWork(await git.PushAsync(work, "main")));
            var w = await git.GetRemoteOnlyWorkAsync(work, "main");
            Assert.False(w.SharesHistory);
            Assert.True(w.ClosestIsLatest);
            Assert.Equal(1, w.DifferencesFromLatest); // only the local edit to a.txt

            var treeBefore = (await shell.RunAsync(work, ["rev-parse", "HEAD^{tree}"])).Output.Trim();
            var r = await git.PlaceOnTopOfRemoteAsync(work, "main", w.RemoteSha!);
            Assert.True(r.Ok, r.Text);
            Assert.Contains("backup/main-before-github-history-", r.Output);

            Assert.Equal(treeBefore, (await shell.RunAsync(work, ["rev-parse", "HEAD^{tree}"])).Output.Trim());
            Assert.Equal(w.RemoteSha, (await shell.RunAsync(work, ["rev-parse", "HEAD~1"])).Output.Trim());
            Assert.Equal("My improvements", (await shell.RunAsync(work, ["log", "-1", "--format=%s"])).Output.Trim());
            Assert.Contains("backup/main-before-github-history-", (await shell.RunAsync(work, ["branch", "--list", "backup/*"])).Output);

            var push = await git.PushAsync(work, "main");
            Assert.True(push.Ok, push.Text);
        }
        finally { DeleteTree(root); }
    }

    [Fact]
    public async Task Downloaded_copy_of_an_older_version_is_flagged()
    {
        if (await DownloadedCopyAsync("v1") is not var (git, _, root, work, _)) return;
        try
        {
            await git.PushAsync(work, "main");
            var w = await git.GetRemoteOnlyWorkAsync(work, "main");
            Assert.False(w.SharesHistory);
            Assert.False(w.ClosestIsLatest);
            Assert.Equal(1, w.ClosestDifferences);
            Assert.Equal(3, w.DifferencesFromLatest);
        }
        finally { DeleteTree(root); }
    }

    [Fact]
    public async Task Put_on_top_refuses_uncommitted_changes()
    {
        if (await DownloadedCopyAsync("v2") is not var (git, shell, root, work, _)) return;
        try
        {
            await git.PushAsync(work, "main");
            var w = await git.GetRemoteOnlyWorkAsync(work, "main");
            var head = (await shell.RunAsync(work, ["rev-parse", "HEAD"])).Output.Trim();
            await File.WriteAllTextAsync(Path.Combine(work, "b.txt"), "unsaved");
            Assert.False((await git.PlaceOnTopOfRemoteAsync(work, "main", w.RemoteSha!)).Ok);
            Assert.Equal(head, (await shell.RunAsync(work, ["rev-parse", "HEAD"])).Output.Trim());
        }
        finally { DeleteTree(root); }
    }

    [Fact]
    public async Task Related_history_is_not_flagged_as_downloaded_copy()
    {
        if (await RepoWithReadmeOnRemoteAsync("") is not var (git, _, root, work, _)) return;
        try
        {
            // Bring GitHub's commit in, add one more locally, then GitHub moves on: the histories are related.
            Assert.True((await git.CombineWithRemoteAsync(work, "main")).Ok);
            var w = await git.GetRemoteOnlyWorkAsync(work, "main");
            Assert.True(w.SharesHistory);
        }
        finally { DeleteTree(root); }
    }

    private sealed class RecordingShell : IGitShell
    {
        public List<string> Calls { get; } = [];

        public Task<GitResult> RunAsync(string? folder, IReadOnlyList<string> args, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls.Add(string.Join(' ', args));
            return Task.FromResult(new GitResult(0, "", ""));
        }
    }
}
