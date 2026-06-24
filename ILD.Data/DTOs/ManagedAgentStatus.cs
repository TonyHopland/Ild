namespace ILD.Data.DTOs;

/// <summary>
/// Version state for a managed coding agent, surfaced on the AI Provider page.
/// Drives the "Update {installed} → {latest}" button: it is shown enabled only
/// when <see cref="UpdateAvailable"/> is true.
/// </summary>
/// <param name="Key">Stable identifier (e.g. <c>pi</c>, <c>opencode</c>).</param>
/// <param name="DisplayName">Human-facing name.</param>
/// <param name="NpmPackage">npm package the agent is installed from.</param>
/// <param name="InstalledVersion">Currently installed version, or null when not installed/unreadable.</param>
/// <param name="LatestVersion">Latest version on the npm registry, or null when the lookup failed.</param>
/// <param name="UpdateAvailable">True when a newer version than the installed one is available.</param>
/// <param name="Error">Human-readable note when version state could not be fully determined.</param>
public sealed record ManagedAgentStatus(
    string Key,
    string DisplayName,
    string NpmPackage,
    string? InstalledVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    string? Error);
