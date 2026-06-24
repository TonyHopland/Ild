using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Reports version state for the managed coding agents (Pi, OpenCode, Claude Code)
/// and performs user-triggered installs/updates: install the version onto the
/// persistent <c>/data</c> volume, then atomically swap it in. Agents are not baked
/// into the image; the <c>/data</c> install is what the adapters launch and it
/// survives container restarts and redeploys.
/// </summary>
public interface IManagedAgentService
{
    /// <summary>The agents this service manages.</summary>
    IReadOnlyList<ManagedAgent> Agents { get; }

    /// <summary>Current + latest version state for every managed agent.</summary>
    Task<IReadOnlyList<ManagedAgentStatus>> GetStatusesAsync(CancellationToken ct = default);

    /// <summary>Current + latest version state for a single managed agent.</summary>
    Task<ManagedAgentStatus> GetStatusAsync(ManagedAgent agent, CancellationToken ct = default);

    /// <summary>
    /// Install the latest published version of <paramref name="agentKey"/> to
    /// <c>/data</c> and atomically make it the active version. A failed or
    /// interrupted install leaves the previously active version untouched.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No managed agent has the given key.</exception>
    /// <exception cref="InvalidOperationException">The install failed (e.g. npm error, registry unreachable).</exception>
    Task<ManagedAgentStatus> UpdateAsync(string agentKey, CancellationToken ct = default);

    /// <summary>
    /// Install <paramref name="agentKey"/> to <c>/data</c> only if it is not
    /// already present (on <c>/data</c> or PATH). A present-but-behind install is
    /// left untouched — updating an existing agent stays a user-triggered action.
    /// Used to auto-provision the agents that configured AI providers need, so a
    /// fresh or upgraded deployment doesn't fail the first AI run on a missing CLI.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No managed agent has the given key.</exception>
    /// <exception cref="InvalidOperationException">An install was needed but failed (e.g. npm error, registry unreachable).</exception>
    Task<ManagedAgentStatus> EnsureInstalledAsync(string agentKey, CancellationToken ct = default);
}
