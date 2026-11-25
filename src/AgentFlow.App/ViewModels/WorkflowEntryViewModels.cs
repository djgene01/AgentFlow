using AgentFlow.Core.Chat;
using AgentFlow.Core.Workflows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.App.ViewModels;

/// <summary>Lets gate cards answer the engine without knowing about ProjectChat.</summary>
public interface IGateResponder
{
    bool IsPending(Guid gateEntryId);
    bool Resolve(Guid gateEntryId, GateDecision decision);
}

/// <summary>What the timeline cards may do with past runs. Implemented by the chat.</summary>
public interface IRunActions
{
    /// <summary>True when nothing runs and <paramref name="runId"/> is the project's newest run.</summary>
    bool CanAct(string runId);
    RunRecord? GetRun(string runId);
    string? StageName(string runId, string? stageKey);
    void Resume(string runId, ResumeMode mode, string stageKey);
    void Discard(string runId, Guid recoveryEntryId);
    void ExportRun(string runId);
}

/// <summary>Cards whose buttons depend on whether something is running.</summary>
public interface IRefreshable
{
    void Refresh();
}

public sealed partial class StageDividerViewModel : EntryViewModel, IRefreshable
{
    private readonly IRunActions _actions;
    private string _runId = "";
    private string _stageKey = "";

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private bool _canRunAgain;

    public StageDividerViewModel(ChatEntry e, IRunActions actions) : base(e)
    {
        _actions = actions;
        Update(e.Data);
    }

    public override void Update(ChatEntryData data)
    {
        if (data is not StageDividerData s) return;
        _runId = s.RunId;
        _stageKey = s.StageKey;
        Title = $"Stage {s.StageNumber} · {s.StageName}{(s.Attempt > 1 ? $" (round {s.Attempt})" : "")}";
        Details = string.Join(" · ", new[] { s.Model ?? "default model", s.Effort is null ? null : s.Effort + " effort", s.Permissions, s.SessionMode }
            .Where(x => !string.IsNullOrEmpty(x)));
        Refresh();
    }

    public void Refresh() => CanRunAgain = _actions.CanAct(_runId);

    [RelayCommand]
    private void RunAgain() => _actions.Resume(_runId, ResumeMode.RunFrom, _stageKey);
}

public sealed partial class RunStatusViewModel : EntryViewModel, IRefreshable
{
    private readonly IRunActions _actions;
    private string _runId = "";

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _canRetry;
    [ObservableProperty] private string _retryLabel = "Retry";
    [ObservableProperty] private string _skipLabel = "Skip";

    public RunStatusViewModel(ChatEntry e, IRunActions actions) : base(e)
    {
        _actions = actions;
        Update(e.Data);
    }

    public override void Update(ChatEntryData data)
    {
        if (data is not RunStatusData r) return;
        _runId = r.RunId;
        Text = r.Text;
        Status = r.Status;
        Refresh();
    }

    /// <summary>Retry and Skip appear on the card that says a run stopped or failed, while it is the newest run.</summary>
    public void Refresh()
    {
        var run = Status is "failed" or "cancelled" && _actions.CanAct(_runId) ? _actions.GetRun(_runId) : null;
        CanRetry = run is { CurrentStageKey: not null, Status: RunStatus.Failed or RunStatus.Cancelled };
        if (CanRetry)
        {
            var name = _actions.StageName(_runId, run!.CurrentStageKey);
            RetryLabel = $"Retry {name}";
            SkipLabel = $"Skip {name}";
        }
    }

    [RelayCommand]
    private void Retry()
    {
        if (_actions.GetRun(_runId)?.CurrentStageKey is { } key) _actions.Resume(_runId, ResumeMode.RunFrom, key);
    }

    [RelayCommand]
    private void Skip()
    {
        if (_actions.GetRun(_runId)?.CurrentStageKey is { } key) _actions.Resume(_runId, ResumeMode.Skip, key);
    }

    /// <summary>Only the card that ends a run offers Export.</summary>
    public bool IsEnd => Status is "completed" or "failed" or "cancelled";

    [RelayCommand]
    private void Export() => _actions.ExportRun(_runId);
}

/// <summary>Shown when the app closed in the middle of a run.</summary>
public sealed partial class RecoveryViewModel : EntryViewModel, IRefreshable
{
    private readonly IRunActions _actions;
    private RecoveryData _data = null!;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _canAct;

    public RecoveryViewModel(ChatEntry e, IRunActions actions) : base(e)
    {
        _actions = actions;
        Update(e.Data);
    }

    public override void Update(ChatEntryData data)
    {
        if (data is not RecoveryData r) return;
        _data = r;
        Text = r.AtGate
            ? $"AgentFlow closed while \"{r.WorkflowName}\" was waiting for your approval of {r.StageName}. Resume shows the approval again; nothing is re-run."
            : $"AgentFlow closed while \"{r.WorkflowName}\" was working on {r.StageName}. Resume continues that stage's Claude Code session where it stopped.";
        StatusText = r.Status switch
        {
            RecoveryStatus.Resumed => "Resumed",
            RecoveryStatus.Discarded => "Discarded: the run was closed",
            _ => "",
        };
        Refresh();
    }

    public void Refresh() => CanAct = _data.Status == RecoveryStatus.Pending && _actions.CanAct(_data.RunId);

    [RelayCommand]
    private void Resume() => _actions.Resume(_data.RunId, ResumeMode.Recover, _data.StageKey);

    [RelayCommand]
    private void Discard() => _actions.Discard(_data.RunId, Id);
}

public sealed record FindingViewModel(string Severity, string Location, string Issue, string? Fix);

public sealed partial class VerdictViewModel : EntryViewModel
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private bool _passed;
    [ObservableProperty] private string? _summary;
    [ObservableProperty] private IReadOnlyList<FindingViewModel> _findings = [];

    public VerdictViewModel(ChatEntry e) : base(e) => Update(e.Data);

    public override void Update(ChatEntryData data)
    {
        if (data is not VerdictData v) return;
        Passed = v.Verdict == Verdict.Pass;
        Title = Passed ? "Review passed" : $"Changes requested · {v.Findings.Count} finding{(v.Findings.Count == 1 ? "" : "s")}";
        Summary = v.Summary;
        Findings = v.Findings.Select(f => new FindingViewModel(
            f.Severity,
            f.File is null ? "" : f.Line is { } l ? $"{f.File}:{l}" : f.File,
            f.Issue,
            string.IsNullOrWhiteSpace(f.Fix) ? null : "Fix: " + f.Fix)).ToList();
    }
}

/// <summary>
/// A decision the run waits for. Approval gates: approve as is, edit first, ask for changes, or cancel.
/// Loop-limit questions: run one more round, or finish the run.
/// </summary>
public sealed partial class GateViewModel : EntryViewModel
{
    private readonly IGateResponder _responder;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _editedText = "";
    [ObservableProperty] private string _feedback = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ApproveCommand), nameof(RequestChangesCommand), nameof(CancelRunCommand))]
    private bool _isPending;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isLoopQuestion;
    [ObservableProperty] private string _approveLabel = "Approve";
    [ObservableProperty] private string _cancelLabel = "Cancel run";

    public GateViewModel(ChatEntry e, IGateResponder responder) : base(e)
    {
        _responder = responder;
        Update(e.Data);
    }

    public override void Update(ChatEntryData data)
    {
        if (data is not GateData g) return;
        IsLoopQuestion = g.GateKind == GateData.LoopLimitKind;
        Title = IsLoopQuestion ? "Another review round?" : $"{g.StageName} ready for your approval";
        ApproveLabel = IsLoopQuestion ? "Run another round" : "Approve";
        CancelLabel = IsLoopQuestion ? "Finish the run" : "Cancel run";
        Text = g.Text;
        if (!IsEditing) EditedText = g.Text;
        IsPending = g.Status == GateStatus.Pending && _responder.IsPending(Id);
        StatusText = (IsLoopQuestion, g.Status) switch
        {
            (true, GateStatus.Approved) => "You chose another round",
            (true, GateStatus.Cancelled) => "You finished the run with these findings open",
            (_, GateStatus.Approved) => g.WasEdited ? "Approved with your edits" : "Approved",
            (_, GateStatus.ChangesRequested) => "You asked for changes: " + g.Feedback,
            (_, GateStatus.Cancelled) => "Run cancelled",
            _ when !IsPending => "Not answered: the run ended",
            _ => "",
        };
        if (g.Status == GateStatus.Approved && !string.IsNullOrWhiteSpace(g.Feedback))
            StatusText += " · notes: " + g.Feedback;
    }

    [RelayCommand]
    private void ToggleEdit()
    {
        IsEditing = !IsEditing;
        if (IsEditing && string.IsNullOrEmpty(EditedText)) EditedText = Text;
    }

    [RelayCommand(CanExecute = nameof(IsPending))]
    private void Approve() => Answer(new GateDecision(GateDecisionKind.Approve,
        EditedText: IsEditing ? EditedText : null,
        Feedback: string.IsNullOrWhiteSpace(Feedback) ? null : Feedback.Trim()));

    [RelayCommand(CanExecute = nameof(IsPending))]
    private void RequestChanges()
    {
        if (string.IsNullOrWhiteSpace(Feedback))
        {
            StatusText = "Write what should change in the box first.";
            return;
        }
        Answer(new GateDecision(GateDecisionKind.RequestChanges, Feedback: Feedback.Trim()));
    }

    [RelayCommand(CanExecute = nameof(IsPending))]
    private void CancelRun() => Answer(new GateDecision(GateDecisionKind.Cancel));

    private void Answer(GateDecision d)
    {
        if (_responder.Resolve(Id, d))
        {
            IsEditing = false;
            IsPending = false;
        }
    }
}
