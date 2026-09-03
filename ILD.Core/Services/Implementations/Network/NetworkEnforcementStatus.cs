namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// Whether the agent's egress is <c>enforced</c> — the container entrypoint
/// installed the uid-keyed firewall rules, so a connection that skips the proxy
/// is dropped — or merely <c>advisory</c>: the proxy still judges and logs every
/// connection that honours <c>HTTP_PROXY</c>, but a hostile agent could bypass
/// it. The entrypoint decides and reports through the environment; the app only
/// surfaces the answer, with the reason, so the degradation is never silent.
/// </summary>
public sealed record NetworkEnforcementStatus(string Enforcement, string Reason, bool ProxyEnabled, int? ProxyPort)
{
    public const string EnforcementEnvVar = "ILD_NETWORK_ENFORCEMENT";
    public const string ReasonEnvVar = "ILD_NETWORK_ENFORCEMENT_REASON";

    public const string Enforced = "enforced";
    public const string Advisory = "advisory";

    public bool IsEnforced => Enforcement == Enforced;

    public static NetworkEnforcementStatus FromEnvironment(EgressProxyOptions proxy)
        => Resolve(
            proxy,
            Environment.GetEnvironmentVariable(EnforcementEnvVar),
            Environment.GetEnvironmentVariable(ReasonEnvVar));

    public static NetworkEnforcementStatus Resolve(EgressProxyOptions proxy, string? reported, string? reportedReason)
    {
        if (!proxy.Enabled)
            return new(Advisory,
                $"{AgentIsolation.EgressProxyPortEnvVar} is not set, so no egress proxy is running and agent launches are not pointed at one.",
                false, null);

        if (string.Equals(reported?.Trim(), Enforced, StringComparison.OrdinalIgnoreCase))
            return new(Enforced,
                string.IsNullOrWhiteSpace(reportedReason)
                    ? "The container entrypoint installed firewall rules that drop agent-uid traffic bypassing the proxy."
                    : reportedReason.Trim(),
                true, proxy.Port);

        return new(Advisory,
            string.IsNullOrWhiteSpace(reportedReason)
                ? "The container entrypoint did not install firewall rules (no NET_ADMIN, uid isolation off, or not running in the container image); agent traffic that ignores HTTP_PROXY is not stopped."
                : reportedReason.Trim(),
            true, proxy.Port);
    }
}
