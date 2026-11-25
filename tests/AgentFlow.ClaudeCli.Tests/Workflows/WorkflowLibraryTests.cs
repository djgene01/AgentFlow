using AgentFlow.Core.Workflows;

namespace AgentFlow.ClaudeCli.Tests.Workflows;

public class WorkflowLibraryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("agentflow-lib2-").FullName;
    private string Library => Path.Combine(_root, "library");
    private string Project => Directory.CreateDirectory(Path.Combine(_root, "proj")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static WorkflowDefinition Three(string name) => new()
    {
        Name = name,
        Stages = [StageTemplates.Planner(), StageTemplates.Developer(), StageTemplates.Reviewer()],
    };

    [Fact]
    public void SaveNew_names_the_file_after_the_workflow_and_never_overwrites()
    {
        var lib = new WorkflowLibrary(Library);
        var changes = 0;
        lib.Changed += () => changes++;

        var a = lib.SaveNew(Three("Quick fix!"), WorkflowFile.LibraryScope, null);
        var b = lib.SaveNew(Three("Quick fix!"), WorkflowFile.LibraryScope, null);

        Assert.Equal("quick-fix.json", Path.GetFileName(a));
        Assert.Equal("quick-fix-2.json", Path.GetFileName(b));
        Assert.Equal(2, changes);
        Assert.All(lib.List(null), f => Assert.True(f.IsValid));
    }

    [Fact]
    public void Project_workflows_go_into_the_project_folder_and_list_first()
    {
        var lib = new WorkflowLibrary(Library);
        lib.EnsureDefault();
        var path = lib.SaveNew(Three("Team flow"), WorkflowFile.ProjectScope, Project);

        Assert.Equal(Path.Combine(Project, ".workflows", "team-flow.json"), path);
        var files = lib.List(Project);
        Assert.Equal(WorkflowFile.ProjectScope, files[0].Scope);
        Assert.Equal("Team flow", files[0].Name);
    }

    [Fact]
    public void Delete_moves_the_file_aside_instead_of_erasing_it()
    {
        var lib = new WorkflowLibrary(Library);
        var path = lib.SaveNew(Three("Temp"), WorkflowFile.LibraryScope, null);

        var moved = lib.Delete(path);

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(moved));
        Assert.Contains(".deleted", moved);
        Assert.Empty(lib.List(null));
    }

    [Fact]
    public void Import_copies_a_file_and_rejects_non_workflow_json()
    {
        var lib = new WorkflowLibrary(Library);
        var source = Path.Combine(_root, "shared.json");
        WorkflowJson.Save(Three("Shared"), source);
        var bad = Path.Combine(_root, "bad.json");
        File.WriteAllText(bad, "{ nope");

        var imported = lib.Import(source, WorkflowFile.LibraryScope, null);

        Assert.True(File.Exists(source));
        Assert.Equal("Shared", WorkflowJson.Load(imported).Name);
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => lib.Import(bad, WorkflowFile.LibraryScope, null));
    }

    [Fact]
    public void Renaming_a_key_updates_sessions_transitions_and_prompt_variables()
    {
        var wf = DefaultWorkflows.PlanDevelopReviewRefine();

        var renamed = WorkflowEdits.RenameKey(WorkflowEdits.RenameKey(wf, "develop", "build"), "review", "check");

        Assert.Empty(WorkflowJson.Validate(renamed));
        Assert.Equal(["plan", "build", "check", "refine"], renamed.Stages.Select(s => s.Key));
        var refine = renamed.Stages[3];
        Assert.Equal("continue:build", refine.Session);
        Assert.Equal("check", refine.Transitions.Single().Goto);
        Assert.Contains("{{stages.check.findings}}", refine.PromptTemplate);
        Assert.Contains("{{stages.check.result}}", refine.PromptTemplate);
        Assert.Equal("refine", renamed.Stages[2].Transitions.Single().Goto);
    }

    [Fact]
    public void Unique_keys_come_from_the_name()
    {
        Assert.Equal("code-review", WorkflowEdits.UniqueKey("Code review", ["plan"]));
        Assert.Equal("code-review-2", WorkflowEdits.UniqueKey("Code review", ["code-review"]));
        Assert.Equal("stage", WorkflowEdits.UniqueKey("!!!", []));
        Assert.Equal("stage", WorkflowEdits.UniqueKey("end", []));
    }

    [Fact]
    public void Stage_templates_build_a_valid_three_stage_workflow()
    {
        Assert.Empty(WorkflowJson.Validate(Three("T")));
        Assert.Equal(5, StageTemplates.All.Count);
    }
}
