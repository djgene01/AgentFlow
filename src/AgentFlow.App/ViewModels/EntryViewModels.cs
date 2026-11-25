using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using AgentFlow.Core.Chat;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentFlow.App.ViewModels;

/// <summary>Base for every item in the chat timeline. Each subclass has a DataTemplate.</summary>
public abstract partial class EntryViewModel(ChatEntry entry) : ObservableObject
{
    /// <summary>The entry this card shows; its Data is replaced as the entry updates.</summary>
    public ChatEntry Entry => entry;

    /// <summary>Contains the current search text.</summary>
    [ObservableProperty] private bool _isSearchHit;

    /// <summary>The match the search is on.</summary>
    [ObservableProperty] private bool _isCurrentHit;

    public Guid Id => entry.Id;
    public DateTimeOffset CreatedAt => entry.CreatedAt;
    public string TimeText => entry.CreatedAt.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

    public abstract void Update(ChatEntryData data);

    public static EntryViewModel Create(ChatEntry e, IGateResponder gates, IRunActions runs, IApprovalResponder approvals) => e.Data switch
    {
        ApprovalData => new ApprovalViewModel(e, approvals),
        StageDividerData => new StageDividerViewModel(e, runs),
        GateData => new GateViewModel(e, gates),
        VerdictData => new VerdictViewModel(e),
        RunStatusData => new RunStatusViewModel(e, runs),
        RecoveryData => new RecoveryViewModel(e, runs),
        UserMessageData => new UserMessageViewModel(e),
        AssistantTextData => new AssistantTextViewModel(e),
        ToolCallData => new ToolCallViewModel(e),
        NoticeData => new NoticeViewModel(e),
        TurnSummaryData => new TurnSummaryViewModel(e),
        SessionDividerData => new DividerViewModel(e),
        _ => new NoticeViewModel(new ChatEntry(e.Id, e.ProjectId, e.Seq, new NoticeData(NoticeLevel.Warning, "(unknown item)"), e.CreatedAt)),
    };
}

public sealed partial class UserMessageViewModel : EntryViewModel
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _meta = "";

    public UserMessageViewModel(ChatEntry e) : base(e) => Update(e.Data);

    public override void Update(ChatEntryData data)
    {
        if (data is not UserMessageData u) return;
        Text = u.Text;
        Meta = u.QueuedFor switch
        {
            "notes" => "queued as a note for the next stage",
            { } target => $"queued for {target}",
            null when u.Model is null && u.Effort is null && u.PermissionMode is null => "workflow task",
            null => string.Join(" · ", new[] { u.Model ?? "default model", u.Effort is null ? null : u.Effort + " effort", PermissionLabels.Short(u.PermissionMode) }
                .Where(s => !string.IsNullOrEmpty(s))),
        };
    }
}

public sealed partial class AssistantTextViewModel : EntryViewModel
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isStreaming;

    public AssistantTextViewModel(ChatEntry e) : base(e) => Update(e.Data);

    public override void Update(ChatEntryData data)
    {
        if (data is not AssistantTextData a) return;
        Text = a.Text;
        IsStreaming = a.IsStreaming;
    }
}

public sealed partial class ToolCallViewModel : EntryViewModel
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private string? _result;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isPending;
    [ObservableProperty] private bool _isExpanded;

    public string ToolUseId { get; private set; } = "";
    public string? ParentToolUseId { get; private set; }

    /// <summary>Tool calls made by a subagent this tool started.</summary>
    public ObservableCollection<ToolCallViewModel> Children { get; } = [];

    public ToolCallViewModel(ChatEntry e) : base(e) => Update(e.Data);

    public override void Update(ChatEntryData data)
    {
        if (data is not ToolCallData t) return;
        ToolUseId = t.ToolUseId;
        ParentToolUseId = t.ParentToolUseId;
        Name = t.Name;
        Summary = ToolSummary.Describe(t.Name, t.InputJson);
        InputText = PrettyJson(t.InputJson);
        Result = t.Result;
        IsError = t.IsError;
        IsPending = t.IsPending;
    }

    private static string PrettyJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, Pretty);
        }
        catch (JsonException) { return json; }
    }
}

public sealed partial class NoticeViewModel : EntryViewModel
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private NoticeLevel _level;

    public NoticeViewModel(ChatEntry e) : base(e) => Update(e.Data);

    public override void Update(ChatEntryData data)
    {
        if (data is not NoticeData n) return;
        Text = n.Text;
        Level = n.Level;
    }
}

public sealed partial class TurnSummaryViewModel : EntryViewModel
{
    [ObservableProperty] private string _text = "";

    public TurnSummaryViewModel(ChatEntry e) : base(e) => Update(e.Data);

    public override void Update(ChatEntryData data)
    {
        if (data is not TurnSummaryData s) return;
        var parts = new List<string>();
        if (s.Model is not null) parts.Add(s.Model);
        if (s.DurationMs is { } ms) parts.Add($"{ms / 1000.0:0.0} s");
        if (s.NumTurns is { } n) parts.Add(n == 1 ? "1 turn" : $"{n} turns");
        if (s.InputTokens is { } i && s.OutputTokens is { } o) parts.Add($"{i:N0} in / {o:N0} out tokens");
        if (s.CostUsd is { } c) parts.Add($"est. ${c:0.0000}");
        Text = string.Join(" · ", parts);
    }
}

public sealed partial class DividerViewModel : EntryViewModel
{
    [ObservableProperty] private string _text = "";

    public DividerViewModel(ChatEntry e) : base(e) => Update(e.Data);

    public override void Update(ChatEntryData data)
    {
        if (data is SessionDividerData d) Text = d.Text;
    }
}

/// <summary>A one-line description of a tool call for its collapsed card.</summary>
public static class ToolSummary
{
    private static readonly string[] Keys = ["file_path", "path", "command", "pattern", "url", "query", "description", "prompt", "skill", "notebook_path"];

    public static string Describe(string name, string inputJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            foreach (var key in Keys)
            {
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = (v.GetString() ?? "").ReplaceLineEndings(" ").Trim();
                    return s.Length > 140 ? s[..140] + "…" : s;
                }
            }
        }
        catch (JsonException) { }
        return "";
    }
}
