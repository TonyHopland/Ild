namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// Whether the agent's egress is <c>enforced</c> — the container entrypoint
/// installed the uid-keyed firewall rules, so a connection that skips the proxy
/// is dropped — or merely <c>advisory</c>: the proxy still judges and logs every
/// connection that honours <c>HTTP_PROXY</c>, but a hostile agent could bypass
/// it. The entrypoint decides and reports through the environment; the app only
/// surfaces the answer, with the reason, so the degradation is never silent.
/// </summary>
public sealed record NetworkEnforcementStatus(string Enforcement, string Reason, bool ProxyEnabled, int? ProxyPort, bool OpenGroupEnabled = false)
{
    public const string EnforcementEnvVar = "ILD_NETWORK_ENFORCEMENT";
    public const string ReasonEnvVar = "ILD_NETWORK_ENFORCEMENT_REASON";

    public const string Enforced = "enforced";
    public const string Advisory = "advisory";

    public bool IsEnforced => Enforcement == Enforced;

    /// <summary>
    /// Whether an agent launched in network mode Off can reach the network: always when the firewall is not
    /// enforced, and under the firewall only when the entrypoint enabled the open group.
    /// </summary>
    public bool OffAvailable => !IsEnforced || OpenGroupEnabled;

    public static NetworkEnforcementStatus FromEnvironment(EgressProxyOptions proxy)
        => Resolve(
            proxy,
            Environment.GetEnvironmentVariable(EnforcementEnvVar),
            Environment.GetEnvironmentVariable(ReasonEnvVar),
            AgentIsolation.NetworkOpenGroup);

    public static NetworkEnforcementStatus Resolve(EgressProxyOptions proxy, string? reported, string? reportedReason, string? openGroup = null)
    {
        if (!proxy.Enabled)
            return new(Advisory,
                $"{AgentIsolation.EgressProxyPortEnvVar} is not set, so no egress proxy is running and agent launches are not pointed at one.",
                false, null);

        if (string.Equals(reported?.Trim(), Enforced, StringComparison.OrdinalIgnoreCase))
        {
            var openGroupEnabled = !string.IsNullOrWhiteSpace(openGroup);
            return new(Enforced,
                string.IsNullOrWhiteSpace(reportedReason)
                    ? "The container entrypoint installed firewall rules that drop agent-uid traffic bypassing the proxy."
                        + (openGroupEnabled ? "" : " Network mode Off is unavailable: no open group is enabled; agents launched in it have no network.")
                    : reportedReason.Trim(),
                true, proxy.Port, openGroupEnabled);
        }

        return new(Advisory,
            string.IsNullOrWhiteSpace(reportedReason)
                ? "The container entrypoint did not install firewall rules (no NET_ADMIN, uid isolation off, or not running in the container image); agent traffic that ignores HTTP_PROXY is not stopped."
                : reportedReason.Trim(),
            true, proxy.Port);
    }
}
