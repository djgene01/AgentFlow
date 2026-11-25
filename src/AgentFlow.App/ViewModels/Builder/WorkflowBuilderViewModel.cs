using System.Collections.ObjectModel;
using System.IO;
using AgentFlow.App.Services;
using AgentFlow.Core.Workflows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.App.ViewModels.Builder;

public sealed record WorkflowListItem(WorkflowFile File)
{
    public string Name => File.Name;
    public string Where => File.Scope == WorkflowFile.ProjectScope ? "This project" : "Library";
    public string Status => File.IsValid ? $"{File.Definition!.Stages.Count} stage{(File.Definition.Stages.Count == 1 ? "" : "s")}" : "Has errors";
    public bool HasErrors => !File.IsValid;
}

public sealed record ScopeOption(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The Workflows page: your library plus the selected project's workflows, and the editor.</summary>
public sealed partial class WorkflowBuilderViewModel : ObservableObject
{
    private readonly WorkflowLibrary _library;
    private readonly IDialogService _dialogs;

    public ObservableCollection<WorkflowListItem> Items { get; } = [];
    public ObservableCollection<ScopeOption> Scopes { get; } = [];

    [ObservableProperty] private WorkflowListItem? _selectedItem;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(CloneCommand), nameof(ExportCommand), nameof(DeleteCommand), nameof(RevertCommand))]
    private WorkflowEditorViewModel? _editor;
    [ObservableProperty] private string? _loadError;
    [ObservableProperty] private ScopeOption? _saveScope;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isNew;

    /// <summary>The project whose .workflows folder is shown; null shows the library only.</summary>
    public string? ProjectFolder { get; private set; }
    public string? ProjectName { get; private set; }

    private bool _switching;

    public WorkflowBuilderViewModel(WorkflowLibrary library, IDialogService dialogs)
    {
        _library = library;
        _dialogs = dialogs;
    }

    public void Open(string? projectFolder, string? projectName)
    {
        ProjectFolder = projectFolder;
        ProjectName = projectName;
        Scopes.Clear();
        Scopes.Add(new ScopeOption(WorkflowFile.LibraryScope, "My library (all projects)"));
        if (projectFolder is not null) Scopes.Add(new ScopeOption(WorkflowFile.ProjectScope, $"This project: {projectName}"));
        SaveScope = Scopes[0];
        Reload(Editor?.Path);
    }

    private void Reload(string? selectPath)
    {
        _switching = true;
        Items.Clear();
        foreach (var f in _library.List(ProjectFolder)) Items.Add(new WorkflowListItem(f));
        _switching = false;
        if (Editor is { Path: null, IsDirty: true }) return; // keep an unsaved new workflow open
        SelectedItem = Items.FirstOrDefault(i => i.File.Path == selectPath) ?? Items.FirstOrDefault();
    }

    partial void OnSelectedItemChanged(WorkflowListItem? oldValue, WorkflowListItem? newValue)
    {
        if (_switching) return;
        if (newValue is null) return;
        if (Editor is { IsDirty: true } && Editor.Path != newValue.File.Path && !ConfirmDiscard())
        {
            _switching = true;
            SelectedItem = oldValue;
            _switching = false;
            return;
        }
        LoadEditor(newValue.File);
    }

    private void LoadEditor(WorkflowFile file)
    {
        LoadError = file.Definition is null ? "This file can't be read as a workflow: " + string.Join(" ", file.Errors) : null;
        Editor = file.Definition is null ? null : new WorkflowEditorViewModel(file.Definition, file.Path, file.Scope);
        IsNew = false;
        StatusText = "";
    }

    [RelayCommand]
    private void New()
    {
        if (Editor is { IsDirty: true } && !ConfirmDiscard()) return;
        _switching = true;
        SelectedItem = null;
        _switching = false;
        Editor = new WorkflowEditorViewModel(new WorkflowDefinition { Name = "New workflow", Stages = [StageTemplates.Planner()] }, null, SaveScope?.Value ?? WorkflowFile.LibraryScope);
        IsNew = true;
        LoadError = null;
        StatusText = "Add stages on the left, then Save.";
    }

    [RelayCommand(CanExecute = nameof(HasEditor))]
    private void Clone()
    {
        if (Editor is null) return;
        var copy = Editor.ToDefinition() with { Name = Editor.Name + " (copy)" };
        _switching = true;
        SelectedItem = null;
        _switching = false;
        Editor = new WorkflowEditorViewModel(copy, null, Editor.Scope);
        IsNew = true;
        StatusText = "A copy. Rename it and Save.";
    }

    [RelayCommand(CanExecute = nameof(HasEditor))]
    private void Save()
    {
        if (Editor is null) return;
        if (!Editor.IsValid && !_dialogs.Confirm("Save workflow",
                "This workflow has problems and can't be run until they're fixed:\n\n" + string.Join("\n", Editor.Errors.Take(6)) + "\n\nSave it anyway?"))
            return;

        var def = Editor.ToDefinition();
        try
        {
            if (Editor.Path is null)
            {
                var scope = SaveScope?.Value ?? WorkflowFile.LibraryScope;
                Editor.Scope = scope;
                Editor.MarkSaved(_library.SaveNew(def, scope, ProjectFolder));
            }
            else
            {
                _library.Save(Editor.Path, def);
                Editor.MarkSaved(Editor.Path);
            }
            IsNew = false;
            var saved = Editor.Path;
            Reload(saved);
            StatusText = $"Saved to {saved}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Inform("Save workflow", "Could not save: " + ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasEditor))]
    private void Revert()
    {
        if (Editor?.Path is not { } path) { New(); return; }
        LoadEditor(_library.Load(path, Editor.Scope));
    }

    [RelayCommand(CanExecute = nameof(HasSavedEditor))]
    private void Delete()
    {
        if (Editor?.Path is not { } path) return;
        if (!_dialogs.Confirm("Delete workflow",
                $"Delete \"{Editor.Name}\"?\n\nThe file is moved to a .deleted folder next to it, so you can restore it:\n{path}"))
            return;
        var moved = _library.Delete(path);
        Editor = null;
        Reload(null);
        StatusText = $"Moved to {moved}";
    }

    [RelayCommand]
    private void Import()
    {
        var source = _dialogs.OpenFile("Import a workflow", "Workflow JSON (*.json)|*.json");
        if (source is null) return;
        try
        {
            var path = _library.Import(source, SaveScope?.Value ?? WorkflowFile.LibraryScope, ProjectFolder);
            Reload(path);
            StatusText = $"Imported to {path}";
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            _dialogs.Inform("Import workflow", "That file is not a workflow AgentFlow can read: " + ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasEditor))]
    private void Export()
    {
        if (Editor is null) return;
        var target = _dialogs.SaveFile("Export workflow", WorkflowEdits.Slug(Editor.Name) + ".json", "Workflow JSON (*.json)|*.json");
        if (target is null) return;
        WorkflowLibrary.Export(Editor.ToDefinition(), target);
        StatusText = $"Exported to {target}";
    }

    private bool HasEditor() => Editor is not null;
    private bool HasSavedEditor() => Editor?.Path is not null;

    private bool ConfirmDiscard() =>
        _dialogs.Confirm("Unsaved changes", $"\"{Editor!.Name}\" has unsaved changes. Discard them?");
}
