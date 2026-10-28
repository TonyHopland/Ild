using System.Diagnostics;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Executors;
using ILD.Data.Enums;

namespace ILD.Tests;

/// <summary>
/// The launch side of the Off network mode (ADR-0019): Off leaves the proxy out and
/// swaps the primary gid for the open group the firewall accepts; every other mode
/// launches exactly as before. Driven through the explicit-parameter seams, since
/// the test host is single-uid and has no real open group.
/// </summary>
public class AgentIsolationOpenNetworkTests
{
    private const string Agent = "agent";
    private const string Primary = "agent-primary";
    private const string Open = "ild-net-open";
    private static readonly Guid Provider = Guid.Parse("7f2b9c1e-3a4d-4e5f-8a6b-1c2d3e4f5a6b");

    private static readonly string[] ProxyKeys =
        { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy", "NO_PROXY", "no_proxy", "NODE_USE_ENV_PROXY" };

    private static ProcessStartInfo BuildPsi()
    {
        var psi = new ProcessStartInfo("/data/agents/claude-code/versions/v1/node_modules/.bin/claude")
        {
            UseShellExecute = false,
            WorkingDirectory = "/worktrees/wi-1",
        };
        psi.ArgumentList.Add("--print");
        return psi;
    }

    private static List<string> SetprivArgs(IEnumerable<string> argv)
        => argv.TakeWhile(a => a != "--").ToList();

    [Theory]
    [InlineData(NetworkMode.Monitor)]
    [InlineData(NetworkMode.Whitelist)]
    [InlineData(NetworkMode.Blacklist)]
    public void The_proxied_modes_get_the_provider_scoped_proxy_and_never_the_open_group(NetworkMode mode)
    {
        var (proxy, regid) = AgentIsolation.ResolveLaunchNetwork(mode, "3128", Open, Provider);

        Assert.Equal(AgentIsolation.ResolveEgressProxyUrl("3128", Provider), proxy);
        Assert.Null(regid);
    }

    [Theory]
    [InlineData(NetworkMode.Monitor)]
    [InlineData(NetworkMode.Whitelist)]
    [InlineData(NetworkMode.Blacklist)]
    public void The_proxied_modes_without_a_configured_port_have_no_proxy_as_before(NetworkMode mode)
    {
        var (proxy, regid) = AgentIsolation.ResolveLaunchNetwork(mode, null, Open, null);

        Assert.Null(proxy);
        Assert.Null(regid);
    }

    [Theory]
    [InlineData("3128", Open)]
    [InlineData(null, Open)]
    [InlineData("3128", null)]
    [InlineData("3128", "")]
    public void Off_never_gets_a_proxy_and_gets_the_open_group_only_when_one_is_configured(string? port, string? openGroup)
    {
        var (proxy, regid) = AgentIsolation.ResolveLaunchNetwork(NetworkMode.Off, port, openGroup, Provider);

        Assert.Null(proxy);
        Assert.Equal(string.IsNullOrEmpty(openGroup) ? null : openGroup, regid);
    }

    [Fact]
    public void An_Off_launch_runs_under_the_open_group_keeps_the_supplementary_groups_and_sets_no_proxy_variable()
    {
        var (proxy, regid) = AgentIsolation.ResolveLaunchNetwork(NetworkMode.Off, "3128", Open, Provider);
        var psi = BuildPsi();

        AgentIsolation.Route(psi, Agent, Primary, "/home/agent", egressProxy: proxy, regidOverride: regid);

        Assert.Equal("/usr/bin/setpriv", psi.FileName);
        var setpriv = SetprivArgs(psi.ArgumentList);
        Assert.Equal(new[]
        {
            $"--reuid={Agent}", $"--regid={Open}", "--init-groups", "--inh-caps=-all", "--ambient-caps=-all",
        }, setpriv);
        Assert.DoesNotContain($"--regid={Primary}", psi.ArgumentList);
        foreach (var key in ProxyKeys)
            Assert.False(psi.Environment.ContainsKey(key), $"{key} set for an Off launch");
        Assert.Equal("/home/agent", psi.Environment["HOME"]);
    }

    [Fact]
    public void An_Off_launch_with_no_open_group_keeps_the_primary_group_and_no_proxy()
    {
        var (proxy, regid) = AgentIsolation.ResolveLaunchNetwork(NetworkMode.Off, "3128", null, null);
        var psi = BuildPsi();

        AgentIsolation.Route(psi, Agent, Primary, "/home/agent", egressProxy: proxy, regidOverride: regid);

        Assert.Contains($"--regid={Primary}", psi.ArgumentList);
        foreach (var key in ProxyKeys)
            Assert.False(psi.Environment.ContainsKey(key), $"{key} set for an Off launch");
    }

    [Fact]
    public void An_Off_launch_in_single_uid_mode_has_no_setpriv_and_no_proxy()
    {
        var (proxy, regid) = AgentIsolation.ResolveLaunchNetwork(NetworkMode.Off, "3128", Open, null);
        var psi = BuildPsi();

        AgentIsolation.Route(psi, agentUser: null, agentGroup: null, agentHome: null, egressProxy: proxy, regidOverride: regid);

        Assert.NotEqual("/usr/bin/setpriv", psi.FileName);
        foreach (var key in ProxyKeys)
            Assert.False(psi.Environment.ContainsKey(key), $"{key} set for an Off launch");
    }

    [Theory]
    [InlineData(NetworkMode.Monitor)]
    [InlineData(NetworkMode.Whitelist)]
    [InlineData(NetworkMode.Blacklist)]
    public void A_proxied_launch_is_exactly_what_it_was_before_the_open_group_existed(NetworkMode mode)
    {
        var (proxy, regid) = AgentIsolation.ResolveLaunchNetwork(mode, "3128", Open, Provider);
        var routed = BuildPsi();
        var before = BuildPsi();

        AgentIsolation.Route(routed, Agent, Primary, "/home/agent", egressProxy: proxy, regidOverride: regid);
        AgentIsolation.Route(before, Agent, Primary, "/home/agent", egressProxy: AgentIsolation.ResolveEgressProxyUrl("3128", Provider));

        Assert.Equal(before.FileName, routed.FileName);
        Assert.Equal(before.ArgumentList, routed.ArgumentList);
        Assert.Contains($"--regid={Primary}", routed.ArgumentList);
        Assert.Equal(AgentIsolation.ResolveEgressProxyUrl("3128", Provider), routed.Environment["HTTPS_PROXY"]);
        Assert.Equal("1", routed.Environment["NODE_USE_ENV_PROXY"]);
    }

    [Fact]
    public void The_provider_terminal_command_follows_the_same_rule()
    {
        var (proxy, regid) = AgentIsolation.ResolveLaunchNetwork(NetworkMode.Off, "3128", Open, Provider);

        var routed = AgentIsolation.RouteCommand("/usr/bin/claude", Array.Empty<string>(), Agent, Primary, "/home/agent",
            egressProxy: proxy, regidOverride: regid);

        Assert.Equal("/usr/bin/setpriv", routed.FileName);
        Assert.Contains($"--regid={Open}", routed.Arguments);
        Assert.Contains("--init-groups", routed.Arguments);
        Assert.DoesNotContain($"--regid={Primary}", routed.Arguments);
        foreach (var key in ProxyKeys)
            Assert.False(routed.Environment.ContainsKey(key), $"{key} set for an Off terminal");
    }

    [Fact]
    public void A_Cmd_node_in_Off_runs_under_the_open_group_without_a_proxy_and_in_Monitor_as_before()
    {
        var (offProxy, offRegid) = AgentIsolation.ResolveLaunchNetwork(NetworkMode.Off, "3128", Open, null);
        var (monitorProxy, monitorRegid) = AgentIsolation.ResolveLaunchNetwork(NetworkMode.Monitor, "3128", Open, null);

        var offPsi = CmdNodeExecutor.IsolateCommand(CmdNodeExecutor.ShellStartInfo("echo hi", "/worktrees/wi-1"),
            Agent, Primary, "/home/agent", offProxy, regidOverride: offRegid);
        var monitorPsi = CmdNodeExecutor.IsolateCommand(CmdNodeExecutor.ShellStartInfo("echo hi", "/worktrees/wi-1"),
            Agent, Primary, "/home/agent", monitorProxy, regidOverride: monitorRegid);

        Assert.Contains($"--regid={Open}", offPsi.ArgumentList);
        foreach (var key in ProxyKeys)
            Assert.False(offPsi.Environment.ContainsKey(key), $"{key} set for an Off Cmd node");
        Assert.Contains($"--regid={Primary}", monitorPsi.ArgumentList);
        Assert.Equal("http://127.0.0.1:3128", monitorPsi.Environment["HTTPS_PROXY"]);
    }

    [Fact]
    public void The_open_group_variable_is_stripped_from_what_agents_inherit()
    {
        Assert.Equal("ILD_NETWORK_OPEN_GROUP", AgentIsolation.NetworkOpenGroupEnvVar);
        Assert.Contains(AgentIsolation.NetworkOpenGroupEnvVar, AgentIsolation.OrchestratorTopologyEnvKeys);

        var psi = BuildPsi();
        psi.Environment[AgentIsolation.NetworkOpenGroupEnvVar] = Open;
        AgentIsolation.StripOrchestratorEnvironment(psi);

        Assert.False(psi.Environment.ContainsKey(AgentIsolation.NetworkOpenGroupEnvVar));
    }
}
