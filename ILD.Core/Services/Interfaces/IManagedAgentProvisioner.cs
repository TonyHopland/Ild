namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Auto-provisions the managed coding agents (Pi, OpenCode, Claude Code) that
/// configured AI providers need. Agents are no longer baked into the image, so
/// without this an existing provider would fail its first run on a missing CLI
/// until someone clicked Install on the AI Provider page. Runs once at startup
/// for every agent an existing provider uses, and on demand when a provider that
/// needs a not-yet-installed agent is added.
/// </summary>
public interface IManagedAgentProvisioner
{
    /// <summary>
    /// If <paramref name="providerType"/> is backed by a managed agent that
    /// isn't installed yet, install it in the background. No-op for non-managed
    /// provider types or agents already present. Returns immediately; the install
    /// runs detached and any failure is logged (the user can still install
    /// manually from the AI Provider page).
    /// </summary>
    void EnsureInstalledForProviderType(string? providerType);
}
