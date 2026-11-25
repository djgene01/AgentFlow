namespace AgentFlow.Core.Workflows;

/// <summary>Starting points for a new stage in the builder. Every field can be changed afterwards.</summary>
public static class StageTemplates
{
    public sealed record Template(string Id, string Label, string Description, Func<StageDefinition> Create);

    public static IReadOnlyList<Template> All { get; } =
    [
        new("planner", "Planner", "Investigates and writes a plan; read only; waits for your approval", Planner),
        new("developer", "Developer", "Implements the previous stage's plan; can edit files", Developer),
        new("reviewer", "Reviewer", "Independent review with a pass / changes_requested verdict; read only", Reviewer),
        new("refiner", "Refiner", "Fixes the reviewer's findings in the developer's session", Refiner),
        new("custom", "Custom", "An empty stage", Custom),
    ];

    public static StageDefinition Planner() => new()
    {
        Key = "plan",
        Name = "Plan",
        Model = "claude-fable-5-2",
        Effort = "high",
        PermissionPreset = PermissionPreset.ReadOnly,
        Gate = StageGate.Approve,
        MaxTurns = 40,
        RolePrompt = """
            You are the planning stage of a multi-stage workflow. Another model will implement your plan
            without seeing this conversation, so the plan must stand on its own.
            Investigate the code as much as you need. Do not change any files.
            """,
        PromptTemplate = """
            Plan this task for the project in the current folder.

            Produce a concrete, numbered implementation plan: the files to change and why, the approach,
            edge cases and risks, and how to verify the result (build and test commands). Keep it as short
            as it can be while still being complete.

            Task:
            {{task}}
            """,
    };

    public static StageDefinition Developer() => new()
    {
        Key = "develop",
        Name = "Develop",
        Model = "claude-opus-5-5",
        Effort = "medium",
        PermissionPreset = PermissionPreset.Edit,
        RolePrompt = """
            You are the development stage of a multi-stage workflow. Implement the approved plan fully.
            A separate reviewer will check your work against the plan, so follow it and say where you
            had to deviate and why.
            """,
        PromptTemplate = """
            Implement the approved plan below. Keep the changes focused on the task. Build and run the
            tests where the project has them. Finish with a short summary of what you changed.

            Task:
            {{task}}

            Approved plan:
            {{stages.plan.result}}

            Notes from the user:
            {{notes}}
            """,
    };

    public static StageDefinition Reviewer() => new()
    {
        Key = "review",
        Name = "Review",
        Model = "claude-sonnet-5-5",
        Effort = "high",
        PermissionPreset = PermissionPreset.ReadOnlyWithCommands,
        Output = StageOutput.Verdict,
        MaxTurns = 40,
        RolePrompt = """
            You are an independent code reviewer in a multi-stage workflow. You did not write this code.
            Be specific and concrete: every finding names the file, the problem and a fix.
            Do not change any files.
            """,
        PromptTemplate = """
            Review the changes made for this task against the approved plan. Check correctness, missed
            requirements, bugs, error handling, tests and code quality. Read the changed files and run the
            build and tests if that helps.

            Return verdict "pass" when the work is ready, or "changes_requested" with findings that must
            be fixed. Minor style preferences alone are not a reason to request changes.

            Task:
            {{task}}

            Approved plan:
            {{stages.plan.result}}

            Changes since the workflow started:
            {{git.diff}}

            Files edited during the workflow:
            {{changedFiles}}
            """,
    };

    public static StageDefinition Refiner() => new()
    {
        Key = "refine",
        Name = "Refine",
        Model = "claude-opus-5-5",
        Effort = "high",
        PermissionPreset = PermissionPreset.Edit,
        Session = "continue:develop",
        RolePrompt = "You are the refinement stage. Fix what the independent review found.",
        PromptTemplate = """
            An independent reviewer checked your work and requested changes. Address every finding below,
            or explain briefly why a finding should not be changed. Build and run the tests again, then
            summarise what you changed.

            Findings:
            {{stages.review.findings}}

            Reviewer's summary:
            {{stages.review.result}}
            """,
        Transitions = [new() { Goto = "review" }],
    };

    public static StageDefinition Custom() => new()
    {
        Key = "stage",
        Name = "New stage",
        PermissionPreset = PermissionPreset.ReadOnly,
        PromptTemplate = "{{task}}",
    };
}
