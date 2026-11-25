namespace AgentFlow.Core.Agents;

/// <summary>Everything needed to run one turn of one stage.</summary>
public sealed record StageTurnRequest
{
    public required string WorkingDirectory { get; init; }
    public required string Prompt { get; init; }

    /// <summary>A model alias (fable, opus, sonnet, haiku) or a full model name. Null = CLI default.</summary>
    public string? Model { get; init; }

    /// <summary>low, medium, high, xhigh or max. Null = CLI default.</summary>
    public string? Effort { get; init; }

    /// <summary>default, acceptEdits, plan, auto or dontAsk. bypassPermissions is refused.</summary>
    public string? PermissionMode { get; init; }

    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    public IReadOnlyList<string> DisallowedTools { get; init; } = [];

    public SessionSpec Session { get; init; } = SessionSpec.NewSession();

    /// <summary>Stage role prompt, appended to Claude Code's default system prompt.</summary>
    public string? AppendSystemPrompt { get; init; }

    public int? MaxTurns { get; init; }
    public decimal? MaxBudgetUsd { get; init; }

    /// <summary>A JSON Schema the result must match; arrives in <see cref="TurnResult.StructuredOutput"/>.</summary>
    public string? JsonSchema { get; init; }

    public bool IncludePartialMessages { get; init; } = true;

    /// <summary>Extra MCP servers for this turn (for example the video studio), added with --mcp-config.</summary>
    public IReadOnlyList<McpServerSpec> McpServers { get; init; } = [];

    /// <summary>
    /// Answers this turn's permission prompts (usually by asking you in the chat). Null: anything a
    /// rule or the permission mode does not allow is denied, as in phases 1 to 4.
    /// </summary>
    public Func<ApprovalRequest, CancellationToken, Task<ApprovalAnswer>>? ApprovalHandler { get; init; }
}

/// <summary>How the turn relates to earlier sessions.</summary>
public abstract record SessionSpec
{
    /// <summary>Start a new session under an ID the app chose.</summary>
    public sealed record New(Guid SessionId) : SessionSpec;

    /// <summary>Continue an earlier session; with Fork, branch it under a new ID instead.</summary>
    public sealed record Resume(string SessionId, bool Fork = false) : SessionSpec;

    public static SessionSpec NewSession() => new New(Guid.NewGuid());
}
