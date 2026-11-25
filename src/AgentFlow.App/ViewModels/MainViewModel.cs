using System.Collections.ObjectModel;
using System.IO;
using AgentFlow.App.Services;
using AgentFlow.App.ViewModels.Builder;
using AgentFlow.Core.Agents;
using AgentFlow.Core.Chat;
using AgentFlow.Core.Workflows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.App.ViewModels;

public sealed partial class ProjectItemViewModel(Project project) : ObservableObject
{
    [ObservableProperty] private Project _project = project;
    [ObservableProperty] private bool _isRunning;

    public string Name => Project.Name;
    public string FolderPath => Project.FolderPath;

    /// <summary>The first letter or digit of the name, for the sidebar avatar.</summary>
    public string Initial => Name.FirstOrDefault(char.IsLetterOrDigit) is var c and not '\0' ? char.ToUpperInvariant(c).ToString() : "•";

    partial void OnProjectChanged(Project value)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Initial));
        OnPropertyChanged(nameof(FolderPath));
    }
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IChatStore _store;
    private readonly IAgentRunner _runner;
    private readonly IDialogService _dialogs;
    private readonly IRunStore _runs;
    private readonly WorkflowLibrary _library;
    private readonly Dictionary<string, ChatViewModel> _chats = [];
    private readonly SoundService? _sounds;

    public ObservableCollection<ProjectItemViewModel> Projects { get; } = [];
    public DiagnosticsViewModel Diagnostics { get; }
    public WorkflowBuilderViewModel Builder { get; }
    public GitViewModel Git { get; }
    public VideoStudioViewModel? Video { get; }
    public bool HasVideo => Video is not null;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RemoveProjectCommand))] private ProjectItemViewModel? _selectedProject;
    [ObservableProperty] private object? _currentPage;

    /// <summary>Which sidebar entry is open: Workflows, Git, Video, Voice, Status, or empty for a project chat.</summary>
    public string ActiveNav => CurrentPage switch
    {
        WorkflowBuilderViewModel => "Workflows",
        GitViewModel => "Git",
        VideoStudioViewModel => "Video",
        StudioAppViewModel s => s.App.Key, // "pixel", "sfx"
        VoiceViewModel => "Voice",
        DiagnosticsViewModel => "Status",
        _ => "",
    };

    partial void OnCurrentPageChanged(object? value) => OnPropertyChanged(nameof(ActiveNav));

    public bool AnyRunning => _chats.Values.Any(c => c.IsRunning);

    /// <summary>Ctrl+1 … Ctrl+9.</summary>
    [RelayCommand]
    private void SelectProjectByNumber(string number)
    {
        if (int.TryParse(number, out var n) && n >= 1 && n <= Projects.Count) SelectedProject = Projects[n - 1];
    }

    /// <summary>False when the permission server could not start; prompts are then denied.</summary>
    public bool ApprovalsAvailable { get; set; } = true;

    public VoiceViewModel? Voice { get; }
    public bool HasVoice => Voice is not null;

    /// <summary>Pixel Art Studio and SfxForge pages (each adds its MCP server to turns when on).</summary>
    public IReadOnlyList<StudioAppViewModel> Studios { get; }
    public StudioAppViewModel? PixelArt => Studios.FirstOrDefault(s => s.App.Key == AgentFlow.Core.Studios.StudioApps.PixelArtStudio.Key);
    public StudioAppViewModel? Sfx => Studios.FirstOrDefault(s => s.App.Key == AgentFlow.Core.Studios.StudioApps.SfxForge.Key);
    public bool HasPixelArt => PixelArt is not null;
    public bool HasSfx => Sfx is not null;

    /// <summary>Every integration's MCP servers and rules, handed to each project chat.</summary>
    private readonly AgentFlow.Core.Agents.IMcpIntegrations? _integrations;

    public MainViewModel(IChatStore store, IRunStore runs, IAgentRunner runner, WorkflowLibrary library, IDialogService dialogs, DiagnosticsViewModel diagnostics, WorkflowBuilderViewModel builder, GitViewModel git, SoundService? sounds = null, VideoStudioViewModel? video = null, VoiceViewModel? voice = null, IEnumerable<StudioAppViewModel>? studios = null)
    {
        _sounds = sounds;
        Video = video;
        Voice = voice;
        Studios = studios?.ToList() ?? [];
        var parts = new List<AgentFlow.Core.Agents.IMcpIntegrations>();
        if (video is not null) parts.Add(video);
        parts.AddRange(Studios);
        _integrations = parts.Count == 0 ? null : new AgentFlow.Core.Agents.CompositeMcpIntegrations(parts);
        if (voice is not null) voice.OpenRequested += ShowVoice;
        Builder = builder;
        Git = git;
        _runs = runs;
        _library = library;
        _store = store;
        _runner = runner;
        _dialogs = dialogs;
        Diagnostics = diagnostics;
        foreach (var p in store.GetProjects())
            Projects.Add(new ProjectItemViewModel(p));
    }

    public async Task InitializeAsync()
    {
        await Diagnostics.RefreshAsync();
        if (!Diagnostics.IsReady || Projects.Count == 0)
            CurrentPage = Diagnostics.IsReady ? new WelcomeViewModel() : Diagnostics;
        else
            SelectedProject = Projects[0];
    }

    partial void OnSelectedProjectChanged(ProjectItemViewModel? value)
    {
        if (value is null) return;
        _lastProject = value;
        CurrentPage = GetChat(value);
    }

    [RelayCommand]
    private void ShowWorkflows()
    {
        var project = SelectedProject ?? _lastProject;
        Builder.Open(project?.FolderPath, project?.Name);
        SelectedProject = null;
        CurrentPage = Builder;
    }

    private ProjectItemViewModel? _lastProject;

    /// <summary>Opens the Git page for the open project (sidebar button and the chat header's Git button).</summary>
    [RelayCommand]
    private void ShowGit()
    {
        var project = SelectedProject ?? _lastProject;
        Git.Open(Projects, project);
        SelectedProject = null;
        CurrentPage = Git;
    }

    [RelayCommand]
    private void ShowVideo()
    {
        if (Video is null) return;
        SelectedProject = null;
        CurrentPage = Video;
    }

    [RelayCommand]
    private void ShowPixelArt() => ShowStudio(PixelArt);

    [RelayCommand]
    private void ShowSfx() => ShowStudio(Sfx);

    private void ShowStudio(StudioAppViewModel? studio)
    {
        if (studio is null) return;
        SelectedProject = null;
        CurrentPage = studio;
    }

    [RelayCommand]
    private void ShowVoice()
    {
        if (Voice is null) return;
        SelectedProject = null;
        CurrentPage = Voice;
    }

    [RelayCommand]
    private void ShowDiagnostics()
    {
        SelectedProject = null;
        CurrentPage = Diagnostics;
        _ = Diagnostics.RefreshAsync();
    }

    [RelayCommand]
    private void AddProject()
    {
        var folder = _dialogs.PickFolder();
        if (folder is null) return;

        var existing = Projects.FirstOrDefault(p => string.Equals(p.FolderPath, folder, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedProject = existing;
            return;
        }

        var ok = _dialogs.Confirm(
            "Add project",
            $"Claude Code will run in:\n{folder}\n\n" +
            "In this mode Claude Code runs the folder's own hooks (.claude/settings.json) and MCP servers (.mcp.json) " +
            "without asking first. Only add folders whose contents you trust.");
        if (!ok) return;

        var now = DateTimeOffset.UtcNow;
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        var project = new Project(Guid.NewGuid().ToString("D"), string.IsNullOrEmpty(name) ? folder : name, folder,
            null, null, null, "acceptEdits", now, now);
        _store.SaveProject(project);
        var item = new ProjectItemViewModel(project);
        Projects.Insert(0, item);
        SelectedProject = item;
    }

    /// <summary>Removes the given project (its row's button or menu), or the selected one.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveProject))]
    private void RemoveProject(ProjectItemViewModel? target)
    {
        if ((target ?? SelectedProject) is not { } item) return;
        if (_chats.TryGetValue(item.Project.Id, out var chat) && chat.IsRunning)
        {
            _dialogs.Inform("Remove project", "Stop the running turn first.");
            return;
        }
        if (!_dialogs.Confirm("Remove project",
                $"Remove '{item.Name}' and its chat history from AgentFlow?\n\nYour folder and Claude Code's own session files are not touched."))
            return;

        _store.DeleteProject(item.Project.Id);
        _chats.Remove(item.Project.Id);
        if (ReferenceEquals(_lastProject, item)) _lastProject = null;
        var wasOpen = ReferenceEquals(SelectedProject, item);
        Projects.Remove(item);
        if (!wasOpen) return; // another page or project stays open
        SelectedProject = Projects.FirstOrDefault();
        if (SelectedProject is null) CurrentPage = new WelcomeViewModel();
    }

    private bool CanRemoveProject(ProjectItemViewModel? target) => (target ?? SelectedProject) is not null;

    public void StopAll()
    {
        foreach (var c in _chats.Values.Where(c => c.IsRunning))
            c.StopCommand.Execute(null);
    }

    private ChatViewModel GetChat(ProjectItemViewModel item)
    {
        if (_chats.TryGetValue(item.Project.Id, out var vm)) return vm;

        var chat = new ProjectChat(_store.GetProject(item.Project.Id) ?? item.Project, _store, _runner, _runs,
            approvalPolicy: () => ApprovalsAvailable ? Diagnostics.ApprovalPolicy : AgentFlow.Core.Agents.ApprovalPolicy.Deny,
            integrations: _integrations);
        _sounds?.Watch(chat);
        Voice?.Watch(chat);
        chat.ProjectChanged += p => System.Windows.Application.Current.Dispatcher.BeginInvoke(() => item.Project = p);
        vm = new ChatViewModel(chat, _library, _dialogs) { Voice = Voice };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.IsRunning)) item.IsRunning = vm.IsRunning;
        };
        _chats[item.Project.Id] = vm;
        return vm;
    }
}

public sealed class WelcomeViewModel;
