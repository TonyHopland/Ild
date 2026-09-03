using System.Diagnostics;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// The app side of running the coding agent under its own, lower-trust OS user
/// (<c>docs/adr/0014-agent-uid-isolation.md</c>). It owns four jobs, all of them
/// expressed through <c>setpriv</c> and Unix modes:
///
/// <list type="number">
///   <item><b>Crossing to the agent uid</b> — <see cref="Route(ProcessStartInfo)"/>
///   for a spawn, <see cref="RouteCommand(string, IReadOnlyList{string})"/> for
///   APIs that take a command plus argv (the interactive terminal's PTY). Both
///   also strip the orchestrator's secrets (<see cref="SecretEnvironmentKeys"/>)
///   from the environment the agent inherits.</item>
///   <item><b>Keeping the orchestrator's capabilities away from agent-authored
///   code</b> — <see cref="DropInheritedCapabilities(ProcessStartInfo)"/>, for the
///   orchestrator-side commands (preview, git, npm) whose input the agent
///   controls, and <see cref="StripOrchestratorEnvironment(ProcessStartInfo)"/>
///   for the ones that must not inherit the orchestrator's secrets or its own
///   runtime topology either.</item>
///   <item><b>Placing files both uids must share</b> — <see cref="ScratchRoot"/>,
///   the directory whose group/setgid setup lets the two uids hand files back and
///   forth, and <see cref="ProtectFromAgentWrites(string)"/> /
///   <see cref="StageForAgentExec(string)"/> for the trees the agent may execute
///   but must never modify.</item>
///   <item><b>Placing files only the orchestrator may see</b> —
///   <see cref="PrivateRoot"/>, an owner-only root for state that would be a way
///   back across the boundary if the agent could read or plant it.</item>
/// </list>
///
/// <para>
/// The orchestrator (the runtime user, e.g. <c>ild</c>) is granted ambient
/// <c>CAP_SETUID</c>/<c>CAP_SETGID</c>/<c>CAP_KILL</c> by the container entrypoint,
/// which is what lets it — and only it — drop a child to the agent uid.
/// </para>
///
/// <para>
/// Activation is controlled by <c>ILD_AGENT_USER</c>. When it is unset — local
/// development, unit tests, any single-uid deployment — every operation here is a
/// no-op and commands run inline as the current user, exactly as before.
/// </para>
/// </summary>
public static class AgentIsolation
{
    // These ILD_AGENT_* vars are exported by the entrypoint from its own
    // AGENT_USER/AGENT_GROUP/AGENT_HOME, so the user the app drops to is always
    // the one whose ownership and ACLs the entrypoint just provisioned, and
    // clearing AGENT_USER turns isolation off on both sides at once. They are
    // deliberately not set in the image: an independently-set app-side value
    // would keep routing launches through setpriv after the shell-side setup had
    // been switched off.

    /// <summary>When set, the OS user the agent CLI is dropped to (e.g. <c>agent</c>).</summary>
    public const string AgentUserEnvVar = "ILD_AGENT_USER";

    /// <summary>Group for the drop's <c>--regid</c>; defaults to the agent user's name.</summary>
    public const string AgentGroupEnvVar = "ILD_AGENT_GROUP";

    /// <summary>
    /// <c>HOME</c> to set for the agent process so it resolves its own CLI config
    /// (e.g. <c>~/.claude</c>) rather than the orchestrator's. Left untouched when unset.
    /// </summary>
    public const string AgentHomeEnvVar = "ILD_AGENT_HOME";

    /// <summary>
    /// Root for scratch both uids touch. Set by the entrypoint to a shared-group
    /// setgid directory; unset means "use TMPDIR" (see <see cref="ScratchRoot"/>).
    /// </summary>
    public const string ScratchRootEnvVar = "ILD_AGENT_SCRATCH_ROOT";

    /// <summary>
    /// Root for orchestrator-only state. Set by the entrypoint to a directory it
    /// created owner-only; unset means a fixed absolute path under the process
    /// <c>TMPDIR</c> (see <see cref="PrivateRoot"/>).
    /// </summary>
    public const string PrivateRootEnvVar = "ILD_ORCHESTRATOR_PRIVATE_ROOT";

    /// <summary>
    /// Extra, deployment-specific environment variable names (comma-separated) to
    /// strip from the agent's environment on top of <see cref="DefaultSecretEnvKeys"/>.
    /// </summary>
    public const string SecretEnvDenylistEnvVar = "ILD_AGENT_ENV_DENYLIST";

    /// <summary>
    /// Loopback port of the egress proxy (<c>ILD.Core.Services.Implementations.Network.EgressProxy</c>).
    /// When set, every crossing points the child's <c>HTTP_PROXY</c>/<c>HTTPS_PROXY</c>/
    /// <c>ALL_PROXY</c> at it (<see cref="EgressProxyEnvironment"/>); unset means no proxy
    /// is running and no launch is pointed anywhere. Read by the container entrypoint too,
    /// which lets exactly this port through the agent uid's firewall rules.
    /// </summary>
    public const string EgressProxyPortEnvVar = "ILD_NETWORK_PROXY_PORT";

    /// <summary>
    /// Destinations a proxied child reaches directly: ILD's own API (the MCP
    /// callback at <c>ILD_API_URL</c>) and anything else on loopback, which the
    /// agent uid's firewall rules let through untouched.
    /// </summary>
    public const string EgressNoProxy = "localhost,127.0.0.1,::1";

    // The privilege-drop tool, by absolute path. Shipped by util-linux in the
    // image (Dockerfile installs it explicitly), so the location is ours to fix.
    //
    // It must NOT be a bare name resolved on PATH. This is the binary that both
    // crossings and DropInheritedCapabilities exec, so whoever controls its
    // resolution controls whether the drop happens at all — and .NET resolves a
    // bare FileName against the PATH of the *child* environment, which for a
    // Worktree Preview leads with an agent-writable npm bin directory. A planted
    // `setpriv` there would be exec'd in place of the real one, as the
    // orchestrator, with the ambient capabilities still held because nothing ever
    // dropped them: exactly the escalation the wrap exists to prevent, wearing its
    // name. An absolute path removes the resolution step and with it the whole
    // class.
    private const string SetprivCommand = "/usr/bin/setpriv";

    // Orchestrator-only secrets that must never reach the agent uid — the DB
    // connection strings, the encryption-at-rest key, the session-token pepper,
    // the bootstrap password, and the API tokens/keys the orchestrator uses to
    // talk to itself and the WorkItem server. The pepper is what makes a
    // UserSessions row unforgeable by anyone who can only write that table, so an
    // agent holding it would be back to minting its own admin sign-in.
    //
    // .NET pre-populates a child's environment from the current process, so
    // without stripping these the agent would inherit them verbatim.
    //
    // Exact names, not patterns: the adapters set the agent's OWN secrets on the
    // same environment (e.g. Pi's ILD_PI_PROVIDER_API_KEY, opencode's
    // OPENCODE_CONFIG_CONTENT) under different names, and a pattern like
    // "*_API_KEY" or "*TOKEN*" would strip those too. The agent's per-run
    // callback token reaches its MCP server through the MCP config file, and the
    // git commit identity travels in GIT_AUTHOR_*/GIT_COMMITTER_*, so neither is
    // here. New orchestrator secrets must be added to this list (or via
    // ILD_AGENT_ENV_DENYLIST) — the same discipline the shared-volume scheme uses.
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
    /// The configured agent user, or <c>null</c> when uid isolation is disabled.
    /// Callers can key incidental setup (e.g. relaxing scratch-dir permissions so
    /// the agent uid can write) off this without duplicating the env lookup.
    /// </summary>
    public static string? AgentUser => NonEmpty(Environment.GetEnvironmentVariable(AgentUserEnvVar));

    /// <summary>
    /// The group for the drop's <c>--regid</c>, or <c>null</c> when unset (in which
    /// case the crossing defaults it to the agent user's own name).
    /// </summary>
    public static string? AgentGroup => NonEmpty(Environment.GetEnvironmentVariable(AgentGroupEnvVar));

    /// <summary>
    /// The configured agent home, or <c>null</c> when unset. Callers deciding what a
    /// crossing does to <c>HOME</c> want <see cref="ResolveChildHome"/>, not this —
    /// a home configured while <c>ILD_AGENT_USER</c> is not is inert.
    /// </summary>
    public static string? AgentHome => NonEmpty(Environment.GetEnvironmentVariable(AgentHomeEnvVar));

    /// <summary>
    /// The <c>HOME</c> a routed child actually runs with, or <c>null</c> when the
    /// crossing leaves <c>HOME</c> alone — because isolation is off, or because no
    /// agent home is configured.
    ///
    /// <para>
    /// This is the single owner of that rule. <see cref="Route(ProcessStartInfo, string?, string?, string?, string?)"/>
    /// and <see cref="RouteCommand(string, IReadOnlyList{string}, string?, string?, string?)"/>
    /// apply it as part of the crossing, so no launch site has to remember to; and
    /// callers that must <em>derive paths</em> from where the child's <c>HOME</c>
    /// ends up read the same answer here rather than re-deriving it. The Worktree
    /// Preview is the reason it is public: it points <c>NPM_CONFIG_PREFIX</c> at
    /// <c>$HOME/.local</c>, and a prefix computed from a different rule than the one
    /// that set <c>HOME</c> would send <c>npm install -g</c> somewhere the uid
    /// running it cannot write (ADR-0016).
    /// </para>
    ///
    /// <para>
    /// A crossing configured with a user but no home is a real, if odd, shape — the
    /// container's entrypoint always exports the two together, but the parameters
    /// are independent. It resolves to <c>null</c>: the child keeps whatever
    /// <c>HOME</c> it inherited, which is the pre-isolation behaviour and is what
    /// every path derived from it must also fall back to.
    /// </para>
    /// </summary>
    public static string? ResolveChildHome(string? agentUser, string? agentHome)
        => NonEmpty(agentUser) is null ? null : NonEmpty(agentHome);

    /// <summary>
    /// Rewrite <paramref name="psi"/> in place so its command runs as the
    /// configured agent user, returning the same instance for fluent use. Adapters
    /// do not call this directly — <c>CliAgentAdapterBase.StartAgentProcess</c>
    /// applies it so no launch site can forget to.
    /// A no-op returning <paramref name="psi"/> unchanged when
    /// <c>ILD_AGENT_USER</c> is unset. Preserves the redirected streams, working
    /// directory and environment already configured on <paramref name="psi"/>;
    /// <c>setpriv</c> inherits all three and passes them through to the agent.
    /// </summary>
    public static ProcessStartInfo Route(ProcessStartInfo psi)
        => Route(psi, aiProviderId: null);

    /// <summary>
    /// <see cref="Route(ProcessStartInfo)"/> for a launch made on behalf of one AI
    /// provider: the egress proxy URL then names the provider, so the whitelist and
    /// blacklist entries scoped to it apply to this child's connections.
    /// </summary>
    public static ProcessStartInfo Route(ProcessStartInfo psi, Guid? aiProviderId)
        => Route(psi, AgentUser, AgentGroup, AgentHome, EgressProxyUrl(aiProviderId));

    /// <summary>
    /// The wrap primitive with explicit parameters — the env-based
    /// <see cref="Route(ProcessStartInfo)"/> is the production convenience over
    /// this. Exposed so the rewrite can be verified without mutating (global)
    /// process environment variables. No-op when <paramref name="agentUser"/> is
    /// null/blank; <paramref name="agentGroup"/> defaults to the user and
    /// <paramref name="agentHome"/> is left as-is on the psi when null.
    /// <paramref name="egressProxy"/> is applied whenever it is set, uid crossing
    /// or not: the funnel is a property of the launch, the firewall behind it of
    /// the uid, and a single-uid deployment still gets the log.
    /// </summary>
    public static ProcessStartInfo Route(ProcessStartInfo psi, string? agentUser, string? agentGroup, string? agentHome, string? egressProxy = null)
    {
        if (NonEmpty(egressProxy) is { } proxy)
        {
            foreach (var (key, value) in EgressProxyEnvironment(proxy))
                psi.Environment[key] = value;
        }

        var user = NonEmpty(agentUser);
        if (user is null) return psi;

        var group = NonEmpty(agentGroup) ?? user;

        // The agent resolves its login/config state from $HOME. Point it at the
        // agent user's home so it reads the shared credential store through that
        // home's symlinks (and can freely create its own ~/.cache etc.) instead
        // of the orchestrator's home, which it cannot write to.
        if (ResolveChildHome(user, agentHome) is { } home)
            psi.Environment["HOME"] = home;

        // .NET copied the orchestrator's whole environment onto the psi, secrets
        // included. Remove them so the agent — a different, lower-trust uid — never
        // sees the DB strings, encryption key, or the orchestrator's API tokens.
        foreach (var key in SecretEnvironmentKeys)
            psi.Environment.Remove(key);

        Wrap(psi, BuildSetprivArgs(user, group));
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
    /// The variables that describe <em>this</em> orchestrator process's identity and
    /// its private directories, exported by the container entrypoint. Unlike
    /// <see cref="SecretEnvironmentKeys"/> these are not secret — they are simply
    /// wrong for any child that is not this orchestrator, and wrong silently.
    ///
    /// <para>
    /// The demonstration is a child that reads the same names ILD does — in
    /// practice a Worktree Preview of an ILD. It concludes uid isolation is on and
    /// routes its own agent launches through <c>setpriv --reuid</c> without holding
    /// <c>CAP_SETUID</c>, and it points its orchestrator-private root at the outer
    /// instance's, the directory holding the git askpass helper that is handed to
    /// git as <c>GIT_ASKPASS</c> with a repository token in its environment. Both
    /// uids being the same means the collision produces no error, just an
    /// overwrite. That shape is rare; the rule is not, which is why these are
    /// removed for every child rather than for the one that visibly breaks.
    /// </para>
    /// </summary>
    public static IReadOnlyCollection<string> OrchestratorTopologyEnvKeys { get; } =
    [
        AgentUserEnvVar,
        AgentGroupEnvVar,
        AgentHomeEnvVar,
        ScratchRootEnvVar,
        PrivateRootEnvVar,
    ];

    /// <summary>
    /// Remove everything a child inherits by accident rather than by decision: the
    /// orchestrator's secrets (<see cref="SecretEnvironmentKeys"/>) and its own
    /// runtime topology (<see cref="OrchestratorTopologyEnvKeys"/>). .NET
    /// pre-populates a child's environment from the current process, so an
    /// unprepared <see cref="ProcessStartInfo"/> hands both over verbatim.
    ///
    /// <para>
    /// This is deliberately <em>not</em> folded into
    /// <see cref="DropInheritedCapabilities(ProcessStartInfo)"/>. That helper also
    /// wraps <c>ProcessRunner</c> (git, npm) and <c>AIProviderService.RunShellAsync</c>
    /// (Cmd nodes), where a user's command may legitimately rely on the inherited
    /// environment; scrubbing there would change those silently. Callers that must
    /// not inherit say so by name.
    /// </para>
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

        foreach (var key in OrchestratorTopologyEnvKeys)
            psi.Environment.Remove(key);

        return psi;
    }

    /// <summary>
    /// Run a command as the <em>orchestrator's own</em> uid but with no inherited
    /// capabilities. The entrypoint gives the orchestrator ambient
    /// <c>CAP_SETUID</c>/<c>CAP_SETGID</c>/<c>CAP_KILL</c>, and ambient capabilities
    /// are inherited by <em>every</em> descendant in both the permitted and
    /// effective sets. That is fine for children whose input the orchestrator
    /// controls, but several orchestrator-side commands execute agent-authored
    /// input — the preview service runs the worktree's <c>ild.config.json</c>
    /// command, and npm/git run against agent-writable <c>package.json</c> and
    /// <c>.git/config</c>/<c>hooks</c>. A process holding effective
    /// <c>CAP_SETUID</c> can <c>setuid(0)</c>, and an exec with euid 0 is treated
    /// as if the file's capability sets were all ones, so its permitted set
    /// becomes the full bounding set: hijacking such a command would escalate
    /// from "runs as the orchestrator" to "runs as container root". Wrapping them
    /// here keeps the pre-isolation ceiling.
    ///
    /// <para>
    /// Requires no privilege — dropping capabilities from your own inheritable and
    /// ambient sets is always permitted. A no-op when uid isolation is off, since
    /// there are then no ambient capabilities to strip.
    /// </para>
    /// </summary>
    public static ProcessStartInfo DropInheritedCapabilities(ProcessStartInfo psi)
        => DropInheritedCapabilities(psi, AgentUser);

    /// <inheritdoc cref="DropInheritedCapabilities(ProcessStartInfo)"/>
    /// <param name="psi">The command to wrap.</param>
    /// <param name="agentUser">
    /// Isolation marker — when null/blank this is a no-op. Explicit form so the
    /// rewrite is testable without mutating global process environment variables.
    /// </param>
    public static ProcessStartInfo DropInheritedCapabilities(ProcessStartInfo psi, string? agentUser)
    {
        if (NonEmpty(agentUser) is null) return psi;

        Wrap(psi, BuildSetprivArgs(user: null, group: null));
        return psi;
    }

    /// <summary>
    /// The agent-uid wrap for spawn APIs that take a command and argv rather than
    /// a <see cref="ProcessStartInfo"/> — the interactive provider terminal runs
    /// its CLI through a PTY. Returns the command unchanged when isolation is off.
    /// </summary>
    public static AgentCommand RouteCommand(string fileName, IReadOnlyList<string> arguments)
        => RouteCommand(fileName, arguments, aiProviderId: null);

    /// <inheritdoc cref="Route(ProcessStartInfo, Guid?)"/>
    public static AgentCommand RouteCommand(string fileName, IReadOnlyList<string> arguments, Guid? aiProviderId)
        => RouteCommand(fileName, arguments, AgentUser, AgentGroup, AgentHome, EgressProxyUrl(aiProviderId));

    /// <inheritdoc cref="RouteCommand(string, IReadOnlyList{string})"/>
    public static AgentCommand RouteCommand(
        string fileName,
        IReadOnlyList<string> arguments,
        string? agentUser,
        string? agentGroup,
        string? agentHome,
        string? egressProxy = null)
    {
        // The environment overrides travel WITH the command, for the same reason
        // Route applies them to the psi: they are part of the crossing, not extras
        // the caller may forget.
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (NonEmpty(egressProxy) is { } proxy)
        {
            foreach (var (key, value) in EgressProxyEnvironment(proxy))
                environment[key] = value;
        }

        var user = NonEmpty(agentUser);
        if (user is null)
            return new AgentCommand(fileName, arguments, environment.Count == 0 ? EmptyEnvironment : environment);

        var argv = new List<string>(BuildSetprivArgs(user, NonEmpty(agentGroup) ?? user)) { fileName };
        argv.AddRange(arguments);

        // HOME: forgetting it is silent and expensive — the login TUI would write
        // credentials into the orchestrator's home and every later run would read
        // as logged-out, the exact failure routing the terminal to the agent uid
        // exists to prevent.
        if (ResolveChildHome(user, agentHome) is { } home)
            environment["HOME"] = home;

        // Secrets: a PTY child inherits the orchestrator's environment and the
        // caller only *merges* these overrides over it, so — unlike Route, which
        // owns the psi and can remove keys outright — neutralize each secret to an
        // empty value. The agent never needs any of them; an empty DB string or
        // token is inert.
        foreach (var key in ResolveSecretEnvironmentKeys(Environment.GetEnvironmentVariable(SecretEnvDenylistEnvVar)))
            environment[key] = string.Empty;

        return new AgentCommand(SetprivCommand, argv, environment);
    }

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
    /// most Node and Go clients read the upper-case forms.
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
        };

    // Truly immutable so the shared sentinel cannot be mutated by a caller that
    // casts an AgentCommand.Environment back to Dictionary — that would leak into
    // every other RouteCommand result.
    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment =
        System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>
    /// A command line, possibly rewritten to cross to the agent uid, together with
    /// the environment overrides that crossing requires. Callers must apply both.
    /// </summary>
    public readonly record struct AgentCommand(
        string FileName,
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string> Environment);

    /// <summary>
    /// The <c>setpriv</c> argument list, up to and including the <c>--</c>
    /// terminator. With a <paramref name="user"/> it switches uid/gid and loads the
    /// agent's supplementary groups; without one it only drops capabilities,
    /// leaving the uid alone.
    ///
    /// <para>
    /// The capability clears are what make the child safe either way. A
    /// non-root→non-root setuid does NOT auto-clear capabilities, so without them
    /// the child would keep the orchestrator's. Clearing the inheritable + ambient
    /// sets is enough: the child's post-exec permitted set is
    /// <c>(inheritable &amp; file-caps) | ambient</c> = empty. Clearing the bounding
    /// set too would need <c>CAP_SETPCAP</c>, which the orchestrator deliberately
    /// does not hold — and adds nothing once permitted is empty.
    /// </para>
    /// </summary>
    private static string[] BuildSetprivArgs(string? user, string? group)
        => user is null
            ? ["--inh-caps=-all", "--ambient-caps=-all", "--"]
            : [$"--reuid={user}", $"--regid={group}", "--init-groups", "--inh-caps=-all", "--ambient-caps=-all", "--"];

    /// <summary>Rewrite <paramref name="psi"/> to run its command under <c>setpriv</c> with the given prefix.</summary>
    private static void Wrap(ProcessStartInfo psi, string[] setprivArgs)
    {
        // The legacy single-string Arguments and ArgumentList are mutually
        // exclusive in .NET. Moving FileName into the argv would leave a non-empty
        // Arguments applying to setpriv instead of the real command — silently
        // running the wrong thing. No caller uses it; fail loudly if one starts to.
        if (!string.IsNullOrEmpty(psi.Arguments))
            throw new InvalidOperationException(
                "AgentIsolation cannot wrap a ProcessStartInfo that uses the legacy Arguments string; use ArgumentList.");

        var innerFile = psi.FileName;
        var innerArgs = psi.ArgumentList.ToArray();

        psi.FileName = SetprivCommand;
        psi.ArgumentList.Clear();
        foreach (var arg in setprivArgs)
            psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(innerFile);
        foreach (var arg in innerArgs)
            psi.ArgumentList.Add(arg);
    }

    /// <summary>
    /// Where scratch that both uids touch must live: per-run agent session state,
    /// the interactive terminal's cwd. Under uid isolation this is a directory the
    /// entrypoint set up like the other shared trees — owned by the orchestrator,
    /// group-owned by the shared group, <c>setgid</c>, with a default ACL — so
    /// anything created beneath it inherits the shared group and (via the
    /// container's <c>umask 002</c>) stays group-writable.
    ///
    /// <para>
    /// That inheritance is the whole mechanism, and it is why there is no
    /// per-directory permission call here any more. The orchestrator frequently
    /// <em>seeds a file the agent must then keep writing</em> — Pi's restored
    /// session transcript is created by the orchestrator and appended to by pi for
    /// the rest of the turn. Granting the directory alone cannot express that:
    /// create/unlink/rename are governed by the directory, but writing an existing
    /// file is governed by that file's own mode. Placing the tree under a setgid
    /// shared-group root makes the seeded file come out group-writable on its own,
    /// which is exactly why the equivalent claude path (whose transcripts sit in
    /// the shared config store) already worked.
    /// </para>
    ///
    /// <para>
    /// Falls back to the process <c>TMPDIR</c> when unset, which is both the
    /// pre-isolation behavior and what local development and unit tests get.
    /// </para>
    /// </summary>
    public static string ScratchRoot => ResolveScratchRoot(Environment.GetEnvironmentVariable(ScratchRootEnvVar));

    /// <inheritdoc cref="ScratchRoot"/>
    /// <param name="configured">
    /// The configured root, or null/blank for the default. Explicit form so both
    /// branches of the rule are testable without setting the (process-global) env
    /// var — mirrors <see cref="ResolvePrivateRoot"/>.
    /// </param>
    public static string ResolveScratchRoot(string? configured)
        => NonEmpty(configured) ?? Path.GetTempPath();

    /// <summary>
    /// Create a scratch directory under <see cref="ScratchRoot"/> and return its
    /// path. Going through here rather than composing <c>TMPDIR</c> by hand is what
    /// keeps a later scratch directory from silently landing outside the shared
    /// tree and leaving the agent unable to write it.
    /// </summary>
    public static string CreateScratchDirectory(params string[] segments)
    {
        var path = Path.Combine(new[] { ScratchRoot }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Where orchestrator-only state goes — the counterpart to
    /// <see cref="ScratchRoot"/>, and provisioned the same way: the entrypoint
    /// creates it owner-only (<c>0700</c>) before anything else runs and exports
    /// the path, the app just consumes it.
    ///
    /// <para>
    /// It exists because a fixed, predictable path in world-writable <c>/tmp</c>
    /// stops being harmless once the agent is a different uid: the agent can
    /// create the file first, and orchestrator code that guards on "does it
    /// already exist?" will then trust and use the agent's version. The git
    /// askpass helper is the sharp case — it is handed to git as
    /// <c>GIT_ASKPASS</c> with the repository token in its environment, so a
    /// planted script is both arbitrary code as the orchestrator and credential
    /// exfiltration.
    /// </para>
    ///
    /// <para>
    /// What closes that is the <c>0700</c> mode plus the root existing before any
    /// agent-uid process can run — not the location and not path unpredictability.
    /// So the root stays on <c>/tmp</c>, where this state used to live and where it
    /// is discarded with the container, rather than accumulating forever on the
    /// data volume. (The <c>/tmp</c> sticky bit alone would not be enough: it only
    /// prevents replacing a file that already exists.)
    /// </para>
    /// </summary>
    /// <remarks>
    /// Absolute by construction — <see cref="Path.GetFullPath(string)"/> is applied
    /// even to a configured value. This path is handed to git as <c>GIT_ASKPASS</c>
    /// and git runs with the worktree as its cwd, so a relative root resolves
    /// inside the worktree and git dies with "cannot exec", taking down every
    /// authenticated clone/fetch/push. Making it unconditional means no
    /// environment can reintroduce that.
    /// </remarks>
    public static string PrivateRoot => ResolvePrivateRoot(Environment.GetEnvironmentVariable(PrivateRootEnvVar));

    /// <inheritdoc cref="PrivateRoot"/>
    /// <param name="configured">
    /// The configured root, or null/blank for the default. Explicit form so the
    /// absolute-path guarantee is testable for a CONFIGURED value too — reading
    /// only the ambient environment would exercise the (already rooted) fallback
    /// and never the branch that actually broke.
    /// </param>
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

    /// <summary>
    /// Re-assert "the agent may read and execute this, but never write it" on a
    /// tree the orchestrator just created, by stripping group and other write from
    /// every entry (the owning orchestrator keeps its own write access).
    ///
    /// <para>
    /// The managed agent CLIs are npm-installed onto <c>/data</c> at <em>runtime</em>,
    /// which is after the entrypoint's boot-time pass has run, and npm writes them
    /// group-writable under the container's <c>umask 002</c>. Where a default POSIX
    /// ACL is in effect it already clamps new entries, but those <c>setfacl</c>
    /// calls are best-effort, so asserting it here — where the files are created —
    /// is what makes the guarantee independent of the volume filesystem. Boot
    /// repair stays as the fallback.
    /// </para>
    ///
    /// <para>
    /// Symlinks are skipped: their mode is always 0777 and cannot be changed.
    /// A no-op when uid isolation is off. Best-effort: never throws.
    /// </para>
    /// </summary>
    public static void ProtectFromAgentWrites(string path)
        => ProtectFromAgentWrites(path, AgentUser);

    /// <summary>
    /// Build a tree the agent will execute but must never be able to modify,
    /// keeping it unreachable while it is incomplete.
    ///
    /// <para>
    /// <see cref="ProtectFromAgentWrites(string)"/> can only be applied to a
    /// <em>finished</em> tree — it strips write from what is already there. During
    /// a runtime <c>npm install</c>, which builds the whole <c>node_modules</c>
    /// tree as the orchestrator under the container's <c>umask 002</c> with the
    /// shared group inherited from the setgid parent, the agent would otherwise be
    /// free to overwrite files in the very tree the orchestrator later execs as
    /// itself. So the directory is closed to the agent for the duration and
    /// reopened only once its contents are complete and read-only.
    /// </para>
    ///
    /// <para>
    /// The whole transition lives here rather than in the caller because its
    /// ordering is the security property: closed → build → strip write → reopen →
    /// only then may anything point at it. Returning a scope also makes the
    /// failure path right by default — leaving without
    /// <see cref="AgentExecStaging.Publish"/> simply leaves the tree closed, which
    /// is exactly what a failed install wants.
    /// </para>
    /// </summary>
    public static AgentExecStaging StageForAgentExec(string directory) => StageForAgentExec(directory, AgentUser);

    /// <inheritdoc cref="StageForAgentExec(string)"/>
    public static AgentExecStaging StageForAgentExec(string directory, string? agentUser)
        => new(directory, NonEmpty(agentUser));

    /// <summary>
    /// The scope returned by <see cref="StageForAgentExec(string)"/>. Closed on
    /// construction, opened by <see cref="Publish"/>; a no-op throughout when uid
    /// isolation is off.
    /// </summary>
    public sealed class AgentExecStaging : IDisposable
    {
        private readonly string _directory;
        private readonly string? _agentUser;
        private readonly UnixFileMode? _restoreMode;

        internal AgentExecStaging(string directory, string? agentUser)
        {
            _directory = directory;
            _agentUser = agentUser;

            if (agentUser is null || !OperatingSystem.IsLinux())
                return;

            try
            {
                // Capture the mode so Publish can put it back exactly. Restoring a
                // captured value rather than OR-ing bits back on is what keeps the
                // round trip lossless: File.SetUnixFileMode is a raw chmod(2) and
                // clears setgid, and losing setgid here would desynchronise the
                // finished install from the shared-group scheme the entrypoint's
                // tripwire checks — forcing a full re-walk of /data/agents on the
                // next boot, and dropping the tree out of the shared group in the
                // window before that.
                _restoreMode = File.GetUnixFileMode(_directory);

                // Owner-only, but keep setgid so the tree still inherits the shared
                // group as npm builds it. The agent cannot traverse in regardless of
                // how permissive the modes beneath happen to be mid-install.
                TrySetMode(_directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | (_restoreMode.Value & UnixFileMode.SetGroup));
            }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }

        /// <summary>
        /// Make the finished tree read-only to the agent and reopen it. Must be
        /// called before anything points at the tree.
        /// </summary>
        public void Publish()
        {
            ProtectFromAgentWrites(_directory, _agentUser);

            if (_restoreMode is not { } mode)
                return;

            // Restore the captured mode, grant the agent group read+execute (it is
            // a member of the shared group the tree carries), and — crucially —
            // clamp group/other write back off. The clamp is what makes the
            // read-only guarantee a property of THIS method rather than of the
            // caller: the capture reflects whatever mode the directory had when the
            // scope opened, which under the container's umask 002 with a setgid
            // parent is group-writable (2775), and ProtectFromAgentWrites can also
            // fail its walk before reaching this directory. Either way, without the
            // clamp Publish would re-grant write to the tree `current` is about to
            // name. No "other" bits are added — the shared roots deny non-members
            // traversal, so widening here would only extend reach to a future uid.
            TrySetMode(_directory,
                (mode | UnixFileMode.GroupRead | UnixFileMode.GroupExecute)
                & ~(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
        }

        /// <summary>
        /// Deliberately does nothing: if the scope is left without
        /// <see cref="Publish"/> the tree simply stays closed, which is the correct
        /// outcome for a failed or abandoned install.
        /// </summary>
        public void Dispose()
        {
        }
    }

    private static void TrySetMode(string path, UnixFileMode mode)
    {
        try { File.SetUnixFileMode(path, mode); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    /// <inheritdoc cref="ProtectFromAgentWrites(string)"/>
    public static void ProtectFromAgentWrites(string path, string? agentUser)
    {
        if (NonEmpty(agentUser) is null || !OperatingSystem.IsLinux())
            return;

        try
        {
            if (Directory.Exists(path))
                StripSharedWriteRecursive(new DirectoryInfo(path));
            else
                StripSharedWrite(path);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static void StripSharedWriteRecursive(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            // A symlink's own mode is always 0777 and chmod cannot change it;
            // what matters is the mode of the target, which the walk reaches
            // separately when it lives inside this tree.
            if (entry.LinkTarget is not null)
                continue;

            if (entry is DirectoryInfo subdirectory)
                StripSharedWriteRecursive(subdirectory);
            else
                StripSharedWrite(entry.FullName);
        }

        StripSharedWrite(directory.FullName);
    }

    private static void StripSharedWrite(string path)
    {
        try
        {
            var mode = File.GetUnixFileMode(path);
            var stripped = mode & ~(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);
            if (stripped != mode)
                File.SetUnixFileMode(path, stripped);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static string? NonEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
