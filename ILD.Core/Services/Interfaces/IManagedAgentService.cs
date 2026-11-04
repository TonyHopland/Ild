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
    /// <c>/data</c> and atomically make it the active version, and lift any hold a rollback
    /// put on newer versions. A failed or interrupted install leaves the previously active
    /// version untouched.
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

    /// <summary>
    /// What auto-update would do for <paramref name="agentKey"/> now, read from the registry and
    /// changing nothing: the newest stable version published at least <paramref name="cooldown"/>
    /// before <paramref name="now"/>, not newer than the registry's <c>latest</c> tag and newer than
    /// any version a rollback holds back, and the next release that is still inside the cooldown
    /// and when it passes it. Null when the registry cannot be read.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No managed agent has the given key.</exception>
    Task<AutoUpdatePlan?> GetAutoUpdatePlanAsync(string agentKey, TimeSpan cooldown, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// Install exactly <paramref name="version"/> of <paramref name="agentKey"/> and make it active
    /// at once, whether or not the agent is in use: a process already running keeps the install it
    /// was launched from, and only new launches follow the switch. Does nothing and returns
    /// <see cref="AutoUpdateResult.NotNewer"/> when the installed version is already
    /// <paramref name="version"/> or newer (a manual update may have got there first).
    /// </summary>
    /// <exception cref="KeyNotFoundException">No managed agent has the given key.</exception>
    /// <exception cref="InvalidOperationException">The install failed (e.g. npm error).</exception>
    Task<AutoUpdateResult> AutoUpdateAsync(string agentKey, string version, CancellationToken ct = default);

    /// <summary>
    /// Switch <paramref name="agentKey"/> back to the install that was active before its last swap,
    /// immediately and even while the agent is in use (a run in progress keeps the install it started
    /// in). The version rolled back from is held: auto-update installs only something newer than it,
    /// until the next manual update. The previous-version marker is cleared, so a second rollback is
    /// refused until the next update; the install rolled back from is removed once the agent is idle.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No managed agent has the given key.</exception>
    /// <param name="expectedPreviousVersion">
    /// The version the caller was shown it would roll back to. When given and the previous version is
    /// no longer that one (an update landed meanwhile), nothing is rolled back.
    /// </param>
    /// <exception cref="InvalidOperationException">There is no previous version to roll back to, or it is not the expected one.</exception>
    Task<ManagedAgentStatus> RollbackAsync(string agentKey, string? expectedPreviousVersion = null, CancellationToken ct = default);

    /// <summary>
    /// Deletes the installs of <paramref name="agentKey"/> other than the active one and the one a
    /// rollback would return to, if no run or terminal is using the agent right now; otherwise
    /// nothing is deleted and a later call gets another chance. Does nothing while an install or
    /// rollback of the agent is in progress, or when there is nothing to delete.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No managed agent has the given key.</exception>
    Task RemoveOldVersionsWhenIdleAsync(string agentKey, CancellationToken ct = default);
}

/// <summary>What <see cref="IManagedAgentService.GetAutoUpdatePlanAsync"/> found.</summary>
/// <param name="Target">The version to install now, or null when there is none.</param>
/// <param name="Next">A newer release that is still inside the cooldown, or null.</param>
/// <param name="NextAfter">When <paramref name="Next"/> passes the cooldown.</param>
public sealed record AutoUpdatePlan(string? Target, string? Next, DateTimeOffset? NextAfter);

public enum AutoUpdateResult
{
    Installed,
    NotNewer,
}
