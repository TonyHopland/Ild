using System.Diagnostics;
using ILD.Data.Enums;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// What a process ILD starts for an agent is given and kept from: the egress proxy for its network mode
/// (<see cref="ApplyLaunchEnvironment(ProcessStartInfo, Guid?, NetworkMode)"/>), none of the orchestrator's
/// secrets (<see cref="StripOrchestratorEnvironment"/>), and the directories its files go in
/// (<see cref="ScratchRoot"/>, <see cref="AgentReadRoot"/>, <see cref="PrivateRoot"/>).
/// </summary>
public static class AgentIsolation
{
    /// <summary>
    /// Root for agent scratch: pi's per-run directories, the interactive terminal's cwd. Unset means
    /// <c>TMPDIR</c> (see <see cref="ScratchRoot"/>).
    /// </summary>
    public const string ScratchRootEnvVar = "ILD_AGENT_SCRATCH_ROOT";

    /// <summary>Root for orchestrator-only state. Unset means a fixed path under <c>TMPDIR</c> (see <see cref="PrivateRoot"/>).</summary>
    public const string PrivateRootEnvVar = "ILD_ORCHESTRATOR_PRIVATE_ROOT";

    /// <summary>Root for the files ILD hands a launch to read. Unset means a per-user path under <c>TMPDIR</c> (see <see cref="AgentReadRoot"/>).</summary>
    public const string AgentReadRootEnvVar = "ILD_AGENT_READ_ROOT";

    /// <summary>
    /// Extra, deployment-specific environment variable names (comma-separated) to
    /// strip from the agent's environment on top of <see cref="DefaultSecretEnvKeys"/>.
    /// </summary>
    public const string SecretEnvDenylistEnvVar = "ILD_AGENT_ENV_DENYLIST";

    /// <summary>
    /// Loopback port of the egress proxy (<c>ILD.Core.Services.Implementations.Network.EgressProxy</c>).
    /// When set, every agent launch points its <c>HTTP_PROXY</c>/<c>HTTPS_PROXY</c>/<c>ALL_PROXY</c> at it and
    /// sets <c>NODE_USE_ENV_PROXY</c> (<see cref="EgressProxyEnvironment"/>); unset means no proxy is running
    /// and no launch is pointed anywhere.
    /// </summary>
    public const string EgressProxyPortEnvVar = "ILD_NETWORK_PROXY_PORT";

    /// <summary>
    /// Destinations a proxied child reaches directly: ILD's own API (the MCP
    /// callback at <c>ILD_API_URL</c>) and anything else on loopback. Both IPv6 spellings,
    /// because Node's fetch, .NET's HttpClient and npm compare the host with its
    /// brackets, so <c>::1</c> alone does not match <c>http://[::1]:port</c> for
    /// them, while curl and Node's http module match only the bare form.
    /// </summary>
    public const string EgressNoProxy = "localhost,127.0.0.1,::1,[::1]";

    // Orchestrator-only secrets that must never reach an agent — the DB
    // connection strings, the encryption-at-rest key, the session-token pepper,
    // the bootstrap password, and the API tokens/keys the orchestrator uses to
    // talk to itself and the WorkItem server. The pepper is what makes a
    // UserSessions row unforgeable by anyone who can only write that table, so an
    // agent holding it would be back to minting its own admin sign-in.
    //
    // Exact names, not patterns: the adapters set the agent's OWN secrets on the
    // same environment (e.g. Pi's ILD_PI_PROVIDER_API_KEY, opencode's
    // OPENCODE_CONFIG_CONTENT) under different names, and a pattern like
    // "*_API_KEY" or "*TOKEN*" would strip those too. The agent's per-run
    // callback token reaches its MCP server through the MCP config file, and the
    // git commit identity travels in GIT_AUTHOR_*/GIT_COMMITTER_*, so neither is
    // here. New orchestrator secrets must be added to this list (or via
    // ILD_AGENT_ENV_DENYLIST).
    private static readonly string[] DefaultSecretEnvKeys =
    {
        "ILD_DB_CONNECTION_STRING",
        "WORKITEM_DB_CONNECTION_STRING",
        "ILD_SECRET_KEY",
        "ILD_SESSION_TOKEN_PEPPER",
        "ILD_PASSWORD",
        "ILD_USERNAME",
        "WORKITEM_API_KEYS",
        "ILD_WORKITEM_SERVER_API_KEY",
        "ILD_API_TOKEN",
        "ILD_AGENT_TOKEN",
    };

    /// <summary>
    /// Keeps a <c>dotnet build</c> inside a launched process from leaving MSBuild
    /// worker nodes, the MSBuild server or the Roslyn compiler server running after
    /// it returns. Added by <see cref="ApplyLaunchEnvironment(ProcessStartInfo, string?)"/>
    /// only where the launch does not already set the name.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildServerEnvironment { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["UseSharedCompilation"] = "false",
        ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
    };

    /// <summary>
    /// Give an agent launch what every launch in <paramref name="mode"/> gets: the build-server switches and,
    /// unless the mode is Off, the egress proxy for <paramref name="aiProviderId"/>. Returns <paramref name="psi"/>.
    /// </summary>
    public static ProcessStartInfo ApplyLaunchEnvironment(ProcessStartInfo psi, Guid? aiProviderId, NetworkMode mode)
        => ApplyLaunchEnvironment(psi, LaunchProxy(mode, aiProviderId));

    /// <summary>
    /// <see cref="ApplyLaunchEnvironment(ProcessStartInfo, Guid?, NetworkMode)"/> with the proxy URL already
    /// resolved; null or blank for none.
    /// </summary>
    public static ProcessStartInfo ApplyLaunchEnvironment(ProcessStartInfo psi, string? egressProxy)
    {
        foreach (var (key, value) in BuildServerEnvironment)
            psi.Environment.TryAdd(key, value);

        if (NonEmpty(egressProxy) is { } proxy)
        {
            foreach (var (key, value) in EgressProxyEnvironment(proxy))
                psi.Environment[key] = value;
        }
        return psi;
    }

    /// <summary>
    /// The orchestrator-only environment variables scrubbed from the agent —
    /// <see cref="DefaultSecretEnvKeys"/> plus any names in
    /// <c>ILD_AGENT_ENV_DENYLIST</c>.
    /// </summary>
    public static IReadOnlyCollection<string> SecretEnvironmentKeys
        => ResolveSecretEnvironmentKeys(Environment.GetEnvironmentVariable(SecretEnvDenylistEnvVar));

    /// <inheritdoc cref="SecretEnvironmentKeys"/>
    /// <param name="extraDenylist">
    /// Comma-separated extra names, or null/blank for none. Explicit form so the
    /// merge is testable without the process-global env var.
    /// </param>
    public static IReadOnlyCollection<string> ResolveSecretEnvironmentKeys(string? extraDenylist)
    {
        var keys = new HashSet<string>(DefaultSecretEnvKeys, StringComparer.Ordinal);
        if (NonEmpty(extraDenylist) is { } extra)
        {
            foreach (var name in extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                keys.Add(name);
        }
        return keys;
    }

    /// <summary>
    /// Remove the orchestrator's secrets (<see cref="SecretEnvironmentKeys"/>) from what a child inherits.
    /// .NET pre-populates a child's environment from the current process, so an unprepared
    /// <see cref="ProcessStartInfo"/> hands them over verbatim.
    ///
    /// <para>
    /// Call it <em>before</em> layering the child's own environment on, not after:
    /// stripping is about what was inherited, and a value the caller set
    /// deliberately — a Worktree Preview's <c>ild.config.json</c> supplying its own
    /// database connection string, say — has to survive.
    /// </para>
    /// </summary>
    public static ProcessStartInfo StripOrchestratorEnvironment(ProcessStartInfo psi)
    {
        foreach (var key in SecretEnvironmentKeys)
            psi.Environment.Remove(key);
        return psi;
    }

    /// <summary>
    /// The command line for a terminal to start <paramref name="fileName"/> as an agent launch would be: with the
    /// egress proxy for <paramref name="aiProviderId"/> under <paramref name="mode"/> in its environment.
    /// </summary>
    public static AgentCommand TerminalCommand(string fileName, IReadOnlyList<string> arguments, Guid? aiProviderId, NetworkMode mode)
        => TerminalCommand(fileName, arguments, LaunchProxy(mode, aiProviderId));

    /// <inheritdoc cref="TerminalCommand(string, IReadOnlyList{string}, Guid?, NetworkMode)"/>
    public static AgentCommand TerminalCommand(string fileName, IReadOnlyList<string> arguments, string? egressProxy)
        => new(fileName, arguments, NonEmpty(egressProxy) is { } proxy ? EgressProxyEnvironment(proxy) : EmptyEnvironment);

    /// <summary>The proxy URL a launch in <paramref name="mode"/> is pointed at; null in mode Off or when no proxy runs.</summary>
    public static string? LaunchProxy(NetworkMode mode, Guid? aiProviderId)
        => ResolveLaunchProxy(mode, Environment.GetEnvironmentVariable(EgressProxyPortEnvVar), aiProviderId);

    /// <inheritdoc cref="LaunchProxy(NetworkMode, Guid?)"/>
    /// <param name="mode">The mode the launch follows.</param>
    /// <param name="configuredPort">The configured proxy port, or null/blank for no proxy. Explicit form for tests.</param>
    /// <param name="aiProviderId">The provider the launch is made for, if any.</param>
    public static string? ResolveLaunchProxy(NetworkMode mode, string? configuredPort, Guid? aiProviderId)
        => ResolveLaunchProxy(mode, ResolveEgressProxyUrl(configuredPort, aiProviderId));

    /// <inheritdoc cref="LaunchProxy(NetworkMode, Guid?)"/>
    /// <param name="mode">The mode the launch follows.</param>
    /// <param name="proxyUrl">The proxy URL the launch would use in a proxied mode, or null for none.</param>
    public static string? ResolveLaunchProxy(NetworkMode mode, string? proxyUrl)
        => mode == NetworkMode.Off ? null : NonEmpty(proxyUrl);

    /// <summary>
    /// The proxy URL a launch is pointed at, or <c>null</c> when
    /// <see cref="EgressProxyPortEnvVar"/> is unset. With an
    /// <paramref name="aiProviderId"/> the URL carries the provider as Basic-auth
    /// credentials, which the proxy reads back out of <c>Proxy-Authorization</c>
    /// to apply that provider's scoped list entries.
    /// </summary>
    public static string? EgressProxyUrl(Guid? aiProviderId)
        => ResolveEgressProxyUrl(Environment.GetEnvironmentVariable(EgressProxyPortEnvVar), aiProviderId);

    /// <inheritdoc cref="EgressProxyUrl"/>
    /// <param name="configuredPort">The configured port, or null/blank for no proxy. Explicit form for tests.</param>
    /// <param name="aiProviderId">The provider the launch is made for, if any.</param>
    public static string? ResolveEgressProxyUrl(string? configuredPort, Guid? aiProviderId)
        => Network.EgressProxyOptions.Parse(configuredPort).ClientUrl(aiProviderId);

    /// <summary>
    /// The variables that funnel a child's HTTP(S) traffic through
    /// <paramref name="proxyUrl"/>. Both spellings of each, because the tools an
    /// agent runs disagree: curl reads only lower-case <c>http_proxy</c>, while
    /// most Node and Go clients read the upper-case forms. Node's built-in
    /// <c>fetch</c> (corepack's downloads included) ignores all of them unless
    /// <c>NODE_USE_ENV_PROXY=1</c>, which Node reads from v24.0.0 and v22.21.0;
    /// older Node ignores it. Only that upper-case name exists.
    /// </summary>
    public static IReadOnlyDictionary<string, string> EgressProxyEnvironment(string proxyUrl)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HTTP_PROXY"] = proxyUrl,
            ["HTTPS_PROXY"] = proxyUrl,
            ["ALL_PROXY"] = proxyUrl,
            ["http_proxy"] = proxyUrl,
            ["https_proxy"] = proxyUrl,
            ["all_proxy"] = proxyUrl,
            ["NO_PROXY"] = EgressNoProxy,
            ["no_proxy"] = EgressNoProxy,
            ["NODE_USE_ENV_PROXY"] = "1",
        };

    // Truly immutable so the shared sentinel cannot be mutated by a caller that
    // casts an AgentCommand.Environment back to Dictionary — that would leak into
    // every other TerminalCommand result.
    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment =
        System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>A command line together with the environment overrides it must run with. Callers must apply both.</summary>
    public readonly record struct AgentCommand(
        string FileName,
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string> Environment);

    /// <summary>
    /// Where agent scratch lives: pi's per-run agent and session directories, the interactive terminal's cwd.
    /// <see cref="ScratchRootEnvVar"/> when set, otherwise the process <c>TMPDIR</c>.
    /// </summary>
    public static string ScratchRoot => ResolveScratchRoot(Environment.GetEnvironmentVariable(ScratchRootEnvVar));

    /// <inheritdoc cref="ScratchRoot"/>
    /// <param name="configured">The configured root, or null/blank for the default.</param>
    public static string ResolveScratchRoot(string? configured)
        => NonEmpty(configured) ?? Path.GetTempPath();

    /// <summary>Create a scratch directory under <see cref="ScratchRoot"/> and return its path.</summary>
    public static string CreateScratchDirectory(params string[] segments)
    {
        var path = Path.Combine(new[] { ScratchRoot }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Where ILD puts the files it hands a launch to read: pi's ILD extension and the MCP configs given to the
    /// agent CLIs, which carry the ILD API token. <see cref="AgentReadRootEnvVar"/> when set, otherwise a per-user
    /// path under the process <c>TMPDIR</c>. It is per user so that an ILD previewed inside ILD, which runs
    /// without the outer instance's variables, does not land on the outer instance's root.
    /// </summary>
    public static string AgentReadRoot => ResolveAgentReadRoot(Environment.GetEnvironmentVariable(AgentReadRootEnvVar));

    /// <inheritdoc cref="AgentReadRoot"/>
    /// <param name="configured">The configured root, or null/blank for the default.</param>
    public static string ResolveAgentReadRoot(string? configured)
        => Path.GetFullPath(NonEmpty(configured) ?? Path.Combine(Path.GetTempPath(), $"ild-agent-read-{Environment.UserName}"));

    /// <summary>Create a directory under <see cref="AgentReadRoot"/> and return its path.</summary>
    public static string CreateAgentReadDirectory(params string[] segments)
        => CreateAgentReadDirectoryUnder(AgentReadRoot, segments);

    /// <summary>
    /// Create a directory under <paramref name="root"/>, every missing level owner-only, since the files written
    /// there carry the ILD API token, and return its path.
    /// </summary>
    internal static string CreateAgentReadDirectoryUnder(string root, params string[] segments)
    {
        if (OperatingSystem.IsWindows())
            return Directory.CreateDirectory(Path.Combine(new[] { root }.Concat(segments).ToArray())).FullName;

        // One level at a time: a mode passed to CreateDirectory applies to the last level only.
        var path = Path.GetFullPath(root);
        CreateOwnerOnly(path);
        foreach (var segment in segments)
            CreateOwnerOnly(path = Path.Combine(path, segment));
        return path;

        [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
        static void CreateOwnerOnly(string level)
        {
            if (Directory.Exists(level)) return;
            if (Path.GetDirectoryName(level) is { } parent && !Directory.Exists(parent))
                CreateOwnerOnly(parent);
            Directory.CreateDirectory(level, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// Write a file in a <see cref="CreateAgentReadDirectory"/> tree: owner-only, and moved into place whole, so a
    /// reader (pi nodes of one run start in parallel) never sees it half-written.
    /// </summary>
    public static void WriteAgentReadableFile(string path, byte[] content)
    {
        var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            using (var file = new FileStream(temp, options))
                file.Write(content);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Where orchestrator-only state goes, such as the git askpass helper handed to git as <c>GIT_ASKPASS</c>
    /// with a repository token in its environment. <see cref="PrivateRootEnvVar"/> when set, otherwise a fixed
    /// path under the process <c>TMPDIR</c>; made owner-only by <see cref="CreatePrivateDirectory"/>.
    /// </summary>
    /// <remarks>
    /// Absolute by construction — <see cref="Path.GetFullPath(string)"/> is applied
    /// even to a configured value. This path is handed to git as <c>GIT_ASKPASS</c>
    /// and git runs with the worktree as its cwd, so a relative root resolves
    /// inside the worktree and git dies with "cannot exec", taking down every
    /// authenticated clone/fetch/push.
    /// </remarks>
    public static string PrivateRoot => ResolvePrivateRoot(Environment.GetEnvironmentVariable(PrivateRootEnvVar));

    /// <inheritdoc cref="PrivateRoot"/>
    /// <param name="configured">The configured root, or null/blank for the default.</param>
    public static string ResolvePrivateRoot(string? configured)
        => Path.GetFullPath(
            NonEmpty(configured)
            ?? Path.Combine(Path.GetTempPath(), "ild-orchestrator-private"));

    /// <summary>
    /// Create a directory under <see cref="PrivateRoot"/> and return its path,
    /// asserting the root is owner-only so that being private is a property of the
    /// root itself rather than something every caller has to remember to mode.
    /// </summary>
    public static string CreatePrivateDirectory(params string[] segments)
    {
        var root = PrivateRoot;
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsLinux())
        {
            try
            {
                File.SetUnixFileMode(root,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }

        var path = Path.Combine(new[] { root }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private static string? NonEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
