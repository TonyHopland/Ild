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
    // INVARIANT: these ILD_AGENT_* vars (read here, by the app) must name the
    // same user/group/home as the entrypoint's AGENT_USER/AGENT_GROUP/AGENT_HOME
    // (which set up that user's ownership + ACLs on the shared dirs). They are
    // kept in agreement by co-located ENV lines in the Dockerfile — a drift would
    // drop the agent to a uid the entrypoint never provisioned filesystem access
    // for. The two namespaces exist only to separate the shell-side setup from
    // the app-side drop.

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
    /// Rewrite <paramref name="psi"/> in place so its command runs as the
    /// configured agent user, returning the same instance for fluent use at the
    /// call site (<c>Process.Start(AgentUserLauncher.Route(BuildPsi(...)))</c>).
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

        var innerFile = psi.FileName;
        var innerArgs = psi.ArgumentList.ToArray();

        psi.FileName = SetprivCommand;
        psi.ArgumentList.Clear();
        psi.ArgumentList.Add($"--reuid={user}");
        psi.ArgumentList.Add($"--regid={group}");
        psi.ArgumentList.Add("--init-groups");
        // Strip the inheritable + ambient capability sets. The orchestrator holds
        // ambient CAP_SETUID/SETGID to perform this drop; a non-root→non-root
        // setuid does NOT auto-clear caps, so without these the agent would keep
        // them. Clearing inheritable + ambient is enough: the agent binary's
        // post-exec permitted set is (inheritable & file-caps) | ambient = empty.
        // (Clearing the bounding set too would need CAP_SETPCAP, which the
        // orchestrator does not hold — and adds nothing once permitted is empty.)
        psi.ArgumentList.Add("--inh-caps=-all");
        psi.ArgumentList.Add("--ambient-caps=-all");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(innerFile);
        foreach (var arg in innerArgs)
            psi.ArgumentList.Add(arg);

        return psi;
    }

    private static string? NonEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
