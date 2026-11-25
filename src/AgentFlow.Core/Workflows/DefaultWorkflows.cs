namespace AgentFlow.Core.Workflows;

/// <summary>The workflow that ships with the app: Plan (Fable, high), Develop (Opus 5.5, medium),
/// Review (Sonnet 5.5, high) and Refine (Opus 5.5, high), with a plan gate and a review loop.</summary>
public static class DefaultWorkflows
{
    public const string FileName = "plan-develop-review-refine.json";

    public static WorkflowDefinition PlanDevelopReviewRefine() => new()
    {
        Name = "Plan, develop, review, refine",
        Description = "Fable plans, you approve, Opus builds, Sonnet reviews independently, Opus fixes what the review found.",
        Stages =
        [
            StageTemplates.Planner(),
            StageTemplates.Developer(),
            StageTemplates.Reviewer() with { Transitions = [new() { When = Verdict.ChangesRequested, Goto = "refine", MaxLoops = 2 }] },
            StageTemplates.Refiner(),
        ],
    };
}
