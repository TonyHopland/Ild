namespace ILD.Core.Services.Interfaces;

/// <summary>
/// In-memory registry of which managed coding agents are running right now. An
/// agent execution or interactive provider terminal holds a lease on its agent
/// for exactly its duration; old installs are only deleted while no lease is held.
/// </summary>
public interface IManagedAgentUsage
{
    /// <summary>
    /// Marks the agent behind <paramref name="providerType"/> in use until the returned
    /// lease is disposed. Null when the type is not a managed agent.
    /// </summary>
    IDisposable? Acquire(string? providerType);

    /// <summary>
    /// Runs <paramref name="action"/> only if no lease is held, with new leases held off until it
    /// returns, so nothing can start between the check and the action. False when the agent was busy.
    /// </summary>
    bool TryRunWhenIdle(string agentKey, Action action);
}
