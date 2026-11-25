using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using AgentFlow.App.Services;
using AgentFlow.Core.Git;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.App.ViewModels;

/// <summary>One line of the step list at the top of the Git page.</summary>
public sealed partial class GitStepViewModel(string title) : ObservableObject
{
    public string Title { get; } = title;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Mark))] private bool _isDone;
    [ObservableProperty] private string _detail = "";
    public string Mark => IsDone ? "✓" : "○";
}

/// <summary>
/// The Git page: checks git, sets your name and email, signs you in to GitHub through Git Credential
/// Manager's own browser flow, and connects, commits and pushes a project. Credentials stay with Git
/// Credential Manager (Windows Credential Manager); AgentFlow never sees them.
/// </summary>
public sealed partial class GitViewModel : ObservableObject
{
    private readonly GitAssistant _git;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _signInCts;

    private readonly SoundService? _sounds;

    public GitViewModel(GitAssistant git, IDialogService dialogs, SoundService? sounds = null)
    {
        _sounds = sounds;
        _git = git;
        _dialogs = dialogs;
        Steps = [StepGit, StepIdentity, StepGitHub, StepRepo, StepRemote, StepPushed];
    }

    public ObservableCollection<ProjectItemViewModel> Projects { get; } = [];
    public ObservableCollection<string> Accounts { get; } = [];

    public GitStepViewModel StepGit { get; } = new("Git is installed");
    public GitStepViewModel StepIdentity { get; } = new("Your name and email are set");
    public GitStepViewModel StepGitHub { get; } = new("Signed in to GitHub");
    public GitStepViewModel StepRepo { get; } = new("The project is a git repository");
    public GitStepViewModel StepRemote { get; } = new("The project is connected to GitHub");
    public GitStepViewModel StepPushed { get; } = new("Everything is pushed");
    public IReadOnlyList<GitStepViewModel> Steps { get; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isSigningIn;
    [ObservableProperty] private string _activity = "";

    // ---- this computer ----
    [ObservableProperty] private bool _hasGit;
    [ObservableProperty] private bool _hasCredentialManager;
    [ObservableProperty] private string _toolsText = "Checking…";
    [ObservableProperty] private string _helperWarning = "";

    // ---- identity ----
    [ObservableProperty] private string _identityName = "";
    [ObservableProperty] private string _identityEmail = "";

    // ---- project ----
    [ObservableProperty] private ProjectItemViewModel? _selectedProject;
    [ObservableProperty] private RepoStatus? _status;
    [ObservableProperty] private string _repoText = "";
    [ObservableProperty] private string _repoFolder = "";
    [ObservableProperty] private string _remoteUrlInput = "";
    [ObservableProperty] private string _commitMessage = "";

    public bool HasProject => SelectedProject is not null;
    public bool ShowInit => Status?.State == RepoState.NotARepository;
    public bool ShowTrust => Status?.State == RepoState.Unsafe;
    public bool IsRepo => Status?.State == RepoState.Ready;
    public bool HasRemote => IsRepo && Status?.OriginUrl is not null;
    public bool HasChanges => IsRepo && Status?.ChangedFiles > 0;
    public bool CanPush => HasRemote && Status is { HasCommits: true, Branch: not null };
    public bool IsBehind => HasRemote && Status?.Behind > 0;
    public bool CanPull => HasRemote && Status?.Upstream is not null;
    public string? OriginUrl => Status?.OriginUrl;
    public string PushLabel => Status is { Upstream: null } ? "Push to GitHub (first time)" : $"Push {Status?.Ahead ?? 0} commit(s)";

    partial void OnStatusChanged(RepoStatus? value)
    {
        foreach (var name in new[] { nameof(ShowInit), nameof(ShowTrust), nameof(IsRepo), nameof(HasRemote), nameof(HasChanges),
                     nameof(CanPush), nameof(IsBehind), nameof(CanPull), nameof(OriginUrl), nameof(PushLabel) })
            OnPropertyChanged(name);
    }

    partial void OnSelectedProjectChanged(ProjectItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasProject));
        RemoteUrlInput = "";
        CommitMessage = "";
        Rejected = null;
        _ = RefreshProjectAsync();
    }

    /// <summary>Called by the main window when the page opens, with the project that was open.</summary>
    public void Open(IEnumerable<ProjectItemViewModel> projects, ProjectItemViewModel? project)
    {
        Projects.Clear();
        foreach (var p in projects) Projects.Add(p);
        var target = project is null ? Projects.FirstOrDefault() : Projects.FirstOrDefault(p => p.Project.Id == project.Project.Id);
        if (!ReferenceEquals(target, SelectedProject)) SelectedProject = target;
        _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var tools = await _git.GetToolsAsync();
        HasGit = tools.HasGit;
        HasCredentialManager = tools.HasCredentialManager;
        StepGit.IsDone = tools.HasGit;
        if (!tools.HasGit)
        {
            ToolsText = "Git was not found. Install Git for Windows (it includes Git Credential Manager), then press Check again.";
            HelperWarning = "";
            StepGit.Detail = "Not installed";
            await RefreshProjectAsync();
            return;
        }

        ToolsText = $"Git {tools.GitVersion}" + (tools.HasCredentialManager ? $", Git Credential Manager {tools.CredentialManagerVersion}" : ", Git Credential Manager not found");
        StepGit.Detail = $"Git {tools.GitVersion}";
        HelperWarning = !tools.HasCredentialManager
            ? "Git Credential Manager is missing, so AgentFlow cannot sign you in. Reinstall Git for Windows with its default options."
            : !tools.UsesCredentialManager
                ? $"Git is set to keep passwords with \"{tools.CredentialHelper ?? "nothing"}\" instead of Git Credential Manager, so pushes may ask for a password in a hidden prompt and fail. " +
                  "Press \"Use Git Credential Manager\" to fix that."
                : "";

        var id = await _git.GetGlobalIdentityAsync();
        IdentityName = id.Name ?? IdentityName;
        IdentityEmail = id.Email ?? IdentityEmail;
        StepIdentity.IsDone = id.IsComplete;
        StepIdentity.Detail = id.IsComplete ? $"{id.Name} <{id.Email}>" : "Needed before you can commit";

        await RefreshAccountsAsync();
        await RefreshProjectAsync();
    }

    private async Task RefreshAccountsAsync()
    {
        Accounts.Clear();
        if (!HasCredentialManager) { StepGitHub.IsDone = false; StepGitHub.Detail = "Needs Git Credential Manager"; return; }
        foreach (var a in await _git.ListGitHubAccountsAsync()) Accounts.Add(a);
        StepGitHub.IsDone = Accounts.Count > 0;
        StepGitHub.Detail = Accounts.Count > 0 ? string.Join(", ", Accounts) : "Not signed in";
    }

    private async Task RefreshProjectAsync()
    {
        if (SelectedProject is not { } project || !HasGit)
        {
            Status = null;
            RepoText = HasGit ? "Add a project first." : "";
            RepoFolder = "";
            UpdateProjectSteps();
            return;
        }

        var folder = project.FolderPath;
        var status = await _git.GetStatusAsync(folder);
        if (!ReferenceEquals(project, SelectedProject)) return; // switched while checking
        Status = status;
        RepoFolder = status.State == RepoState.Ready ? await _git.GetTopLevelAsync(folder) ?? folder : folder;
        if (string.IsNullOrEmpty(RemoteUrlInput) && status.OriginUrl is not null) RemoteUrlInput = status.OriginUrl;

        RepoText = status.State switch
        {
            RepoState.NoFolder => "The project folder does not exist.",
            RepoState.Unsafe => status.Problem ?? "Git does not trust this folder.",
            RepoState.NotARepository => status.Problem is null ? "This folder is not a git repository yet." : "Git could not read this folder: " + status.Problem,
            _ => DescribeReady(status),
        };
        UpdateProjectSteps();
    }

    private static string DescribeReady(RepoStatus s)
    {
        var parts = new List<string> { s.Branch is null ? "No branch checked out" : $"Branch {s.Branch}" };
        if (!s.HasCommits) parts.Add("no commits yet");
        parts.Add(s.ChangedFiles == 0 ? "no uncommitted changes" : $"{s.ChangedFiles} file(s) to commit");
        if (s.Upstream is not null)
            parts.Add(s.Ahead == 0 && s.Behind == 0 ? "up to date with GitHub" : $"{s.Ahead} to push, {s.Behind} to pull");
        else if (s.OriginUrl is not null)
            parts.Add("not pushed yet");
        return string.Join(" · ", parts);
    }

    private void UpdateProjectSteps()
    {
        var s = Status;
        StepRepo.IsDone = s?.State == RepoState.Ready;
        StepRepo.Detail = s?.State switch
        {
            RepoState.Ready => s.Branch is null ? "Repository" : $"On branch {s.Branch}",
            RepoState.Unsafe => "Git needs you to allow this folder",
            RepoState.NotARepository => "Not yet",
            RepoState.NoFolder => "Folder missing",
            _ => "",
        };
        StepRemote.IsDone = s?.State == RepoState.Ready && s.OriginUrl is not null;
        StepRemote.Detail = s?.OriginUrl ?? "Not yet";
        StepPushed.IsDone = s is { State: RepoState.Ready, Upstream: not null, Ahead: 0, ChangedFiles: 0 };
        StepPushed.Detail = s switch
        {
            null or { State: not RepoState.Ready } => "",
            { HasCommits: false } => "Commit first",
            { Upstream: null } => "Not pushed yet",
            { ChangedFiles: > 0 } => $"{s.ChangedFiles} change(s) not committed",
            { Ahead: > 0 } => $"{s.Ahead} commit(s) to push",
            _ => "Up to date",
        };
    }

    // ---- actions ----

    private async Task<GitResult?> RunAsync(string title, Func<Task<GitResult>> action, bool refresh = true)
    {
        if (IsBusy) return null;
        IsBusy = true;
        GitResult? result = null;
        try
        {
            Log($"› {title}");
            var r = result = await action();
            var text = r.Text;
            if (text.Length > 0) Log(text);
            if (!r.Ok)
            {
                Log(Hint(r) is { } hint ? "✗ " + hint : "✗ Failed.");
                AppLog.Write($"Git: {title} failed ({r.ExitCode}): {r.Error.Trim()}");
            }
            else Log("✓ Done.");
            _sounds?.PlayGit(r.Ok);
        }
        catch (Exception ex)
        {
            Log("✗ " + ex.Message);
            AppLog.Write($"Git: {title}", ex);
            _sounds?.PlayGit(false);
        }
        finally
        {
            IsBusy = false;
        }
        if (refresh) await RefreshAsync();
        return result;
    }

    /// <summary>Plain-language advice for the failures people actually hit.</summary>
    private static string? Hint(GitResult r)
    {
        var e = r.Error;
        if (GitAssistant.IsRejectedForRemoteWork(r))
            return "GitHub has commits this folder does not have (for example a README created with the repository). " +
                   "See \"GitHub has work this folder does not have\" above.";
        if (e.Contains("Repository not found", StringComparison.OrdinalIgnoreCase))
            return "GitHub did not find that repository. Check the address, and that the signed-in account can see it.";
        if (e.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) || e.Contains("could not read Username", StringComparison.OrdinalIgnoreCase))
            return "Sign-in failed. Use \"Sign in with browser\" above, then try again.";
        if (e.Contains("Permission to", StringComparison.OrdinalIgnoreCase) && e.Contains("denied", StringComparison.OrdinalIgnoreCase))
            return "The signed-in GitHub account cannot push to this repository.";
        if (e.Contains("Please tell me who you are", StringComparison.OrdinalIgnoreCase))
            return "Set your name and email above first.";
        if (e.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) || r.Output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
            return "There is nothing to commit.";
        return null;
    }

    private void Log(string line) => Activity = (Activity.Length == 0 ? "" : Activity + "\n") + line.TrimEnd();

    [RelayCommand]
    private void ClearActivity() => Activity = "";

    [RelayCommand]
    private static void OpenGitDownload() =>
        Process.Start(new ProcessStartInfo("https://git-scm.com/download/win") { UseShellExecute = true });

    [RelayCommand]
    private async Task UseCredentialManagerAsync()
    {
        if (!_dialogs.Confirm("Use Git Credential Manager",
                "This sets your global git setting credential.helper to \"manager\", so git keeps GitHub sign-ins in " +
                "Windows Credential Manager.\n\nContinue?")) return;
        await RunAsync("Use Git Credential Manager", () => _git.UseCredentialManagerAsync());
    }

    [RelayCommand]
    private async Task SaveIdentityAsync()
    {
        if (string.IsNullOrWhiteSpace(IdentityName) || !IdentityEmail.Contains('@'))
        {
            _dialogs.Inform("Name and email", "Enter a name and an email address.");
            return;
        }
        await RunAsync($"Set your git name and email to {IdentityName.Trim()} <{IdentityEmail.Trim()}>",
            () => _git.SetGlobalIdentityAsync(IdentityName, IdentityEmail));
    }

    [RelayCommand]
    private static void OpenEmailSettings() =>
        Process.Start(new ProcessStartInfo("https://github.com/settings/emails") { UseShellExecute = true });

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsBusy) return;
        _signInCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        IsSigningIn = true;
        try
        {
            Log("› Sign in to GitHub: finish the sign-in in your browser (GitHub's own page).");
            var r = await _git.SignInToGitHubAsync(_signInCts.Token);
            if (r.Text.Length > 0) Log(r.Text);
            Log(r.Ok ? "✓ Signed in. Git Credential Manager keeps the sign-in in Windows Credential Manager." : "✗ Sign-in did not finish.");
        }
        catch (OperationCanceledException)
        {
            Log("Sign-in cancelled.");
        }
        catch (Exception ex)
        {
            Log("✗ " + ex.Message);
            AppLog.Write("Git sign-in", ex);
        }
        finally
        {
            IsSigningIn = false;
            _signInCts.Dispose();
            _signInCts = null;
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private void CancelSignIn() => _signInCts?.Cancel();

    [RelayCommand]
    private async Task SignOutAsync(string? account)
    {
        if (account is null) return;
        if (!_dialogs.Confirm("Sign out of GitHub",
                $"Remove the GitHub sign-in for {account} from Git Credential Manager?\n\nPushing will ask you to sign in again.")) return;
        await RunAsync($"Sign out {account}", () => _git.SignOutOfGitHubAsync(account));
    }

    [RelayCommand]
    private async Task InitAsync()
    {
        if (SelectedProject is not { } p) return;
        await RunAsync($"Make {p.FolderPath} a git repository (branch main)", () => _git.InitAsync(p.FolderPath));
    }

    [RelayCommand]
    private async Task TrustFolderAsync()
    {
        if (Status?.SafeDirectoryPath is not { } path) return;
        if (!_dialogs.Confirm("Allow this folder",
                $"Git refuses to work in folders owned by another account or on a network drive until you allow them.\n\n" +
                $"This adds the following to your global git settings (safe.directory):\n{path}\n\nOnly allow folders whose contents you trust.")) return;
        await RunAsync("Allow this folder in git", () => _git.TrustFolderAsync(path));
    }

    [RelayCommand]
    private void CreateOnGitHub()
    {
        if (SelectedProject is not { } p) return;
        var name = GitAssistant.RepositoryName(p.FolderPath);
        Log($"› Opened GitHub's \"Create a new repository\" page for '{name}'. Leave README, .gitignore and license unticked, " +
            "press Create repository, then copy the HTTPS address into the box below.");
        Process.Start(new ProcessStartInfo(GitAssistant.NewGitHubRepositoryUrl(name)) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task ConnectRemoteAsync()
    {
        if (SelectedProject is not { } p) return;
        var url = RemoteUrlInput.Trim();
        if (Status?.OriginUrl is { } existing && !string.Equals(existing, url, StringComparison.OrdinalIgnoreCase)
            && !_dialogs.Confirm("Change the GitHub address", $"This project pushes to:\n{existing}\n\nChange it to:\n{url}?")) return;
        await RunAsync($"Connect to {url}", () => _git.SetRemoteAsync(p.FolderPath, url));
    }

    [RelayCommand]
    private async Task CommitAsync()
    {
        if (SelectedProject is not { } p) return;
        var id = await _git.GetEffectiveIdentityAsync(p.FolderPath);
        if (!id.IsComplete)
        {
            _dialogs.Inform("Commit", "Set your name and email first (step 2).");
            return;
        }
        var message = CommitMessage;
        await RunAsync($"Commit {Status?.ChangedFiles ?? 0} changed file(s) as {id.Name}", () => _git.CommitAllAsync(p.FolderPath, message));
        if (Status is { ChangedFiles: 0 }) CommitMessage = "";
    }

    [RelayCommand]
    private async Task PushAsync()
    {
        if (SelectedProject is not { } p || Status is not { Branch: { } branch, OriginUrl: { } url }) return;
        if (!_dialogs.Confirm("Push to GitHub",
                $"Push branch '{branch}' of {p.Name} to:\n{url}\n\n" +
                (Status.Upstream is null ? "This is the first push of this branch." : $"{Status.Ahead} commit(s) will be uploaded.") +
                (Status.ChangedFiles > 0 ? $"\n\n{Status.ChangedFiles} changed file(s) are not committed and will not be pushed." : "") +
                "\n\nIf you are not signed in, Git Credential Manager opens GitHub's sign-in.")) return;
        var r = await RunAsync($"Push {branch} to {url}", () => _git.PushAsync(p.FolderPath, branch));
        if (r is not null && GitAssistant.IsRejectedForRemoteWork(r)) await LoadRemoteWorkAsync(p, branch);
        else if (r?.Ok == true) Rejected = null;
    }

    // ---- push rejected: GitHub has commits this folder lacks ----

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasRejected), nameof(RejectedText), nameof(IsUnrelated), nameof(UnrelatedText), nameof(SharesHistory))]
    private RemoteWork? _rejected;
    public bool HasRejected => Rejected is not null;
    public bool IsUnrelated => Rejected is { SharesHistory: false };
    public bool SharesHistory => Rejected is { SharesHistory: true };

    public string UnrelatedText => Rejected is not { SharesHistory: false } w ? "" :
        "This folder's history is not connected to GitHub's: it looks like a downloaded copy (for example a ZIP) " +
        "that was made into a new repository. Combining would clash on every file you edited.\n\n" +
        (w.ClosestIsLatest
            ? $"Its files match GitHub's latest commit except for {w.DifferencesFromLatest} file(s), which look like your own changes. "
            : w.ClosestCommit is { } c
                ? $"Careful: its files are closest to an older GitHub commit ({c[..7]}, {w.ClosestDifferences} file(s) differ), not the latest " +
                  $"({w.DifferencesFromLatest} differ). Putting it on top would also undo GitHub's later changes in those files. "
                : "") +
        "\"Put my work on top of GitHub's\" keeps your files exactly as they are and records them as one new commit after " +
        "GitHub's latest, so a normal push adds it and nothing on GitHub is lost. Your current commits stay on a backup branch.";

    public string RejectedText => Rejected is not { } w ? "" :
        (w.Commits.Count == 0 ? "GitHub's branch has commits this folder does not have." :
            $"GitHub has {w.Commits.Count} commit(s) this folder does not have:\n" + string.Join("\n", w.Commits.Take(8).Select(c => "  • " + c)) +
            (w.Commits.Count > 8 ? $"\n  … and {w.Commits.Count - 8} more" : "")) +
        (w.Files.Count is > 0 and <= 12 ? "\nFiles on GitHub: " + string.Join(", ", w.Files) : "");

    private async Task LoadRemoteWorkAsync(ProjectItemViewModel p, string branch)
    {
        var work = await _git.GetRemoteOnlyWorkAsync(p.FolderPath, branch);
        if (!work.Fetch.Ok) { Log("✗ Could not read GitHub's branch: " + work.Fetch.Text); return; }
        Rejected = work;
    }

    [RelayCommand]
    private async Task CombineWithGitHubAsync()
    {
        if (SelectedProject is not { } p || Status?.Branch is not { } branch) return;
        var r = await RunAsync($"Combine GitHub's commits into {branch}", () => _git.CombineWithRemoteAsync(p.FolderPath, branch));
        if (r?.Ok != true) return;
        Rejected = null;
        Log("Combined. Press Push to upload.");
    }

    [RelayCommand]
    private async Task PlaceOnTopAsync()
    {
        if (SelectedProject is not { } p || Status?.Branch is not { } branch || Rejected is not { SharesHistory: false, RemoteSha: { } sha } w) return;
        var message = $"Record this folder's files as one new commit on top of GitHub's latest commit ({sha[..7]})?\n\n" +
                      $"Your files do not change. {w.DifferencesFromLatest} file(s) differ from GitHub and will be in the new commit, " +
                      "with the message and author of your latest commit. Your current commits are kept on a backup branch.\n\n" +
                      "Nothing is uploaded until you press Push.";
        if (!w.ClosestIsLatest && w.ClosestCommit is not null)
            message = "Your folder looks like it came from an OLDER GitHub version, so this commit would also undo GitHub's " +
                      "later changes in the differing files.\n\n" + message;
        if (!_dialogs.Confirm("Put my work on top of GitHub's", message)) return;
        var r = await RunAsync($"Put this folder's files on top of GitHub's {branch}", () => _git.PlaceOnTopOfRemoteAsync(p.FolderPath, branch, sha));
        if (r?.Ok == true) Rejected = null;
    }

    [RelayCommand]
    private async Task ReplaceGitHubAsync()
    {
        if (SelectedProject is not { } p || Status is not { Branch: { } branch, OriginUrl: { } url } || Rejected?.RemoteSha is not { } sha) return;
        var lost = Rejected.Commits.Count == 0 ? "GitHub's commits on this branch" : string.Join("\n", Rejected.Commits.Take(8).Select(c => "  • " + c));
        if (!_dialogs.Confirm("Replace GitHub's version",
                $"This overwrites branch '{branch}' on\n{url}\nwith this folder's version. These commits on GitHub will no longer be part of the branch:\n\n{lost}\n\n" +
                "It only goes ahead if GitHub has not changed since you were shown this. This cannot be undone from AgentFlow.\n\nReplace it?")) return;
        var r = await RunAsync($"Replace {branch} on GitHub with this folder's version", () => _git.ReplaceRemoteAsync(p.FolderPath, branch, sha));
        if (r?.Ok == true) Rejected = null;
    }

    [RelayCommand]
    private async Task PullAsync()
    {
        if (SelectedProject is not { } p) return;
        await RunAsync("Pull from GitHub (fast-forward only)", () => _git.PullAsync(p.FolderPath));
    }

    [RelayCommand]
    private void OpenOnGitHub()
    {
        if (OriginUrl is not { } url) return;
        var web = url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase) ? "https://github.com/" + url[15..] : url;
        if (web.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) web = web[..^4];
        if (web.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo(web) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenRepoFolder()
    {
        if (Directory.Exists(RepoFolder)) Process.Start(new ProcessStartInfo(RepoFolder) { UseShellExecute = true });
    }
}
