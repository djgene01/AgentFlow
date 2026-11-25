namespace AgentFlow.ClaudeCli;

public sealed record ClaudeRunnerOptions
{
    /// <summary>Path to claude.exe (or claude.cmd). Found with <see cref="ClaudeCliLocator"/> when null.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Arguments placed before the generated ones. Tests use this to pass FakeClaude its fixture.</summary>
    public IReadOnlyList<string> PrefixArguments { get; init; } = [];

    /// <summary>On stop, send Ctrl+C first and wait this long before killing the process tree.</summary>
    public TimeSpan InterruptGracePeriod { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>When false, stop kills the process tree immediately.</summary>
    public bool TryInterruptFirst { get; init; } = true;

    /// <summary>Path to AgentFlow.CtrlC.exe. Looked up next to the host when null; if missing, stop kills.</summary>
    public string? InterruptHelperPath { get; init; }

    /// <summary>The app's permission server. Used for turns that carry an ApprovalHandler.</summary>
    public AgentFlow.Core.Agents.IApprovalChannel? ApprovalChannel { get; init; }

    /// <summary>Called with every raw stdout line before parsing. The spike uses it to record fixtures.</summary>
    public Action<string>? RawLineObserver { get; init; }
}
