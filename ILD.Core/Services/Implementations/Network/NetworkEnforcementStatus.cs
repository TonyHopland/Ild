using ILD.Core.Services.RunHosts;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// The egress proxy a deployment runs, and how much its network rules hold for the runs of a run host:
/// <c>enforced</c> where runs and chats sit in containers or pods whose network reaches only ILD, so the
/// proxy is their only way out, or <c>advisory</c> where they run on ILD's own machine and only clients
/// that honour <c>HTTP_PROXY</c> go through it.
/// </summary>
public sealed record NetworkEnforcementStatus(bool ProxyEnabled, int? ProxyPort)
{
    public const string Enforced = "enforced";
    public const string Advisory = "advisory";
    public const string Unverified = "unverified";

    /// <summary>What the probe in a run saw; null where no probe runs.</summary>
    public NetworkEnforcementProbe? Probe { get; init; }

    public static NetworkEnforcementStatus FromEnvironment(EgressProxyOptions proxy)
        => new(proxy.Enabled, proxy.Enabled ? proxy.Port : null);

    /// <summary>
    /// Whether the rules are enforced for runs on a host of <paramref name="kind"/>: only what a probe has seen is claimed, so a
    /// container or pod host is <c>unverified</c> until one has run, and <c>advisory</c> when one got out.
    /// </summary>
    public string EnforcementFor(RunHostKind kind)
        => kind == RunHostKind.Local ? Advisory : Probe?.EscapeObserved switch { true => Advisory, false => Enforced, null => Unverified };

    /// <summary>Why the rules are or are not enforced for runs on a host of <paramref name="kind"/>.</summary>
    public string ReasonFor(RunHostKind kind)
        => (kind == RunHostKind.Local, ProxyEnabled) switch
        {
            (false, _) when Probe?.EscapeObserved == true => "A run connected out directly, bypassing the egress proxy: the cluster's or the engine's network does not enforce the isolation ILD asked for (a CNI that ignores NetworkPolicy, an engine that does not isolate internal networks).",
            (false, true) when Probe?.EscapeObserved is null => "No run has checked yet whether its network really reaches only ILD, so the egress proxy is not known to be the only way out.",
            (false, true) => "A run tried a direct connection out and could not make one, so the egress proxy is its only way out.",
            (false, false) => $"Runs and chats sit on a network that reaches only ILD, and {AgentIsolation.EgressProxyPortEnvVar} is not set, so they have no way out.",
            (true, true) => "Runs and chats run on ILD's own machine: the proxy judges and logs every connection that honours HTTP_PROXY, but traffic that ignores it is not stopped.",
            (true, false) => $"Runs and chats run on ILD's own machine and {AgentIsolation.EgressProxyPortEnvVar} is not set, so no egress proxy is running and agent launches are not pointed at one.",
        };
}
