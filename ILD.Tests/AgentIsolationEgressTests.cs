using System.Diagnostics;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Network;

namespace ILD.Tests;

/// <summary>
/// The agent side of ADR-0019: every crossing to the agent uid points the child
/// at the egress proxy, and no orchestrator-side spawn is.
/// </summary>
public class AgentIsolationEgressTests
{
    private static readonly string[] ProxyKeys =
        { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy", "NO_PROXY", "no_proxy" };

    private static ProcessStartInfo BuildPsi()
    {
        var psi = new ProcessStartInfo("/data/agents/claude-code/versions/v1/node_modules/.bin/claude")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = "/worktrees/wi-1",
        };
        psi.ArgumentList.Add("--print");
        return psi;
    }

    [Fact]
    public void No_proxy_url_without_a_configured_port()
    {
        Assert.Null(AgentIsolation.ResolveEgressProxyUrl(null, null));
        Assert.Null(AgentIsolation.ResolveEgressProxyUrl("", Guid.NewGuid()));
        Assert.Null(AgentIsolation.ResolveEgressProxyUrl("not-a-port", null));
        Assert.Null(AgentIsolation.ResolveEgressProxyUrl("0", null));
        Assert.Null(AgentIsolation.ResolveEgressProxyUrl("70000", null));
    }

    [Fact]
    public void The_proxy_url_is_loopback_and_names_the_provider_when_there_is_one()
    {
        var id = Guid.Parse("7f2b9c1e-3a4d-4e5f-8a6b-1c2d3e4f5a6b");

        Assert.Equal("http://127.0.0.1:3128", AgentIsolation.ResolveEgressProxyUrl("3128", null));
        Assert.Equal($"http://provider:{id}@127.0.0.1:3128", AgentIsolation.ResolveEgressProxyUrl("3128", id));
    }

    [Fact]
    public void The_provider_in_the_url_round_trips_through_Proxy_Authorization()
    {
        // Exactly what a client does with the URL's user info.
        var id = Guid.NewGuid();
        var uri = new Uri(AgentIsolation.ResolveEgressProxyUrl("3128", id)!);
        var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(uri.UserInfo));

        Assert.Equal(id, EgressProxy.ReadProviderScope(header));
        Assert.Null(EgressProxy.ReadProviderScope("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("provider:not-a-guid"))));
        // Credentials some other tool put on the URL never attribute a provider,
        // whatever their password looks like.
        Assert.Null(EgressProxy.ReadProviderScope("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"someone:{id}"))));
        Assert.Null(EgressProxy.ReadProviderScope("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(id.ToString()))));
        Assert.Null(EgressProxy.ReadProviderScope("Bearer abc"));
        Assert.Null(EgressProxy.ReadProviderScope("Basic %%%"));
    }

    [Fact]
    public void Route_points_the_agent_at_the_proxy_and_keeps_loopback_direct()
    {
        var psi = BuildPsi();

        AgentIsolation.Route(psi, "agent", "agent", "/home/agent", egressProxy: "http://127.0.0.1:3128");

        foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
            Assert.Equal("http://127.0.0.1:3128", psi.Environment[key]);
        Assert.Equal(AgentIsolation.EgressNoProxy, psi.Environment["NO_PROXY"]);
        Assert.Equal(AgentIsolation.EgressNoProxy, psi.Environment["no_proxy"]);
        // The crossing itself is unchanged.
        Assert.Equal("/usr/bin/setpriv", psi.FileName);
        Assert.Equal("/home/agent", psi.Environment["HOME"]);
    }

    [Fact]
    public void Route_leaves_the_environment_alone_when_no_proxy_is_configured()
    {
        var psi = BuildPsi();

        AgentIsolation.Route(psi, "agent", "agent", "/home/agent", egressProxy: null);

        foreach (var key in ProxyKeys)
            Assert.False(psi.Environment.ContainsKey(key), $"{key} set without a proxy");
    }

    [Fact]
    public void The_funnel_does_not_depend_on_the_uid_crossing()
    {
        // Single-uid deployment: the proxy still logs, so the launch is still pointed at it.
        var psi = BuildPsi();

        AgentIsolation.Route(psi, agentUser: null, agentGroup: null, agentHome: null, egressProxy: "http://127.0.0.1:3128");

        Assert.Equal("http://127.0.0.1:3128", psi.Environment["HTTPS_PROXY"]);
        Assert.NotEqual("/usr/bin/setpriv", psi.FileName);
    }

    [Fact]
    public void Orchestrator_side_spawns_are_never_proxied()
    {
        var psi = BuildPsi();

        AgentIsolation.DropInheritedCapabilities(psi, "agent");
        AgentIsolation.StripOrchestratorEnvironment(psi);

        Assert.Equal("/usr/bin/setpriv", psi.FileName);
        foreach (var key in ProxyKeys)
            Assert.False(psi.Environment.ContainsKey(key), $"{key} reached an orchestrator-side spawn");
    }

    [Fact]
    public void RouteCommand_carries_the_proxy_with_the_pty_command()
    {
        var routed = AgentIsolation.RouteCommand("/usr/bin/claude", Array.Empty<string>(), "agent", "agent", "/home/agent",
            egressProxy: "http://provider:11111111-1111-1111-1111-111111111111@127.0.0.1:3128");

        Assert.Equal("/usr/bin/setpriv", routed.FileName);
        Assert.Equal("http://provider:11111111-1111-1111-1111-111111111111@127.0.0.1:3128", routed.Environment["HTTPS_PROXY"]);
        Assert.Equal(AgentIsolation.EgressNoProxy, routed.Environment["no_proxy"]);
        Assert.Equal("/home/agent", routed.Environment["HOME"]);
    }

    [Fact]
    public void RouteCommand_carries_only_the_proxy_when_isolation_is_off()
    {
        var routed = AgentIsolation.RouteCommand("/usr/bin/claude", Array.Empty<string>(), null, null, null,
            egressProxy: "http://127.0.0.1:3128");

        Assert.Equal("/usr/bin/claude", routed.FileName);
        Assert.Equal(ProxyKeys.OrderBy(k => k, StringComparer.Ordinal), routed.Environment.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Proxy_options_and_enforcement_status_follow_the_port_variable()
    {
        Assert.False(EgressProxyOptions.Parse(null).Enabled);
        Assert.False(EgressProxyOptions.Parse("x").Enabled);
        Assert.Equal(3128, EgressProxyOptions.Parse(" 3128 ").Port);

        var noProxy = NetworkEnforcementStatus.Resolve(EgressProxyOptions.Disabled, "enforced", null);
        Assert.Equal(NetworkEnforcementStatus.Advisory, noProxy.Enforcement);
        Assert.False(noProxy.ProxyEnabled);

        var enforced = NetworkEnforcementStatus.Resolve(EgressProxyOptions.Parse("3128"), "enforced", "nft rules installed for uid 10002");
        Assert.True(enforced.IsEnforced);
        Assert.Equal("nft rules installed for uid 10002", enforced.Reason);
        Assert.Equal(3128, enforced.ProxyPort);

        var advisory = NetworkEnforcementStatus.Resolve(EgressProxyOptions.Parse("3128"), "advisory", "NET_ADMIN not granted");
        Assert.False(advisory.IsEnforced);
        Assert.Equal("NET_ADMIN not granted", advisory.Reason);

        var unreported = NetworkEnforcementStatus.Resolve(EgressProxyOptions.Parse("3128"), null, null);
        Assert.Equal(NetworkEnforcementStatus.Advisory, unreported.Enforcement);
        Assert.NotEmpty(unreported.Reason);
    }
}
