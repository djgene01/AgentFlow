using System.Collections.ObjectModel;
using AgentFlow.Core.Agents;
using AgentFlow.Core.Chat;
using AgentFlow.Core.Workflows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.App.ViewModels;

/// <summary>A workflow in the composer's picker; File is null for direct chat.</summary>
public sealed record WorkflowOption(string Label, WorkflowFile? File)
{
    public override string ToString() => Label;
}

/// <summary>The one chat of one project. Kept alive while the app runs, so a turn continues in the background.</summary>
public sealed partial class ChatViewModel : ObservableObject, IGateResponder, IRunActions, IApprovalResponder
{
    private readonly ProjectChat _chat;
    private readonly WorkflowLibrary _library;
    private readonly AgentFlow.App.Services.IDialogService? _dialogs;
    private readonly Dictionary<Guid, EntryViewModel> _byId = [];
    private readonly Dictionary<string, ToolCallViewModel> _toolsByUseId = new(StringComparer.Ordinal);

    public ObservableCollection<EntryViewModel> Entries { get; } = [];

    private readonly DateTimeOffset _openedAt = DateTimeOffset.UtcNow; // older errors were logged when they happened
    public ObservableCollection<WorkflowOption> Workflows { get; } = [];

    public IReadOnlyList<Choice> Models => Choices.Models;
    public IReadOnlyList<Choice> Efforts => Choices.Efforts;
    public IReadOnlyList<Choice> PermissionModes => Choices.PermissionModes;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private string _draft = "";
    [ObservableProperty] private Choice _selectedModel;
    [ObservableProperty] private Choice _selectedEffort;
    [ObservableProperty] private Choice _selectedPermissionMode;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private WorkflowOption? _selectedWorkflow;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand), nameof(NewSessionCommand), nameof(OpenInTerminalCommand))] private bool _isRunning;
    [ObservableProperty] private string _sessionText = "";
    [ObservableProperty] private string _usageText = "";
    [ObservableProperty] private RunPanelViewModel? _runPanel;
    [ObservableProperty] private string _workflowHint = "";

    /// <summary>Push-to-talk; null when voice input is not available.</summary>
    public VoiceViewModel? Voice { get; init; }

    public bool IsWorkflowMode => SelectedWorkflow?.File is not null;
    public string SendLabel => IsRunning ? "Queue" : IsWorkflowMode ? "Run workflow" : "Send";
    public string ProjectName => _chat.Project.Name;
    public string FolderPath => _chat.Project.FolderPath;
    public string ProjectId => _chat.Project.Id;

    /// <summary>Raised when the view should scroll to the newest entry.</summary>
    public event Action? ContentGrew;

    public ChatViewModel(ProjectChat chat, WorkflowLibrary library, AgentFlow.App.Services.IDialogService? dialogs = null)
    {
        _chat = chat;
        _library = library;
        _selectedModel = Choices.Find(Choices.Models, chat.Project.LastModel);
        _selectedEffort = Choices.Find(Choices.Efforts, chat.Project.LastEffort);
        _selectedPermissionMode = Choices.Find(Choices.PermissionModes, chat.Project.LastPermissionMode ?? "acceptEdits");

        foreach (var e in chat.LoadHistory())
            AddEntry(e);

        chat.EntryAdded += e => OnUi(() => AddEntry(e));
        chat.EntryUpdated += e => OnUi(() => UpdateEntry(e));
        chat.UsageReported += rl => OnUi(() => UsageText = FormatUsage(rl));
        chat.RunningChanged += () => OnUi(() => { IsRunning = chat.IsRunning; RefreshActions(); });
        _dialogs = dialogs;
        chat.ProjectChanged += _ => OnUi(() => { UpdateSessionText(); OpenInTerminalCommand.NotifyCanExecuteChanged(); });
        chat.WorkflowStarted += engine => OnUi(() => AttachRun(engine));
        library.Changed += () => OnUi(RefreshWorkflows);
        UpdateSessionText();
        RefreshWorkflows();
        OfferRecovery();
        RefreshActions();
        RefreshAllowRules();
    }

    partial void OnSelectedWorkflowChanged(WorkflowOption? value)
    {
        OnPropertyChanged(nameof(IsWorkflowMode));
        OnPropertyChanged(nameof(SendLabel));
        WorkflowHint = value?.File switch
        {
            null => "Enter to send, Shift+Enter for a new line",
            { IsValid: false } f => "This workflow has errors: " + string.Join(" ", f.Errors.Take(2)),
            { Definition: { } d } => $"Describe the task. Stages: {string.Join(" → ", d.Stages.Select(s => s.Name))}. Enter to run.",
            _ => "",
        };
    }

    [RelayCommand]
    private void RefreshWorkflows()
    {
        var selected = SelectedWorkflow?.File?.Path;
        Workflows.Clear();
        Workflows.Add(new WorkflowOption("Direct chat", null));
        foreach (var f in _library.List(_chat.Project.FolderPath))
            Workflows.Add(new WorkflowOption(f.Scope == "project" ? $"{f.Name} (this project)" : f.Name, f));
        SelectedWorkflow = Workflows.FirstOrDefault(w => w.File?.Path == selected) ?? Workflows[0];
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        Draft = "";
        if (IsRunning)
        {
            // Goes to the working stage, the notes at a gate, or the next direct turn.
            _chat.Enqueue(text);
            return;
        }
        if (SelectedWorkflow?.File is { Definition: { } wf })
        {
            // Re-read the file so edits made since the app started are used; the run keeps a snapshot.
            var fresh = _library.List(_chat.Project.FolderPath).FirstOrDefault(f => f.Path == SelectedWorkflow.File.Path);
            await _chat.RunWorkflowAsync(fresh?.Definition ?? wf, text);
        }
        else
        {
            await _chat.SendAsync(text, new DirectChatOptions(SelectedModel.Value, SelectedEffort.Value, SelectedPermissionMode.Value ?? "acceptEdits"));
        }
    }

    private bool CanSend() => !string.IsNullOrWhiteSpace(Draft) && (IsRunning || SelectedWorkflow?.File is null or { IsValid: true });

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(SendLabel));

    // ---- search ----

    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _searchStatus = "";
    private List<EntryViewModel> _hits = [];
    private int _hitIndex = -1;

    /// <summary>Raised when the view should bring an entry into view.</summary>
    public event Action<EntryViewModel>? ScrollToEntry;

    /// <summary>Raised for Ctrl+F (true) and Ctrl+L (false): focus the search box or the message box.</summary>
    public event Action<bool>? FocusRequested;

    [RelayCommand]
    private void OpenSearch()
    {
        IsSearchOpen = true;
        FocusRequested?.Invoke(true);
    }

    [RelayCommand]
    private void CloseSearch()
    {
        IsSearchOpen = false;
        SearchText = "";
        FocusRequested?.Invoke(false);
    }

    [RelayCommand]
    private void FocusComposer() => FocusRequested?.Invoke(false);

    partial void OnSearchTextChanged(string value) => RunSearch(keepPosition: false);

    [RelayCommand]
    private void NextMatch() => Step(+1);

    [RelayCommand]
    private void PreviousMatch() => Step(-1);

    private void RunSearch(bool keepPosition)
    {
        var current = _hitIndex >= 0 && _hitIndex < _hits.Count ? _hits[_hitIndex] : null;
        foreach (var e in _hits) { e.IsSearchHit = false; e.IsCurrentHit = false; }
        var q = SearchText.Trim();
        _hits = q.Length == 0 ? [] : Entries.Where(e => ChatText.Matches(e.Entry.Data, q)).ToList();
        foreach (var e in _hits) e.IsSearchHit = true;
        // Start at the newest match, like scrolling up from the bottom of the chat.
        _hitIndex = _hits.Count == 0 ? -1 : keepPosition && current is not null && _hits.Contains(current) ? _hits.IndexOf(current) : _hits.Count - 1;
        ShowHit();
    }

    private void Step(int delta)
    {
        if (_hits.Count == 0) return;
        if (_hitIndex >= 0) _hits[_hitIndex].IsCurrentHit = false;
        _hitIndex = (_hitIndex + delta + _hits.Count) % _hits.Count;
        ShowHit();
    }

    private void ShowHit()
    {
        SearchStatus = SearchText.Trim().Length == 0 ? "" : _hits.Count == 0 ? "No matches" : $"{_hitIndex + 1} of {_hits.Count}";
        if (_hitIndex < 0) return;
        _hits[_hitIndex].IsCurrentHit = true;
        ScrollToEntry?.Invoke(_hits[_hitIndex]);
    }

    // ---- export ----

    [RelayCommand]
    private void ExportChat() =>
        Export($"{ProjectName} chat", WorkflowEdits.Slug(ProjectName) + "-chat.md", _chat.LoadHistory());

    public void ExportRun(string runId)
    {
        var history = _chat.LoadHistory();
        var run = ChatText.RunEntries(history, runId);
        var name = run.Select(e => e.Data).OfType<RunStatusData>().FirstOrDefault()?.WorkflowName ?? "workflow";
        Export($"{name} run in {ProjectName}", $"{WorkflowEdits.Slug(name)}-run-{run.FirstOrDefault()?.CreatedAt.ToLocalTime():yyyyMMdd-HHmm}.md", run);
    }

    private void Export(string title, string fileName, IEnumerable<ChatEntry> entries)
    {
        if (_dialogs?.SaveFile("Export to Markdown", fileName, "Markdown (*.md)|*.md") is not { } path) return;
        System.IO.File.WriteAllText(path, ChatText.ToMarkdown(title, FolderPath, entries));
    }

    // ---- open in a terminal ----

    [RelayCommand(CanExecute = nameof(CanOpenInTerminal))]
    private void OpenInTerminal()
    {
        if (_chat.Project.LastSessionId is not { } session || AgentFlow.ClaudeCli.ClaudeCliLocator.Find() is not { } exe) return;
        if (_dialogs is not null && !_dialogs.Confirm("Open in a terminal",
                "This opens Claude Code itself in a terminal, continuing this project's session.\n\n" +
                "While you work there, don't send messages from AgentFlow: both would write to the same session. " +
                "When you come back, AgentFlow's next message continues from where the terminal left off."))
            return;
        AgentFlow.App.Services.TerminalLauncher.OpenClaudeSession(exe, FolderPath, session);
    }

    private bool CanOpenInTerminal() => !IsRunning && _chat.Project.LastSessionId is not null;

    // ---- approvals ----

    public bool IsApprovalPending(Guid entryId) => _chat.IsApprovalPending(entryId);

    public bool ResolveApproval(Guid entryId, AgentFlow.Core.Agents.ApprovalAnswer answer)
    {
        var ok = _chat.ResolveApproval(entryId, answer);
        if (ok) OnUi(() => BusyText = WorkingText);
        return ok;
    }

    /// <summary>Rules saved with "Always allow" for this project.</summary>
    public ObservableCollection<string> AllowRules { get; } = [];

    [ObservableProperty] private bool _showAllowRules;

    [RelayCommand]
    private void ToggleAllowRules() => ShowAllowRules = !ShowAllowRules;

    [RelayCommand]
    private void RemoveAllowRule(string rule)
    {
        _chat.RemoveAllowRule(rule);
        RefreshAllowRules();
    }

    private void RefreshAllowRules()
    {
        AllowRules.Clear();
        foreach (var r in _chat.AllowRules) AllowRules.Add(r);
        OnPropertyChanged(nameof(AllowRulesLabel));
    }

    public string AllowRulesLabel => AllowRules.Count == 0 ? "No always-allowed rules" : $"Always allowed ({AllowRules.Count})";

    // ---- IRunActions ----

    public bool CanAct(string runId) => !IsRunning && _chat.LatestRun()?.Id == runId;

    public RunRecord? GetRun(string runId) => _chat.LatestRun() is { } r && r.Id == runId ? r : null;

    public string? StageName(string runId, string? stageKey)
    {
        if (GetRun(runId) is not { } run || stageKey is null) return stageKey;
        try { return WorkflowJson.Parse(run.WorkflowJson).Stages.FirstOrDefault(s => s.Key == stageKey)?.Name ?? stageKey; }
        catch (System.Text.Json.JsonException) { return stageKey; }
    }

    public async void Resume(string runId, ResumeMode mode, string stageKey)
    {
        if (!CanAct(runId)) return;
        foreach (var r in Entries.OfType<RecoveryViewModel>().Where(r => r.CanAct))
            if (_chat.LoadHistory().FirstOrDefault(e => e.Id == r.Id) is { Data: RecoveryData d } entry)
                _chat.UpdateEntry(entry, d with { Status = RecoveryStatus.Resumed });
        await _chat.ResumeWorkflowAsync(runId, mode, stageKey);
    }

    public void Discard(string runId, Guid recoveryEntryId)
    {
        _chat.DiscardRun(runId);
        if (_chat.LoadHistory().FirstOrDefault(e => e.Id == recoveryEntryId) is { Data: RecoveryData d } entry)
            _chat.UpdateEntry(entry, d with { Status = RecoveryStatus.Discarded });
        RefreshActions();
    }

    /// <summary>If the app closed mid-run, add a card that offers to resume it (once per run).</summary>
    private void OfferRecovery()
    {
        if (_chat.FindInterruptedRun() is not { CurrentStageKey: { } stageKey } run) return;
        var already = _chat.LoadHistory().Any(e => e.Data is RecoveryData { Status: RecoveryStatus.Pending } r && r.RunId == run.Id);
        if (!already)
            _chat.AddEntry(new RecoveryData(run.Id, run.WorkflowName, stageKey, StageName(run.Id, stageKey) ?? stageKey,
                run.Status == RunStatus.AwaitingApproval, RecoveryStatus.Pending));
    }

    private void RefreshActions()
    {
        foreach (var e in Entries.OfType<IRefreshable>()) e.Refresh();
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _chat.Stop();

    [RelayCommand(CanExecute = nameof(CanStartNewSession))]
    private void NewSession() => _chat.StartNewSession();

    private bool CanStartNewSession() => !IsRunning;

    // ---- IGateResponder ----

    public bool IsPending(Guid gateEntryId) => _chat.IsGatePending(gateEntryId);
    public bool Resolve(Guid gateEntryId, GateDecision decision) => _chat.ResolveGate(gateEntryId, decision);

    // ---- timeline ----

    [ObservableProperty] private string _busyText = WorkingText;
    private const string WorkingText = "Claude Code is working. Queue sends your message to it next; Stop ends the turn and keeps the session.";

    private void AttachRun(WorkflowEngine engine)
    {
        var panel = new RunPanelViewModel(WorkflowJson.Parse(engine.Run.WorkflowJson)) { IsUnattended = _chat.AllowsUnattended };
        RunPanel = panel;
        engine.StageRunChanged += s => OnUi(() => panel.Apply(s));
        engine.RunChanged += r => OnUi(() =>
        {
            panel.Apply(r);
            BusyText = r.Status == RunStatus.AwaitingApproval
                ? "Waiting for your answer in the card above. Anything you queue now becomes a note for the next stage."
                : WorkingText;
        });
    }

    private void AddEntry(ChatEntry e)
    {
        var vm = EntryViewModel.Create(e, this, this, this);
        _byId[e.Id] = vm;

        if (vm is ToolCallViewModel tool)
        {
            _toolsByUseId[tool.ToolUseId] = tool;
            if (tool.ParentToolUseId is { } parent && _toolsByUseId.TryGetValue(parent, out var parentVm))
            {
                parentVm.Children.Add(tool);
                ContentGrew?.Invoke();
                return;
            }
        }

        Entries.Add(vm);
        if (e.Data is NoticeData { Level: NoticeLevel.Error } err && e.CreatedAt > _openedAt)
            AgentFlow.App.Services.AppLog.Write($"[{ProjectName}] {err.Text}");
        if (vm is ApprovalViewModel { IsPending: true })
            BusyText = "Claude Code is waiting for your permission in the card above.";
        ContentGrew?.Invoke();
    }

    private void UpdateEntry(ChatEntry e)
    {
        if (_byId.TryGetValue(e.Id, out var vm))
        {
            vm.Update(e.Data);
            // The rule is saved just before the card is marked, so re-read it now.
            if (e.Data is ApprovalData { Status: ApprovalStatus.AlwaysAllowed }) RefreshAllowRules();
            ContentGrew?.Invoke();
        }
    }

    private void UpdateSessionText() =>
        SessionText = _chat.Project.LastSessionId is { } id ? $"Session {id[..8]}" : "New session on next message";

    private static string FormatUsage(RateLimitStatus rl)
    {
        // Only the plan's two main windows; other keys (such as overage) are left out.
        var parts = rl.Utilization.Where(kv => kv.Key is "five_hour" or "seven_day").Select(kv => $"{Window(kv.Key)} {kv.Value:P0}");
        return "Plan usage: " + string.Join(" · ", parts);

        static string Window(string key) => key switch { "five_hour" => "5-hour", "seven_day" => "7-day", _ => key.Replace('_', ' ') };
    }

    private static void OnUi(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }
}
