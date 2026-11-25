using System.Text.Json;
using AgentFlow.Core.Agents;
using AgentFlow.Core.Chat;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.App.ViewModels;

public interface IApprovalResponder
{
    bool IsApprovalPending(Guid entryId);
    bool ResolveApproval(Guid entryId, ApprovalAnswer answer);
}

/// <summary>Claude Code asks to use a tool no rule allows: allow once, always allow in this project, or deny.</summary>
public sealed partial class ApprovalViewModel : EntryViewModel
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private readonly IApprovalResponder _responder;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private string? _alwaysLabel;
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AllowOnceCommand), nameof(AlwaysAllowCommand), nameof(DenyCommand))]
    private bool _isPending;
    [ObservableProperty] private bool _showInput;

    public ApprovalViewModel(ChatEntry e, IApprovalResponder responder) : base(e)
    {
        _responder = responder;
        Update(e.Data);
    }

    public override void Update(ChatEntryData data)
    {
        if (data is not ApprovalData a) return;
        Title = (a.StageName is null ? "" : a.StageName + ": ") + a.ToolName switch
        {
            "Bash" or "PowerShell" => $"Claude Code wants to run a {a.ToolName} command",
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => "Claude Code wants to change a file",
            "WebFetch" => "Claude Code wants to fetch a web page",
            "WebSearch" => "Claude Code wants to search the web",
            var t => $"Claude Code wants to use {t}",
        };
        Summary = a.Summary;
        Description = a.Description;
        InputText = Prettify(a.InputJson);
        AlwaysLabel = a.SuggestedRule is { } rule ? $"Always allow {rule}" : null;
        IsPending = a.Status == ApprovalStatus.Pending && _responder.IsApprovalPending(Id);
        StatusText = a.Status switch
        {
            ApprovalStatus.Allowed => "Allowed once",
            ApprovalStatus.AlwaysAllowed => $"Always allowed in this project: {a.SuggestedRule}",
            ApprovalStatus.Denied => "Denied" + (string.IsNullOrWhiteSpace(a.DenyMessage) ? "" : ": " + a.DenyMessage),
            ApprovalStatus.NotAnswered => "Not answered: the turn ended",
            ApprovalStatus.AutoAllowed => "Allowed automatically (auto-approve setting)",
            _ when !IsPending => "Not answered: the turn ended",
            _ => "",
        };
    }

    [RelayCommand(CanExecute = nameof(IsPending))]
    private void AllowOnce() => Answer(new ApprovalAnswer(ApprovalChoice.AllowOnce));

    [RelayCommand(CanExecute = nameof(IsPending))]
    private void AlwaysAllow() => Answer(new ApprovalAnswer(ApprovalChoice.AlwaysAllow));

    [RelayCommand(CanExecute = nameof(IsPending))]
    private void Deny() => Answer(new ApprovalAnswer(ApprovalChoice.Deny, string.IsNullOrWhiteSpace(Reason) ? null : Reason.Trim()));

    [RelayCommand]
    private void ToggleInput() => ShowInput = !ShowInput;

    private void Answer(ApprovalAnswer a)
    {
        if (_responder.ResolveApproval(Id, a)) IsPending = false;
    }

    private static string Prettify(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, Pretty);
        }
        catch (JsonException) { return json; }
    }
}
