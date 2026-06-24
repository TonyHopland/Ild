namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// A coding agent CLI that ILD can install onto the persistent <c>/data</c>
/// volume and update on demand (see the AI Provider page "Update agent" flow).
/// </summary>
/// <param name="Key">Stable url-safe identifier used in API routes and on-disk paths.</param>
/// <param name="DisplayName">Human-facing name shown in the UI.</param>
/// <param name="NpmPackage">npm package installed to provide the binary.</param>
/// <param name="BinaryName">Executable dropped into the package's <c>node_modules/.bin</c>.</param>
/// <param name="Command">
/// Fallback command used to launch the agent when no <c>/data</c> install is
/// present — the bare name, resolved against <c>PATH</c>. Agents are no longer
/// baked into the image, so this resolves only if one was installed separately;
/// otherwise the launch fails until the user installs it from the AI Provider page.
/// </param>
public sealed record ManagedAgent(
    string Key,
    string DisplayName,
    string NpmPackage,
    string BinaryName,
    string Command);

/// <summary>
/// The set of coding agents ILD manages via npm. Pi, OpenCode and Claude Code
/// are each installed from an npm package so a single update mechanism (no
/// <c>curl</c> dependency) covers all three. They are not baked into the image:
/// installs land on <c>/data</c> and are launched from there, so a fresh
/// deployment installs them once from the AI Provider page.
/// </summary>
public static class ManagedAgentCatalog
{
    public static readonly ManagedAgent Pi =
        new("pi", "Pi", "@earendil-works/pi-coding-agent", "pi", "pi");

    public static readonly ManagedAgent OpenCode =
        new("opencode", "OpenCode", "opencode-ai", "opencode", "opencode");

    public static readonly ManagedAgent ClaudeCode =
        new("claude-code", "Claude Code", "@anthropic-ai/claude-code", "claude", "claude");

    public static readonly IReadOnlyList<ManagedAgent> All = [Pi, OpenCode, ClaudeCode];

    /// <summary>Look up a managed agent by its <see cref="ManagedAgent.Key"/> (case-insensitive); null when unknown.</summary>
    public static ManagedAgent? Find(string? key)
        => All.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// On-disk layout and launch-path resolution for <see cref="ManagedAgent"/>
/// installs under the persistent data volume. Each agent lives at
/// <c>{dataRoot}/agents/{key}/</c> with one or more npm installs under
/// <c>versions/{versionId}/</c> and a plain-text <c>current</c> pointer file
/// naming the active version. Swapping the active version is a single atomic
/// rename of the pointer file, so a half-finished install never breaks the live
/// agent (the pointer still names the previous, working version).
/// </summary>
public static class ManagedAgentInstall
{
    /// <summary>Root directory holding every managed-agent install for an agent.</summary>
    public static string AgentRoot(string dataRoot, ManagedAgent agent)
        => Path.Combine(dataRoot, "agents", agent.Key);

    /// <summary>Directory under which each versioned npm install lives.</summary>
    public static string VersionsRoot(string dataRoot, ManagedAgent agent)
        => Path.Combine(AgentRoot(dataRoot, agent), "versions");

    /// <summary>Directory for a specific install (its npm <c>--prefix</c>).</summary>
    public static string VersionDir(string dataRoot, ManagedAgent agent, string versionId)
        => Path.Combine(VersionsRoot(dataRoot, agent), versionId);

    /// <summary>Plain-text pointer file naming the active version id.</summary>
    public static string PointerFile(string dataRoot, ManagedAgent agent)
        => Path.Combine(AgentRoot(dataRoot, agent), "current");

    /// <summary>The agent binary inside a given install directory.</summary>
    public static string BinaryIn(string versionDir, ManagedAgent agent)
        => Path.Combine(versionDir, "node_modules", ".bin", agent.BinaryName);

    /// <summary>
    /// The launch path of the active <c>/data</c> install, or null when no
    /// install is present or the pointer names a version whose binary is gone.
    /// </summary>
    public static string? CurrentBinaryPath(string dataRoot, ManagedAgent agent)
    {
        var pointer = PointerFile(dataRoot, agent);
        if (!File.Exists(pointer)) return null;

        string versionId;
        try { versionId = File.ReadAllText(pointer).Trim(); }
        catch { return null; }

        if (string.IsNullOrWhiteSpace(versionId)) return null;

        var binary = BinaryIn(VersionDir(dataRoot, agent, versionId), agent);
        return File.Exists(binary) ? binary : null;
    }

    /// <summary>
    /// Resolve the data root the same way the host does (see Program.cs): the
    /// <c>ILD_DATA_PATH</c> env var wins, otherwise the literal <c>data</c>.
    /// Adapters read it directly here so launch-path resolution stays free of
    /// DI plumbing.
    /// </summary>
    public static string ResolveDataRoot()
        => Environment.GetEnvironmentVariable("ILD_DATA_PATH") is { Length: > 0 } p ? p : "data";

    /// <summary>
    /// The command an adapter should launch for <paramref name="agent"/>:
    /// the active <c>/data</c> install when present, otherwise the bare command
    /// name on <c>PATH</c> (which resolves only if the agent was installed
    /// separately, since it is no longer baked into the image). An explicit
    /// <c>binaryPath</c> in the provider config still overrides this (it is
    /// consulted first by the caller).
    /// </summary>
    public static string ResolveCommand(ManagedAgent agent)
        => ResolveCommand(agent, ResolveDataRoot());

    /// <inheritdoc cref="ResolveCommand(ManagedAgent)"/>
    public static string ResolveCommand(ManagedAgent agent, string dataRoot)
        => CurrentBinaryPath(dataRoot, agent) ?? agent.Command;
}
