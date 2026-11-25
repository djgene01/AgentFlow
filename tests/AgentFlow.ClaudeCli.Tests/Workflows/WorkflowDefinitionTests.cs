using AgentFlow.Core.Workflows;

namespace AgentFlow.ClaudeCli.Tests.Workflows;

public class WorkflowDefinitionTests
{
    [Fact]
    public void Default_workflow_is_valid_and_survives_a_json_round_trip()
    {
        var wf = DefaultWorkflows.PlanDevelopReviewRefine();
        Assert.Empty(WorkflowJson.Validate(wf));

        var json = WorkflowJson.Serialize(wf);
        var back = WorkflowJson.Parse(json);
        Assert.Empty(WorkflowJson.Validate(back));
        Assert.Equal(json, WorkflowJson.Serialize(back));
        Assert.Contains("\"permissionPreset\": \"readOnly\"", json);
        Assert.Contains("\"session\": \"continue:develop\"", json);
    }

    [Fact]
    public void Default_workflow_uses_the_requested_models_and_efforts()
    {
        var s = DefaultWorkflows.PlanDevelopReviewRefine().Stages;
        Assert.Equal([("plan", "claude-fable-5-2", "high"), ("develop", "claude-opus-5-5", "medium"),
                      ("review", "claude-sonnet-5-5", "high"), ("refine", "claude-opus-5-5", "high")],
            s.Select(x => (x.Key, x.Model!, x.Effort!)));
    }

    [Fact]
    public void Validation_reports_every_problem()
    {
        var wf = new WorkflowDefinition
        {
            Name = "",
            Stages =
            [
                new() { Key = "a", Name = "A", Session = "continue:b", PromptTemplate = "{{task}} {{stages.zzz.result}} {{bogus}}" },
                new() { Key = "a", Name = "A2", PermissionPreset = PermissionPreset.Custom, PermissionMode = "bypassPermissions" },
                new() { Key = "b", Name = "B", Effort = "huge", Transitions = [new() { When = "pass", Goto = "nowhere", MaxLoops = 0 }] },
            ],
        };

        var errors = WorkflowJson.Validate(wf);

        Assert.Contains(errors, e => e.Contains("needs a name"));
        Assert.Contains(errors, e => e.Contains("not an earlier stage"));
        Assert.Contains(errors, e => e.Contains("no stage 'zzz'"));
        Assert.Contains(errors, e => e.Contains("unknown variable {{bogus}}"));
        Assert.Contains(errors, e => e.Contains("used twice"));
        Assert.Contains(errors, e => e.Contains("bypassPermissions"));
        Assert.Contains(errors, e => e.Contains("unknown effort"));
        Assert.Contains(errors, e => e.Contains("'nowhere'"));
        Assert.Contains(errors, e => e.Contains("needs the stage output to be 'verdict'"));
        Assert.Contains(errors, e => e.Contains("maxLoops"));
    }

    [Fact]
    public void Template_fills_known_variables_and_marks_empty_ones()
    {
        var text = PromptTemplate.Render("T={{task}} P={{ stages.plan.result }} N={{notes}}",
            v => v switch { "task" => "add login", "stages.plan.result" => "1. do it", _ => null });
        Assert.Equal("T=add login P=1. do it N=(none)", text);
    }

    [Fact]
    public void Presets_map_to_cli_permission_modes()
    {
        Assert.Equal("plan", PermissionPresets.Resolve(new StageDefinition { PermissionPreset = PermissionPreset.ReadOnly }).Mode);

        var review = PermissionPresets.Resolve(new StageDefinition { PermissionPreset = PermissionPreset.ReadOnlyWithCommands });
        Assert.Equal("dontAsk", review.Mode);
        Assert.Contains("Bash(dotnet test *)", review.Allowed);
        Assert.Contains("PowerShell(dotnet test *)", review.Allowed); // Windows runs commands through PowerShell
        Assert.Contains("PowerShell(dotnet run *)", review.Allowed);
        Assert.Contains("Edit", review.Disallowed);

        var dev = PermissionPresets.Resolve(new StageDefinition { PermissionPreset = PermissionPreset.Edit, AllowedTools = ["Bash(make *)"] });
        Assert.Equal("acceptEdits", dev.Mode);
        Assert.Contains("Bash(make *)", dev.Allowed);
        Assert.DoesNotContain("Edit", dev.Disallowed);
    }

    [Fact]
    public void Library_writes_the_default_once_and_lists_project_files_first()
    {
        var root = Directory.CreateTempSubdirectory("agentflow-lib-").FullName;
        try
        {
            var lib = new WorkflowLibrary(Path.Combine(root, "library"));
            lib.EnsureDefault();
            File.WriteAllText(Path.Combine(lib.LibraryFolder, DefaultWorkflows.FileName), WorkflowJson.Serialize(
                DefaultWorkflows.PlanDevelopReviewRefine() with { Name = "Edited by me" }));
            lib.EnsureDefault(); // must not overwrite your edit

            var project = Directory.CreateDirectory(Path.Combine(root, "proj", ".workflows")).Parent!.FullName;
            File.WriteAllText(Path.Combine(project, ".workflows", "broken.json"), "{ not json");

            var files = lib.List(project);
            Assert.Equal("project", files[0].Scope);
            Assert.False(files[0].IsValid);
            Assert.Equal("Edited by me", files[1].Name);
            Assert.True(files[1].IsValid);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
