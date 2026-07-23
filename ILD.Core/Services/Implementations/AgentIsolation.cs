using System.Diagnostics;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// The app side of running the coding agent under its own, lower-trust OS user
/// (<c>docs/adr/0014-agent-uid-isolation.md</c>). It owns three jobs, all of them
/// expressed through <c>setpriv</c> and Unix modes:
///
/// <list type="number">
///   <item><b>Crossing to the agent uid</b> — <see cref="Route(ProcessStartInfo)"/>
///   for a spawn, <see cref="RouteCommand(string, IReadOnlyList{string})"/> for
///   APIs that take a command plus argv (the interactive terminal's PTY).</item>
///   <item><b>Keeping the orchestrator's capabilities away from agent-authored
///   code</b> — <see cref="DropInheritedCapabilities(ProcessStartInfo)"/>, for the
///   orchestrator-side commands (preview, git, npm) whose input the agent
///   controls.</item>
///   <item><b>Placing files both uids must share</b> — <see cref="ScratchRoot"/>,
///   the directory whose group/setgid setup lets the two uids hand files back and
///   forth, and <see cref="ProtectFromAgentWrites(string)"/> for the trees the
///   agent may execute but must never modify.</item>
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

    // The privilege-drop tool. Bare name resolved on PATH (/usr/bin/setpriv,
    // shipped by util-linux in the image).
    private const string SetprivCommand = "setpriv";

    /// <summary>
    /// The configured agent user, or <c>null</c> when uid isolation is disabled.
    /// Callers can key incidental setup (e.g. relaxing scratch-dir permissions so
    /// the agent uid can write) off this without duplicating the env lookup.
    /// </summary>
    public static string? AgentUser => NonEmpty(Environment.GetEnvironmentVariable(AgentUserEnvVar));

    /// <summary>
    /// The agent user's home, or <c>null</c> when unset. Spawn APIs that build
    /// their own environment (the interactive terminal's PTY) need it explicitly;
    /// <see cref="Route(ProcessStartInfo)"/> applies it to the psi itself.
    /// </summary>
    public static string? AgentHome => NonEmpty(Environment.GetEnvironmentVariable(AgentHomeEnvVar));

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
        => Route(psi,
            AgentUser,
            NonEmpty(Environment.GetEnvironmentVariable(AgentGroupEnvVar)),
            NonEmpty(Environment.GetEnvironmentVariable(AgentHomeEnvVar)));

    /// <summary>
    /// The wrap primitive with explicit parameters — the env-based
    /// <see cref="Route(ProcessStartInfo)"/> is the production convenience over
    /// this. Exposed so the rewrite can be verified without mutating (global)
    /// process environment variables. No-op when <paramref name="agentUser"/> is
    /// null/blank; <paramref name="agentGroup"/> defaults to the user and
    /// <paramref name="agentHome"/> is left as-is on the psi when null.
    /// </summary>
    public static ProcessStartInfo Route(ProcessStartInfo psi, string? agentUser, string? agentGroup, string? agentHome)
    {
        var user = NonEmpty(agentUser);
        if (user is null) return psi;

        var group = NonEmpty(agentGroup) ?? user;
        var home = NonEmpty(agentHome);

        // The agent resolves its login/config state from $HOME. Point it at the
        // agent user's home so it reads the shared credential store through that
        // home's symlinks (and can freely create its own ~/.cache etc.) instead
        // of the orchestrator's home, which it cannot write to.
        if (home is not null)
            psi.Environment["HOME"] = home;

        Wrap(psi, BuildSetprivArgs(user, group));
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
        => RouteCommand(fileName, arguments,
            AgentUser,
            NonEmpty(Environment.GetEnvironmentVariable(AgentGroupEnvVar)));

    /// <inheritdoc cref="RouteCommand(string, IReadOnlyList{string})"/>
    public static AgentCommand RouteCommand(string fileName, IReadOnlyList<string> arguments, string? agentUser, string? agentGroup)
    {
        var user = NonEmpty(agentUser);
        if (user is null) return new AgentCommand(fileName, arguments);

        var argv = new List<string>(BuildSetprivArgs(user, NonEmpty(agentGroup) ?? user)) { fileName };
        argv.AddRange(arguments);
        return new AgentCommand(SetprivCommand, argv);
    }

    /// <summary>A command line, possibly rewritten to cross to the agent uid.</summary>
    public readonly record struct AgentCommand(string FileName, IReadOnlyList<string> Arguments);

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
    public static string ScratchRoot
        => NonEmpty(Environment.GetEnvironmentVariable(ScratchRootEnvVar)) ?? Path.GetTempPath();

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
    public static string PrivateRoot
        => Path.GetFullPath(
            NonEmpty(Environment.GetEnvironmentVariable(PrivateRootEnvVar))
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
