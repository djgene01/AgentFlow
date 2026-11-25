using System.Collections.ObjectModel;
using AgentFlow.Core.Workflows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.App.ViewModels.Builder;

/// <summary>Edits one workflow in memory. Validation runs on every change; nothing is written until Save.</summary>
public sealed partial class WorkflowEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private StageEditorViewModel? _selectedStage;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isValid;

    public ObservableCollection<StageEditorViewModel> Stages { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];
    public IReadOnlyList<StageTemplates.Template> Templates => StageTemplates.All;

    /// <summary>The file this workflow came from; null until a new workflow is saved.</summary>
    public string? Path { get; set; }
    public string Scope { get; set; }

    private bool _loading;

    public WorkflowEditorViewModel(WorkflowDefinition wf, string? path, string scope)
    {
        Path = path;
        Scope = scope;
        _loading = true;
        Name = wf.Name;
        Description = wf.Description ?? "";
        foreach (var s in wf.Stages) Attach(StageEditorViewModel.From(s));
        _loading = false;
        RefreshAll();
        SelectedStage = Stages.FirstOrDefault();
        IsDirty = path is null;
    }

    public WorkflowDefinition ToDefinition() => new()
    {
        Name = Name.Trim(),
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        Stages = Stages.Select(s => s.ToDefinition()).ToList(),
    };

    partial void OnNameChanged(string value) => Touch();
    partial void OnDescriptionChanged(string value) => Touch();

    [RelayCommand]
    private void AddStage(StageTemplates.Template? template)
    {
        var def = (template ?? StageTemplates.All[^1]).Create();
        def = def with { Key = WorkflowEdits.UniqueKey(def.Key, Stages.Select(s => s.Key)) };
        var vm = StageEditorViewModel.From(def);
        Attach(vm, SelectedStage is null ? Stages.Count : Stages.IndexOf(SelectedStage) + 1);
        SelectedStage = vm;
        Touch();
    }

    [RelayCommand]
    private void RemoveStage(StageEditorViewModel? stage)
    {
        stage ??= SelectedStage;
        if (stage is null) return;
        var i = Stages.IndexOf(stage);
        stage.Changed -= Touch;
        stage.KeyRenamed -= OnKeyRenamed;
        Stages.Remove(stage);
        SelectedStage = Stages.Count == 0 ? null : Stages[Math.Min(i, Stages.Count - 1)];
        Touch();
    }

    [RelayCommand]
    private void MoveUp(StageEditorViewModel? stage) => Move(stage ?? SelectedStage, -1);

    [RelayCommand]
    private void MoveDown(StageEditorViewModel? stage) => Move(stage ?? SelectedStage, +1);

    private void Move(StageEditorViewModel? stage, int delta)
    {
        if (stage is null) return;
        var from = Stages.IndexOf(stage);
        MoveTo(from, from + delta);
    }

    /// <summary>Used by drag and drop as well as the arrow buttons.</summary>
    public void MoveTo(int from, int to)
    {
        if (from < 0 || from >= Stages.Count || to < 0 || to >= Stages.Count || from == to) return;
        var stage = Stages[from];
        Stages.Move(from, to);
        SelectedStage = stage;
        Touch();
    }

    private void Attach(StageEditorViewModel vm, int? index = null)
    {
        vm.Changed += Touch;
        vm.KeyRenamed += OnKeyRenamed;
        if (index is { } i) Stages.Insert(i, vm);
        else Stages.Add(vm);
    }

    /// <summary>A key edit renames every reference to it, so the workflow stays valid.</summary>
    private void OnKeyRenamed(StageEditorViewModel renamed, string oldKey, string newKey)
    {
        var updated = WorkflowEdits.RenameKey(ToDefinition() with
        {
            Stages = Stages.Select(s => s == renamed ? s.ToDefinition() with { Key = oldKey } : s.ToDefinition()).ToList(),
        }, oldKey, newKey);

        for (var i = 0; i < Stages.Count; i++)
            if (Stages[i] != renamed) Stages[i].ApplyReferences(updated.Stages[i]);
        Touch();
    }

    private void Touch()
    {
        if (_loading) return;
        IsDirty = true;
        RefreshAll();
    }

    private void RefreshAll()
    {
        foreach (var s in Stages) s.RefreshOptions(Stages);

        Errors.Clear();
        foreach (var e in Stages.SelectMany(s => s.FieldErrors())) Errors.Add(e);
        foreach (var e in WorkflowJson.Validate(ToDefinition())) Errors.Add(e);
        foreach (var s in Stages)
            s.HasErrors = Errors.Any(e => e.Contains($"'{s.Name}'", StringComparison.Ordinal) || e.Contains($"'{s.Key}'", StringComparison.Ordinal));
        IsValid = Errors.Count == 0;
    }

    public void MarkSaved(string path)
    {
        Path = path;
        IsDirty = false;
    }
}
