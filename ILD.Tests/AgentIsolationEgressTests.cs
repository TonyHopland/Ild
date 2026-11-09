using ILD.Core.Services.RunHosts.Local;
using System.Diagnostics;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.RunHosts;
using ILD.Data.Enums;

namespace ILD.Tests;

/// <summary>
/// The agent side of ADR-0019: every agent launch points the child at the egress
/// proxy, and no orchestrator-side spawn is.
/// </summary>
public class AgentIsolationEgressTests
{
    private static readonly string[] ProxyKeys =
        { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy", "NO_PROXY", "no_proxy", "NODE_USE_ENV_PROXY" };

    [Fact]
    public void No_proxy_stays_the_loopback_set_so_LAN_traffic_goes_through_the_proxy()
    {
        Assert.Equal("localhost,127.0.0.1,::1,[::1]", AgentIsolation.EgressNoProxy);
    }

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

        Assert.Equal((id, (string?)null), EgressProxy.ReadScope(header));
        Assert.Equal(((Guid?)null, (string?)null), EgressProxy.ReadScope("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("provider:not-a-guid"))));
        // Credentials some other tool put on the URL never attribute a provider,
        // whatever their password looks like.
        Assert.Equal(((Guid?)null, (string?)null), EgressProxy.ReadScope("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"someone:{id}"))));
        Assert.Equal(((Guid?)null, (string?)null), EgressProxy.ReadScope("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(id.ToString()))));
        Assert.Equal(((Guid?)null, (string?)null), EgressProxy.ReadScope("Bearer abc"));
        Assert.Equal(((Guid?)null, (string?)null), EgressProxy.ReadScope("Basic %%%"));
    }

    [Fact]
    public void The_installer_zone_in_the_url_round_trips_through_Proxy_Authorization()
    {
        var url = new Uri(new EgressProxyOptions(true, 3128).InstallerUrl("repo-0123abcd")!);
        var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(url.UserInfo));

        Assert.Equal(((Guid?)null, (string?)"repo-0123abcd"), EgressProxy.ReadScope(header));
        // A zone that is not a short lower-case name attributes nothing, so the log column holds only what ILD wrote.
        Assert.Equal(((Guid?)null, (string?)null), EgressProxy.ReadScope("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("installer:Not A Zone"))));
        Assert.Equal(((Guid?)null, (string?)null), EgressProxy.ReadScope("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("installer:" + new string('a', 65)))));
        Assert.Null(new EgressProxyOptions(false, 0).InstallerUrl("agents"));
    }

    [Fact]
    public void A_launch_is_pointed_at_the_proxy_and_keeps_loopback_direct()
    {
        var psi = BuildPsi();

        AgentIsolation.ApplyLaunchEnvironment(psi, egressProxy: "http://127.0.0.1:3128");

        foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
            Assert.Equal("http://127.0.0.1:3128", psi.Environment[key]);
        Assert.Equal(AgentIsolation.EgressNoProxy, psi.Environment["NO_PROXY"]);
        Assert.Equal(AgentIsolation.EgressNoProxy, psi.Environment["no_proxy"]);
        // Node's fetch matches http://[::1]:port only against the bracketed entry.
        Assert.Equal(["localhost", "127.0.0.1", "::1", "[::1]"], psi.Environment["NO_PROXY"]!.Split(','));
        // Node's built-in fetch ignores the variables above without it.
        Assert.Equal("1", psi.Environment["NODE_USE_ENV_PROXY"]);
        Assert.Equal("/data/agents/claude-code/versions/v1/node_modules/.bin/claude", psi.FileName);
    }

    [Fact]
    public void A_launch_gets_no_proxy_when_none_is_configured()
    {
        var psi = BuildPsi();
        foreach (var key in ProxyKeys)
            psi.Environment.Remove(key);

        AgentIsolation.ApplyLaunchEnvironment(psi, egressProxy: null);

        foreach (var key in ProxyKeys)
            Assert.False(psi.Environment.ContainsKey(key), $"{key} set without a proxy");
    }

    [Theory]
    [InlineData(NetworkMode.Monitor, true)]
    [InlineData(NetworkMode.Whitelist, true)]
    [InlineData(NetworkMode.Blacklist, true)]
    [InlineData(NetworkMode.Off, false)]
    public void Every_mode_but_Off_points_a_local_launch_at_the_proxy(NetworkMode mode, bool proxied)
    {
        Assert.Equal(proxied ? "http://127.0.0.1:3128" : null, AgentIsolation.ResolveLaunchProxy(mode, "3128", null));
        Assert.Null(AgentIsolation.ResolveLaunchProxy(mode, null, null));
    }

    [Fact]
    public void Orchestrator_side_spawns_are_never_proxied()
    {
        var psi = BuildPsi();
        foreach (var key in ProxyKeys)
            psi.Environment.Remove(key);

        AgentIsolation.StripOrchestratorEnvironment(psi);

        foreach (var key in ProxyKeys)
            Assert.False(psi.Environment.ContainsKey(key), $"{key} reached an orchestrator-side spawn");
    }

    [Fact]
    public void TerminalCommand_carries_only_the_proxy_with_the_pty_command()
    {
        var routed = AgentIsolation.TerminalCommand("/usr/bin/claude", Array.Empty<string>(),
            egressProxy: "http://provider:11111111-1111-1111-1111-111111111111@127.0.0.1:3128");

        Assert.Equal("/usr/bin/claude", routed.FileName);
        Assert.Equal("http://provider:11111111-1111-1111-1111-111111111111@127.0.0.1:3128", routed.Environment["HTTPS_PROXY"]);
        Assert.Equal(AgentIsolation.EgressNoProxy, routed.Environment["no_proxy"]);
        Assert.Equal("1", routed.Environment["NODE_USE_ENV_PROXY"]);
        Assert.Equal(ProxyKeys.OrderBy(k => k, StringComparer.Ordinal), routed.Environment.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Empty(AgentIsolation.TerminalCommand("/usr/bin/claude", Array.Empty<string>(), egressProxy: null).Environment);
    }

    [Fact]
    public void Proxy_options_and_status_follow_the_port_variable()
    {
        Assert.False(EgressProxyOptions.Parse(null).Enabled);
        Assert.False(EgressProxyOptions.Parse("x").Enabled);
        Assert.Equal(3128, EgressProxyOptions.Parse(" 3128 ").Port);

        var noProxy = NetworkEnforcementStatus.FromEnvironment(EgressProxyOptions.Disabled);
        Assert.False(noProxy.ProxyEnabled);
        Assert.Null(noProxy.ProxyPort);

        var proxied = NetworkEnforcementStatus.FromEnvironment(EgressProxyOptions.Parse("3128"));
        Assert.True(proxied.ProxyEnabled);
        Assert.Equal(3128, proxied.ProxyPort);
    }

    [Theory]
    [InlineData(RunHostKind.Local, "advisory")]
    [InlineData(RunHostKind.Container, "unverified")]
    [InlineData(RunHostKind.Kubernetes, "unverified")]
    public void Rules_are_claimed_enforced_only_after_a_probe_has_failed_to_get_out(RunHostKind kind, string enforcement)
    {
        Assert.Equal(enforcement, NetworkEnforcementStatus.FromEnvironment(EgressProxyOptions.Parse("3128")).EnforcementFor(kind));
        Assert.NotEmpty(NetworkEnforcementStatus.FromEnvironment(EgressProxyOptions.Parse("3128")).ReasonFor(kind));
        Assert.NotEmpty(NetworkEnforcementStatus.FromEnvironment(EgressProxyOptions.Disabled).ReasonFor(kind));
    }
}
