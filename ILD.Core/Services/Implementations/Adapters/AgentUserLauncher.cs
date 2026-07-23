using System.Diagnostics;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// Routes a coding-agent CLI launch through a dedicated, lower-trust OS user so
/// the agent no longer shares the orchestrator's uid — and therefore its
/// <c>ptrace</c> trust boundary and filesystem reach. See
/// <c>docs/adr/0014-agent-uid-isolation.md</c>.
///
/// <para>
/// The orchestrator process (the runtime user, e.g. <c>ild</c>) is granted
/// ambient <c>CAP_SETUID</c>/<c>CAP_SETGID</c> by the container entrypoint, which
/// lets it — and only it — drop a child to the agent uid via <c>setpriv</c>. This
/// helper rewrites a <see cref="ProcessStartInfo"/> so its command runs as:
/// <code>
/// setpriv --reuid=&lt;agent&gt; --regid=&lt;agent&gt; --init-groups \
///         --inh-caps=-all --ambient-caps=-all -- &lt;cmd…&gt;
/// </code>
/// which switches to the agent uid/gid, loads the agent's supplementary groups
/// (the shared group granting access to the worktree and the <c>/data</c> agent
/// installs), and clears the inheritable + ambient sets so the child's post-exec
/// permitted set is empty. A non-root→non-root setuid does not auto-clear
/// capabilities, so the explicit clears matter. (The bounding set is left alone —
/// see <see cref="Route(ProcessStartInfo, string?, string?, string?)"/>.)
/// </para>
///
/// <para>
/// Activation is controlled by <c>ILD_AGENT_USER</c>. When it is unset — local
/// development, unit tests, any single-uid deployment — this is a no-op and the
/// command runs inline as the current user, exactly as before. The container
/// image sets the variable so every real agent launch is isolated.
/// </para>
/// </summary>
public static class AgentUserLauncher
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
    /// Grant the agent uid write access to an orchestrator-created scratch
    /// directory. Adapters call this to say "the agent writes here"; how that is
    /// granted is this seam's business, not theirs.
    ///
    /// <para>
    /// Scratch dirs live under the orchestrator's <c>TMPDIR</c>, outside any
    /// shared-group tree, so there is no group to grant through — the directory
    /// is opened to world read/write instead, with the sticky bit set (<c>01777</c>,
    /// as on <c>/tmp</c> itself) so that although both uids may create files here,
    /// neither can delete or rename the other's. These are throwaway per-run
    /// directories; a tighter fix is to relocate them under an already
    /// shared-group tree, which is tracked as follow-up in ADR-0014.
    /// </para>
    ///
    /// <para>
    /// A no-op when uid isolation is off (the dir stays orchestrator-private) or
    /// on a platform without Unix modes. Best-effort: never throws.
    /// </para>
    /// </summary>
    public static void ShareScratchDirectory(string directory)
        => ShareScratchDirectory(directory, AgentUser);

    /// <inheritdoc cref="ShareScratchDirectory(string)"/>
    /// <param name="directory">The orchestrator-created scratch directory.</param>
    /// <param name="agentUser">
    /// Isolation marker — when null/blank this is a no-op. Explicit form so the
    /// granted mode is testable without mutating global process environment
    /// variables.
    /// </param>
    public static void ShareScratchDirectory(string directory, string? agentUser)
    {
        if (NonEmpty(agentUser) is null || !OperatingSystem.IsLinux())
            return;

        try
        {
            File.SetUnixFileMode(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute
                | UnixFileMode.StickyBit);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
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
