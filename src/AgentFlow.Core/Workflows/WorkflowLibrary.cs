namespace AgentFlow.Core.Workflows;

public sealed record WorkflowFile(string Path, string Scope, WorkflowDefinition? Definition, IReadOnlyList<string> Errors)
{
    public const string LibraryScope = "library";
    public const string ProjectScope = "project";

    public string Name => Definition?.Name is { Length: > 0 } n ? n : System.IO.Path.GetFileNameWithoutExtension(Path);
    public bool IsValid => Definition is not null && Errors.Count == 0;
}

/// <summary>
/// Workflow JSON files: your library in %APPDATA%\AgentFlow\workflows, and optional
/// per-project files in &lt;project&gt;\.workflows that can be committed to git.
/// </summary>
public sealed class WorkflowLibrary(string libraryFolder)
{
    public string LibraryFolder { get; } = libraryFolder;

    /// <summary>Raised after a save, import or delete, so pickers can reload.</summary>
    public event Action? Changed;

    public static string DefaultFolder() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentFlow", "workflows");

    public static string ProjectFolder(string projectFolder) => System.IO.Path.Combine(projectFolder, ".workflows");

    /// <summary>Writes the default workflow on first start. An existing file is never overwritten.</summary>
    public void EnsureDefault()
    {
        var path = System.IO.Path.Combine(LibraryFolder, DefaultWorkflows.FileName);
        if (!File.Exists(path))
            WorkflowJson.Save(DefaultWorkflows.PlanDevelopReviewRefine(), path);
    }

    public IReadOnlyList<WorkflowFile> List(string? projectFolder)
    {
        var files = new List<WorkflowFile>();
        if (projectFolder is not null)
            files.AddRange(Read(ProjectFolder(projectFolder), WorkflowFile.ProjectScope));
        files.AddRange(Read(LibraryFolder, WorkflowFile.LibraryScope));
        return files;
    }

    public WorkflowFile Load(string path, string scope)
    {
        try
        {
            var def = WorkflowJson.Load(path);
            return new WorkflowFile(path, scope, def, WorkflowJson.Validate(def));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return new WorkflowFile(path, scope, null, [ex.Message]);
        }
    }

    /// <summary>Saves a new workflow under a file name made from its name; never overwrites another file.</summary>
    public string SaveNew(WorkflowDefinition wf, string scope, string? projectFolder)
    {
        var folder = FolderFor(scope, projectFolder);
        Directory.CreateDirectory(folder);
        var path = UniquePath(folder, WorkflowEdits.Slug(wf.Name) is { Length: > 0 } s ? s : "workflow");
        WorkflowJson.Save(wf, path);
        Changed?.Invoke();
        return path;
    }

    /// <summary>Overwrites an existing workflow file.</summary>
    public void Save(string path, WorkflowDefinition wf)
    {
        WorkflowJson.Save(wf, path);
        Changed?.Invoke();
    }

    /// <summary>Moves a workflow file into a ".deleted" folder beside it instead of erasing it, so a mistake can be undone.</summary>
    public string Delete(string path)
    {
        var trash = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, ".deleted");
        Directory.CreateDirectory(trash);
        var target = UniquePath(trash, System.IO.Path.GetFileNameWithoutExtension(path));
        File.Move(path, target);
        Changed?.Invoke();
        return target;
    }

    /// <summary>Copies a workflow file into the library or a project. Throws if it cannot be read as a workflow.</summary>
    public string Import(string sourcePath, string scope, string? projectFolder)
    {
        var def = WorkflowJson.Load(sourcePath); // throws on unreadable JSON; validation errors are kept and shown later
        return SaveNew(def, scope, projectFolder);
    }

    public static void Export(WorkflowDefinition wf, string targetPath) => WorkflowJson.Save(wf, targetPath);

    private string FolderFor(string scope, string? projectFolder) => scope switch
    {
        WorkflowFile.ProjectScope when projectFolder is not null => ProjectFolder(projectFolder),
        WorkflowFile.ProjectScope => throw new ArgumentException("A project workflow needs a project folder.", nameof(projectFolder)),
        _ => LibraryFolder,
    };

    private static string UniquePath(string folder, string baseName)
    {
        var path = System.IO.Path.Combine(folder, baseName + ".json");
        for (var i = 2; File.Exists(path); i++)
            path = System.IO.Path.Combine(folder, $"{baseName}-{i}.json");
        return path;
    }

    private IEnumerable<WorkflowFile> Read(string folder, string scope)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            yield return Load(path, scope);
    }
}
