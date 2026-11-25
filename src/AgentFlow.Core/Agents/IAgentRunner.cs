namespace AgentFlow.Core.Agents;

/// <summary>
/// The one seam between the workflow engine and the agent. Core knows only this interface;
/// AgentFlow.ClaudeCli implements it with the real claude process, tests with fakes.
/// </summary>
public interface IAgentRunner
{
    /// <summary>
    /// Runs one turn and streams its events. The last event is always <see cref="ProcessExited"/>.
    /// Cancelling <paramref name="ct"/> stops the turn: the runner interrupts the agent, keeps
    /// reading whatever it still prints, and only then completes.
    /// </summary>
    IAsyncEnumerable<AgentEvent> RunTurnAsync(StageTurnRequest request, CancellationToken ct = default);
}
