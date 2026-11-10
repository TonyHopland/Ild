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
        var c = EgressProxyTests.Creds;
        Assert.Null(AgentIsolation.ResolveEgressProxyUrl(c, null, new LaunchScope()));
        Assert.Null(AgentIsolation.ResolveEgressProxyUrl(c, "", new LaunchScope(Provider: Guid.NewGuid())));
        Assert.Null(AgentIsolation.ResolveEgressProxyUrl(c, "not-a-port", new LaunchScope()));
        Assert.Null(AgentIsolation.ResolveEgressProxyUrl(c, "0", new LaunchScope()));
        Assert.Null(AgentIsolation.ResolveEgressProxyUrl(c, "70000", new LaunchScope()));
    }

    /// <summary>The scope the proxy would read from the <c>Proxy-Authorization</c> a client sends for <paramref name="proxyUrl"/>, exactly as it does with the URL's user info.</summary>
    internal static bool TryReadUrl(EgressCredentials credentials, string proxyUrl, out LaunchScope scope)
        => credentials.TryRead("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(new Uri(proxyUrl).UserInfo)), out scope);

    /// <summary>The scope in the proxy URL a run host put in <paramref name="environment"/> (<c>NAME=value</c> lines), read with the key launches are minted with.</summary>
    internal static LaunchScope ScopeInEnvironment(IEnumerable<string> environment, EgressCredentials credentials)
    {
        var url = environment.Single(e => e.StartsWith("HTTPS_PROXY=", StringComparison.Ordinal))["HTTPS_PROXY=".Length..];
        Assert.True(TryReadUrl(credentials, url, out var scope));
        return scope;
    }

    [Fact]
    public void Every_proxy_url_carries_a_credential_that_names_its_scope()
    {
        var id = Guid.Parse("7f2b9c1e-3a4d-4e5f-8a6b-1c2d3e4f5a6b");
        var credentials = EgressProxyTests.Creds;
        var run = Guid.NewGuid();

        var unscoped = AgentIsolation.ResolveEgressProxyUrl(credentials, "3128", new LaunchScope(LaunchKind.Run, run))!;
        var provider = AgentIsolation.ResolveEgressProxyUrl(credentials, "3128", new LaunchScope(LaunchKind.Chat, run, id))!;
        var installer = new EgressProxyOptions(true, 3128).InstallerUrl(credentials, "repo-0123abcd")!;

        Assert.StartsWith("http://ild:", unscoped);
        Assert.EndsWith("@127.0.0.1:3128", unscoped);
        Assert.True(TryReadUrl(credentials, unscoped, out var none));
        Assert.Equal(new LaunchScope(LaunchKind.Run, run), none);
        Assert.True(TryReadUrl(credentials, provider, out var scoped));
        Assert.Equal(new LaunchScope(LaunchKind.Chat, run, id), scoped);
        Assert.True(TryReadUrl(credentials, installer, out var zoned));
        Assert.Equal(new LaunchScope(LaunchKind.Installer, Installer: "repo-0123abcd"), zoned);
        Assert.Null(new EgressProxyOptions(false, 0).InstallerUrl(credentials, "agents"));
    }

    [Fact]
    public void Two_launches_never_hold_the_same_token()
    {
        var credentials = EgressProxyTests.Creds;
        var (runA, runB, provider) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var tokens = new[]
        {
            credentials.Mint(new LaunchScope(LaunchKind.Run, runA)),
            credentials.Mint(new LaunchScope(LaunchKind.Run, runB)),
            credentials.Mint(new LaunchScope(LaunchKind.Run, runA, provider)),
            credentials.Mint(new LaunchScope(LaunchKind.Run, runB, provider)),
            credentials.Mint(new LaunchScope(LaunchKind.Chat, runA)),
        };

        Assert.Equal(tokens.Length, tokens.Distinct().Count());
        Assert.True(credentials.TryRead("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ild:" + tokens[1])), out var scope));
        Assert.Equal(new LaunchScope(LaunchKind.Run, runB), scope);
    }

    [Fact]
    public void A_credential_a_client_made_up_has_no_scope()
    {
        var id = Guid.NewGuid();
        var credentials = EgressProxyTests.Creds;
        static string Basic(string text) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        var valid = credentials.Mint(new LaunchScope(Provider: id));

        // The shapes the proxy used to take its scope from.
        Assert.False(credentials.TryRead(Basic($"provider:{id}"), out _));
        Assert.False(credentials.TryRead(Basic("installer:agents"), out _));
        Assert.False(credentials.TryRead(Basic("provider:not-a-guid"), out _));
        // A real token under another user name, or with no user, or not Basic, or not base64.
        Assert.False(credentials.TryRead(Basic($"someone:{valid}"), out _));
        Assert.False(credentials.TryRead(Basic(valid), out _));
        Assert.False(credentials.TryRead("Bearer " + valid, out _));
        Assert.False(credentials.TryRead("Basic %%%", out _));
        Assert.False(credentials.TryRead(null, out _));
        // A token signed with another key.
        var other = new EgressCredentials(new byte[32]);
        Assert.False(credentials.TryRead(Basic($"ild:{other.Mint(new LaunchScope(Installer: "agents"))}"), out _));
        // A valid token whose scope was swapped for another's, keeping the old signature.
        var forged = other.Mint(new LaunchScope(Installer: "agents")).Split('.')[0] + "." + valid.Split('.')[1];
        Assert.False(credentials.TryRead(Basic($"ild:{forged}"), out _));
        Assert.True(credentials.TryRead(Basic($"ild:{valid}"), out var read));
        Assert.Equal(id, read.Provider);
    }

    [Fact]
    public void A_token_survives_a_restart_because_the_key_is_kept_in_a_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "ild-egress-" + Guid.NewGuid().ToString("N"), "egress", "proxy.key");
        try
        {
            var token = EgressCredentials.Load(path).Mint(new LaunchScope(Installer: "toolchains"));
            var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ild:" + token));

            Assert.True(EgressCredentials.Load(path).TryRead(header, out var scope));
            Assert.Equal("toolchains", scope.Installer);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path))!, recursive: true);
        }
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
        var url = AgentIsolation.ResolveLaunchProxy(EgressProxyTests.Creds, mode, "3128", new LaunchScope());
        Assert.Equal(proxied, url is not null);
        if (url is not null)
            Assert.EndsWith("@127.0.0.1:3128", url);
        Assert.Null(AgentIsolation.ResolveLaunchProxy(EgressProxyTests.Creds, mode, null, new LaunchScope()));
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
