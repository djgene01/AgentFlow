using System.Collections.ObjectModel;
using System.Globalization;
using AgentFlow.Core.Workflows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.App.ViewModels.Builder;

public sealed record PresetOption(PermissionPreset Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record VerdictCondition(string? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One transition row: when (always / pass / changes_requested), go to, max loops.</summary>
public sealed partial class TransitionEditorViewModel : ObservableObject
{
    public static IReadOnlyList<VerdictCondition> Conditions { get; } =
    [
        new(null, "Always"),
        new(Verdict.Pass, "Verdict is pass"),
        new(Verdict.ChangesRequested, "Verdict is changes_requested"),
    ];

    [ObservableProperty] private VerdictCondition _when = Conditions[0];
    [ObservableProperty] private string _goto = "end";
    [ObservableProperty] private string _maxLoopsText = "";

    public event Action? Changed;

    public static TransitionEditorViewModel From(StageTransition t) => new()
    {
        When = Conditions.FirstOrDefault(c => c.Value == t.When) ?? Conditions[0],
        Goto = t.Goto,
        MaxLoopsText = t.MaxLoops?.ToString(CultureInfo.InvariantCulture) ?? "",
    };

    public StageTransition ToDefinition() => new()
    {
        When = When.Value,
        Goto = Goto,
        MaxLoops = int.TryParse(MaxLoopsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null,
    };

    public string? NumberError => MaxLoopsText.Length > 0 && !int.TryParse(MaxLoopsText, out _) ? "Max loops must be a whole number." : null;

    partial void OnWhenChanged(VerdictCondition value) => Changed?.Invoke();
    partial void OnGotoChanged(string value) => Changed?.Invoke();
    partial void OnMaxLoopsTextChanged(string value) => Changed?.Invoke();
}

/// <summary>The form for one stage. Every field maps to a <see cref="StageDefinition"/> property.</summary>
public sealed partial class StageEditorViewModel : ObservableObject
{
    public static IReadOnlyList<string> ModelSuggestions { get; } =
        ["claude-fable-5-2", "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "fable", "opus", "sonnet", "haiku"];

    public static IReadOnlyList<Choice> Efforts => Choices.Efforts;

    public static IReadOnlyList<PresetOption> Presets { get; } =
    [
        new(PermissionPreset.ReadOnly, "Read only (plan mode)"),
        new(PermissionPreset.ReadOnlyWithCommands, "Read only + build/test/git commands"),
        new(PermissionPreset.Edit, "Edit files + build/test/git commands"),
        new(PermissionPreset.Custom, "Custom permission mode"),
    ];

    public static IReadOnlyList<string> CustomModes { get; } = ["default", "plan", "acceptEdits", "dontAsk", "auto"];

    public static IReadOnlyList<string> Variables { get; } =
        ["{{task}}", "{{notes}}", "{{git.diff}}", "{{git.baseCommit}}", "{{changedFiles}}"];

    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private Choice _effort = Choices.Efforts[0];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsCustomPreset))] private PresetOption _preset = Presets[0];
    [ObservableProperty] private string _customMode = "default";
    [ObservableProperty] private string _allowedToolsText = "";
    [ObservableProperty] private string _disallowedToolsText = "";
    [ObservableProperty] private Choice? _session;
    [ObservableProperty] private string _rolePrompt = "";
    [ObservableProperty] private string _promptTemplate = "";
    [ObservableProperty] private bool _waitForApproval;
    [ObservableProperty] private bool _returnsVerdict;
    [ObservableProperty] private string _maxTurnsText = "";
    [ObservableProperty] private string _maxBudgetText = "";
    [ObservableProperty] private int _number;
    [ObservableProperty] private bool _hasErrors;

    private string _sessionValue = "new";

    public ObservableCollection<TransitionEditorViewModel> Transitions { get; } = [];
    public ObservableCollection<Choice> SessionOptions { get; } = [];
    public ObservableCollection<string> GotoOptions { get; } = [];

    /// <summary>Variables for this stage's prompt, including the results of the other stages.</summary>
    public ObservableCollection<string> InsertableVariables { get; } = [];

    public bool IsCustomPreset => Preset.Value == PermissionPreset.Custom;
    public string Summary => $"{(string.IsNullOrWhiteSpace(Model) ? "default model" : Model)}{(Effort.Value is null ? "" : " · " + Effort.Value)}{(WaitForApproval ? " · approval" : "")}{(ReturnsVerdict ? " · verdict" : "")}";

    /// <summary>Any field changed.</summary>
    public event Action? Changed;

    /// <summary>The key was edited (old, new); the editor updates references in the other stages.</summary>
    public event Action<StageEditorViewModel, string, string>? KeyRenamed;

    public static StageEditorViewModel From(StageDefinition s)
    {
        var vm = new StageEditorViewModel { _suspend = true };
        vm.Key = s.Key;
        vm.Name = s.Name;
        vm.Model = s.Model ?? "";
        vm.Effort = Choices.Find(Choices.Efforts, s.Effort);
        vm.Preset = Presets.First(p => p.Value == s.PermissionPreset);
        vm.CustomMode = s.PermissionMode ?? "default";
        vm.AllowedToolsText = string.Join(Environment.NewLine, s.AllowedTools);
        vm.DisallowedToolsText = string.Join(Environment.NewLine, s.DisallowedTools);
        vm._sessionValue = s.Session;
        vm.RolePrompt = s.RolePrompt ?? "";
        vm.PromptTemplate = s.PromptTemplate;
        vm.WaitForApproval = s.Gate == StageGate.Approve;
        vm.ReturnsVerdict = s.Output == StageOutput.Verdict;
        vm.MaxTurnsText = s.MaxTurns?.ToString(CultureInfo.InvariantCulture) ?? "";
        vm.MaxBudgetText = s.MaxBudgetUsd?.ToString(CultureInfo.InvariantCulture) ?? "";
        foreach (var t in s.Transitions) vm.AddTransitionVm(TransitionEditorViewModel.From(t));
        vm._suspend = false;
        return vm;
    }

    public StageDefinition ToDefinition() => new()
    {
        Key = Key.Trim(),
        Name = Name.Trim(),
        Model = string.IsNullOrWhiteSpace(Model) ? null : Model.Trim(),
        Effort = Effort.Value,
        PermissionPreset = Preset.Value,
        PermissionMode = IsCustomPreset ? CustomMode : null,
        AllowedTools = Lines(AllowedToolsText),
        DisallowedTools = Lines(DisallowedToolsText),
        Session = _sessionValue,
        RolePrompt = string.IsNullOrWhiteSpace(RolePrompt) ? null : RolePrompt,
        PromptTemplate = PromptTemplate,
        Gate = WaitForApproval ? StageGate.Approve : StageGate.None,
        Output = ReturnsVerdict ? StageOutput.Verdict : StageOutput.Text,
        MaxTurns = int.TryParse(MaxTurnsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var turns) ? turns : null,
        MaxBudgetUsd = decimal.TryParse(MaxBudgetText, NumberStyles.Number, CultureInfo.InvariantCulture, out var b) ? b : null,
        Transitions = Transitions.Select(t => t.ToDefinition()).ToList(),
    };

    /// <summary>Problems a definition cannot show, such as text in a number field.</summary>
    public IEnumerable<string> FieldErrors()
    {
        if (MaxTurnsText.Length > 0 && !int.TryParse(MaxTurnsText, out _)) yield return $"Stage '{Name}': max turns must be a whole number.";
        if (MaxBudgetText.Length > 0 && !decimal.TryParse(MaxBudgetText, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            yield return $"Stage '{Name}': budget must be a number such as 2.50.";
        foreach (var t in Transitions)
            if (t.NumberError is { } e) yield return $"Stage '{Name}': {e}";
    }

    /// <summary>Rebuilds the session and transition choices from the stages around this one.</summary>
    public void RefreshOptions(IReadOnlyList<StageEditorViewModel> all)
    {
        var wasSuspended = _suspend;
        _suspend = true;

        var index = all.ToList().IndexOf(this);
        Number = index + 1;
        SessionOptions.Clear();
        SessionOptions.Add(new Choice("new", "New session"));
        foreach (var earlier in all.Take(index))
        {
            SessionOptions.Add(new Choice($"continue:{earlier.Key}", $"Continue {earlier.Name}'s session"));
            SessionOptions.Add(new Choice($"fork:{earlier.Key}", $"Fork {earlier.Name}'s session"));
        }
        if (SessionOptions.All(o => o.Value != _sessionValue))
            SessionOptions.Add(new Choice(_sessionValue, $"{_sessionValue} (no such earlier stage)"));
        Session = SessionOptions.First(o => o.Value == _sessionValue);

        GotoOptions.Clear();
        foreach (var s in all) GotoOptions.Add(s.Key);
        GotoOptions.Add("end");

        InsertableVariables.Clear();
        foreach (var v in Variables) InsertableVariables.Add(v);
        foreach (var s in all.Where(s => s != this))
        {
            InsertableVariables.Add($"{{{{stages.{s.Key}.result}}}}");
            if (s.ReturnsVerdict) InsertableVariables.Add($"{{{{stages.{s.Key}.findings}}}}");
        }

        _suspend = wasSuspended;
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Replaces fields after another stage's key was renamed.</summary>
    public void ApplyReferences(StageDefinition updated)
    {
        _suspend = true;
        _sessionValue = updated.Session;
        PromptTemplate = updated.PromptTemplate;
        for (var i = 0; i < Transitions.Count && i < updated.Transitions.Count; i++)
            Transitions[i].Goto = updated.Transitions[i].Goto;
        _suspend = false;
    }

    [RelayCommand]
    private void AddTransition()
    {
        AddTransitionVm(new TransitionEditorViewModel { Goto = "end" });
        Raise();
    }

    [RelayCommand]
    private void RemoveTransition(TransitionEditorViewModel t)
    {
        Transitions.Remove(t);
        Raise();
    }

    private void AddTransitionVm(TransitionEditorViewModel t)
    {
        t.Changed += Raise;
        Transitions.Add(t);
    }

    private bool _suspend;

    partial void OnKeyChanged(string? oldValue, string newValue)
    {
        if (!_suspend && !string.IsNullOrEmpty(oldValue) && oldValue != newValue)
            KeyRenamed?.Invoke(this, oldValue, newValue);
        Raise();
    }

    partial void OnSessionChanged(Choice? value)
    {
        if (value?.Value is { } v && !_suspend)
        {
            _sessionValue = v;
            Raise();
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Key) or nameof(Session) or nameof(Number) or nameof(Summary) or nameof(HasErrors) or nameof(IsCustomPreset)) return;
        if (e.PropertyName is nameof(Model) or nameof(Effort) or nameof(WaitForApproval) or nameof(ReturnsVerdict))
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Summary)));
        Raise();
    }

    private void Raise()
    {
        if (!_suspend) Changed?.Invoke();
    }

    private static List<string> Lines(string text) =>
        text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
}
