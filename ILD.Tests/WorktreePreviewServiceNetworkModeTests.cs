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
/// children (each writes the proxy variable it was given to a file), since a crossing
/// to the agent uid cannot be executed on this host.
/// </summary>
[Collection(PreviewPortsCollection.Name)]
public class WorktreePreviewServiceNetworkModeTests : IDisposable
{
    private const string Proxy = "http://127.0.0.1:3128";
    private const string Record = "printf %s \\\"$HTTPS_PROXY\\\" > ";

    private readonly string _worktree = Path.Combine(Path.GetTempPath(), "ild-preview-mode-" + Guid.NewGuid().ToString("N"));
    private WorktreePreviewService? _service;

    public WorktreePreviewServiceNetworkModeTests() => Directory.CreateDirectory(_worktree);

    public void Dispose()
    {
        try { _service?.StopAsync(_worktree).GetAwaiter().GetResult(); } catch { /* best effort */ }
        try { _service?.Dispose(); } catch { /* best effort */ }
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private WorktreePreviewService BuildService(
        IEgressPolicy? policy, string? agentUser = null, string? agentGroup = null, string? openGroup = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
        _service = new WorktreePreviewService(factory.Object, new ConfigurationBuilder().Build(), PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance, agentUser, agentGroup, agentHome: null,
            egressProxy: Proxy, environment: new TestProcessEnvironment().WithRealPath(),
            networkOpenGroup: openGroup, egressPolicy: policy);
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

    private string Written(string name) => File.ReadAllText(Path.Combine(_worktree, name));

    [Theory]
    [InlineData(NetworkMode.Off, "")]
    [InlineData(NetworkMode.Monitor, Proxy)]
    [InlineData(NetworkMode.Whitelist, Proxy)]
    public async Task An_install_launches_in_the_mode_the_policy_reports(NetworkMode mode, string expected)
    {
        WriteConfig();

        await BuildService(EgressPolicies.Reporting(mode)).InstallAsync(_worktree, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected, Written("install.out"));
    }

    [Theory]
    [InlineData(NetworkMode.Off, "")]
    [InlineData(NetworkMode.Blacklist, Proxy)]
    public async Task A_preview_start_launches_its_install_step_and_service_in_the_mode_the_policy_reports(NetworkMode mode, string expected)
    {
        WriteConfig();

        await BuildService(EgressPolicies.Reporting(mode)).StartAsync(_worktree, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected, Written("install.out"));
        Assert.Equal(expected, Written("web.out"));
    }

    [Theory]
    [InlineData(NetworkMode.Off, "")]
    [InlineData(NetworkMode.Monitor, Proxy)]
    public async Task A_single_service_start_launches_in_the_mode_the_policy_reports(NetworkMode mode, string expected)
    {
        WriteConfig();

        await BuildService(EgressPolicies.Reporting(mode)).StartServiceAsync(_worktree, "web", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected, Written("web.out"));
    }

    [Fact]
    public async Task A_preview_without_a_policy_launches_proxied_as_Monitor()
    {
        WriteConfig();

        await BuildService(policy: null).StartAsync(_worktree, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Proxy, Written("install.out"));
        Assert.Equal(Proxy, Written("web.out"));
    }

    [Fact]
    public void An_Off_step_under_uid_isolation_runs_under_the_open_group_and_a_proxied_one_under_the_primary_group()
    {
        var service = BuildService(policy: null, agentUser: "agent", agentGroup: "agent-primary", openGroup: "ild-net-open");
        var step = new WorktreePreviewService.ResolvedStep("true", _worktree, new Dictionary<string, string>(StringComparer.Ordinal));

        var off = service.BuildPreviewProcess(step with { NetworkMode = NetworkMode.Off });
        var monitor = service.BuildPreviewProcess(step with { NetworkMode = NetworkMode.Monitor });

        Assert.Contains("--regid=ild-net-open", off.ArgumentList);
        Assert.DoesNotContain("--regid=agent-primary", off.ArgumentList);
        Assert.False(off.Environment.ContainsKey("HTTPS_PROXY"));
        Assert.Contains("--regid=agent-primary", monitor.ArgumentList);
        Assert.Equal(Proxy, monitor.Environment["HTTPS_PROXY"]);
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
