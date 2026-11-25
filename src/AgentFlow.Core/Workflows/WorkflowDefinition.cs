namespace AgentFlow.Core.Workflows;

/// <summary>An ordered list of stages. Stored as one JSON file per workflow.</summary>
public sealed record WorkflowDefinition
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public List<StageDefinition> Stages { get; init; } = [];
}

public sealed record StageDefinition
{
    /// <summary>Identity used in handoff variables, session references and transitions.</summary>
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";

    public string? Model { get; init; }
    public string? Effort { get; init; }

    public PermissionPreset PermissionPreset { get; init; } = PermissionPreset.ReadOnly;

    /// <summary>Only for <see cref="PermissionPreset.Custom"/>.</summary>
    public string? PermissionMode { get; init; }

    /// <summary>Added to the preset's rules, for example "Bash(npm test *)".</summary>
    public List<string> AllowedTools { get; init; } = [];
    public List<string> DisallowedTools { get; init; } = [];

    /// <summary>"new", "continue:&lt;stage&gt;" (resume that stage's session) or "fork:&lt;stage&gt;" (branch it).</summary>
    public string Session { get; init; } = "new";

    /// <summary>Appended to Claude Code's default system prompt for this stage.</summary>
    public string? RolePrompt { get; init; }

    /// <summary>The user-turn prompt; may contain handoff variables such as {{task}}.</summary>
    public string PromptTemplate { get; init; } = "{{task}}";

    public StageGate Gate { get; init; } = StageGate.None;
    public StageOutput Output { get; init; } = StageOutput.Text;

    public int? MaxTurns { get; init; }
    public decimal? MaxBudgetUsd { get; init; }

    /// <summary>
    /// Evaluated in order after the stage. With none, the run moves to the next stage in the list.
    /// With some but none matching, the run ends.
    /// </summary>
    public List<StageTransition> Transitions { get; init; } = [];
}

public sealed record StageTransition
{
    /// <summary>A verdict value (pass, changes_requested) or null for "always".</summary>
    public string? When { get; init; }

    /// <summary>A stage key, or "end".</summary>
    public string Goto { get; init; } = "";

    /// <summary>How many times this transition may be taken in one run. Null = no limit.</summary>
    public int? MaxLoops { get; init; }
}

public enum PermissionPreset
{
    /// <summary>Plan mode: reads only, no edits, no commands.</summary>
    ReadOnly,
    /// <summary>dontAsk plus build, test and git read commands.</summary>
    ReadOnlyWithCommands,
    /// <summary>acceptEdits plus build, test and git read commands.</summary>
    Edit,
    /// <summary>The stage's own PermissionMode and tool rules only.</summary>
    Custom,
}

public enum StageGate { None, Approve }

public enum StageOutput { Text, Verdict }
