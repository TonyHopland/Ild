using ILD.Core.Services.RunHosts.Local;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Data.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A Worktree Preview follows the network mode (ADR-0019): its install steps and
/// services launch without the proxy in Off and proxied otherwise, with the mode
/// read when the start, single-service start or install is called. Observed on real
/// children (each writes the proxy variable it was given to a file). Runs alone because it
/// clears the runner's proxy variables in the real process environment.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public class WorktreePreviewServiceNetworkModeTests : IDisposable
{
    private const string Record = "printf %s \\\"$HTTPS_PROXY\\\" > ";

    private readonly string _worktree = Path.Combine(Path.GetTempPath(), "ild-preview-mode-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] ProxyKeys =
        { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy", "NODE_USE_ENV_PROXY" };

    private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);
    private WorktreePreviewService? _service;

    // The children inherit the runner's own proxy variables, so an Off case would see them; clear them for the test.
    public WorktreePreviewServiceNetworkModeTests()
    {
        Directory.CreateDirectory(_worktree);
        foreach (var key in ProxyKeys)
        {
            _previous[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous) Environment.SetEnvironmentVariable(name, value);
        try { _service?.StopAsync(_worktree).GetAwaiter().GetResult(); } catch { /* best effort */ }
        try { _service?.Dispose(); } catch { /* best effort */ }
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private WorktreePreviewService BuildService(IEgressPolicy? policy)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
        _service = new WorktreePreviewService(factory.Object, new ConfigurationBuilder().Build(), PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance,
            egressProxy: new EgressProxyOptions(true, 3128), credentials: EgressProxyTests.Creds, environment: new TestProcessEnvironment().WithRealPath(), egressPolicy: policy, host: LocalRunHost.Shared);
        return _service;
    }

    private void WriteConfig()
    {
        var server = "node -e \\\"require('http').createServer((q,r)=>{r.end('ok')}).listen(process.env.PORT)\\\"";
        File.WriteAllText(Path.Combine(_worktree, "ild.config.json"), $$"""
        {
          "preview": {
            "defaultProfile": "app",
            "profiles": {
              "app": {
                "install": [ { "cwd": ".", "command": "{{Record}}install.out" } ],
                "services": [
                  {
                    "name": "web",
                    "port": "web",
                    "suggestedPort": {{FindFreePort()}},
                    "command": "{{Record}}web.out; PORT=${PORT} {{server}}",
                    "healthUrl": "http://127.0.0.1:${PORT}/"
                  }
                ]
              }
            }
          }
        }
        """);
    }

    // What a preview command saw as its HTTPS_PROXY: nothing, or a proxy URL signed for a preview, which only the proxy's key reads.
    private static void AssertProxy(bool proxied, string written)
    {
        if (!proxied) { Assert.Equal(string.Empty, written); return; }
        Assert.EndsWith("@127.0.0.1:3128", written);
        Assert.True(AgentIsolationEgressTests.TryReadUrl(EgressProxyTests.Creds, written, out var scope));
        Assert.Equal(LaunchKind.Preview, scope.Kind);
    }

    private string Written(string name) => File.ReadAllText(Path.Combine(_worktree, name));

    [Theory]
    [InlineData(NetworkMode.Off, false)]
    [InlineData(NetworkMode.Monitor, true)]
    [InlineData(NetworkMode.Whitelist, true)]
    public async Task An_install_launches_in_the_mode_the_policy_reports(NetworkMode mode, bool proxied)
    {
        WriteConfig();

        await BuildService(EgressPolicies.Reporting(mode)).InstallAsync(_worktree, cancellationToken: TestContext.Current.CancellationToken);

        AssertProxy(proxied, Written("install.out"));
    }

    [Theory]
    [InlineData(NetworkMode.Off, false)]
    [InlineData(NetworkMode.Blacklist, true)]
    public async Task A_preview_start_launches_its_install_step_and_service_in_the_mode_the_policy_reports(NetworkMode mode, bool proxied)
    {
        WriteConfig();

        await BuildService(EgressPolicies.Reporting(mode)).StartAsync(_worktree, cancellationToken: TestContext.Current.CancellationToken);

        AssertProxy(proxied, Written("install.out"));
        AssertProxy(proxied, Written("web.out"));
    }

    [Theory]
    [InlineData(NetworkMode.Off, false)]
    [InlineData(NetworkMode.Monitor, true)]
    public async Task A_single_service_start_launches_in_the_mode_the_policy_reports(NetworkMode mode, bool proxied)
    {
        WriteConfig();

        await BuildService(EgressPolicies.Reporting(mode)).StartServiceAsync(_worktree, "web", cancellationToken: TestContext.Current.CancellationToken);

        AssertProxy(proxied, Written("web.out"));
    }

    [Theory]
    [InlineData(NetworkMode.Off)]
    [InlineData(NetworkMode.Monitor)]
    [InlineData(NetworkMode.Blacklist)]
    public async Task A_preview_hands_its_run_host_the_mode_it_was_launched_in(NetworkMode mode)
    {
        WriteConfig();
        var config = Path.Combine(_worktree, "ild.config.json");
        var host = new ILD.Tests.Fakes.FakeRunHost().WithDirectory(_worktree).WithFile(config, File.ReadAllText(config));
        var factory = new Mock<IHttpClientFactory>();
        using var service = new WorktreePreviewService(factory.Object, new ConfigurationBuilder().Build(), PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance,
            egressProxy: new EgressProxyOptions(true, 3128), credentials: EgressProxyTests.Creds, environment: new TestProcessEnvironment().WithRealPath(), egressPolicy: EgressPolicies.Reporting(mode), host: host);

        await service.InstallAsync(_worktree, cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(host.Started, c => Assert.Equal(mode, c.NetworkMode));
        Assert.NotEmpty(host.Started);
    }

    [Fact]
    public async Task A_preview_without_a_policy_launches_proxied_as_Monitor()
    {
        WriteConfig();

        await BuildService(policy: null).StartAsync(_worktree, cancellationToken: TestContext.Current.CancellationToken);

        AssertProxy(true, Written("install.out"));
        AssertProxy(true, Written("web.out"));
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
