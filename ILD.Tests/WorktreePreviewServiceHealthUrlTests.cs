using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Local;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A service's health URL names its own machine's loopback. When the preview runs in a run's container or pod, ILD
/// checks it where the preview proxy reaches it, at the address the run host gives, not at its own loopback.
/// </summary>
public sealed class WorktreePreviewServiceHealthUrlTests
{
    private const string Worktree = "/ild-run/0123456789abcdef0123456789abcdef/repo";

    private static IRunHost HostAnswering(string target)
    {
        var host = new Mock<IRunHost>();
        host.Setup(h => h.PreviewTargetAsync(Worktree, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, int port, CancellationToken _) => new Uri($"http://{target}:{port}"));
        return host.Object;
    }

    [Theory]
    [InlineData("http://127.0.0.1:3000/health")]
    [InlineData("http://localhost:3000/health")]
    [InlineData("http://0.0.0.0:3000/health")]
    public async Task A_loopback_health_url_is_checked_at_the_runs_address(string healthUrl)
    {
        var probed = await WorktreePreviewService.ReachableHealthUrlAsync(HostAnswering("10.89.3.2"), healthUrl, Worktree, CancellationToken.None);

        Assert.Equal("http://10.89.3.2:3000/health", probed);
    }

    [Fact]
    public async Task On_ilds_own_machine_the_url_is_checked_as_written()
    {
        var probed = await WorktreePreviewService.ReachableHealthUrlAsync(HostAnswering("127.0.0.1"), "http://127.0.0.1:3000/health?deep=1", Worktree, CancellationToken.None);

        Assert.Equal("http://127.0.0.1:3000/health?deep=1", probed);
    }

    [Fact]
    public async Task A_health_url_on_another_host_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorktreePreviewService.ReachableHealthUrlAsync(HostAnswering("10.89.3.2"), "https://status.example.test/ping", Worktree, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("https://status.example.test/ping")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://10.0.0.5:${PORT}/")]
    [InlineData("http://${PUBLIC_HOST}:${PORT}/")]
    [InlineData("http://127.0.0.1@evil.test/")]
    [InlineData("ftp://127.0.0.1/")]
    public async Task A_health_url_that_is_not_the_services_own_loopback_is_refused_at_validation(string healthUrl)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ValidateAsync(healthUrl));

        Assert.Contains("healthUrl must name the service's own loopback", error.Message);
    }

    [Theory]
    [InlineData("http://127.0.0.1:${PORT}/health")]
    [InlineData("http://localhost:${PORT:web}/health")]
    [InlineData("http://${HOST}:${PORT}/")]
    [InlineData("http://[::1]:${PORT}/")]
    [InlineData("https://0.0.0.0:3000/")]
    public async Task A_loopback_health_url_passes_validation(string healthUrl)
    {
        var result = await ValidateAsync(healthUrl);

        Assert.True(result.Configured);
    }

    [Fact]
    public async Task The_health_probe_does_not_follow_a_redirect_to_another_host()
    {
        var other = new HttpListener();
        var otherPort = FreePort();
        other.Prefixes.Add($"http://127.0.0.1:{otherPort}/");
        other.Start();
        var hits = 0;
        _ = Task.Run(async () =>
        {
            while (other.IsListening)
            {
                try
                {
                    var context = await other.GetContextAsync();
                    Interlocked.Increment(ref hits);
                    context.Response.Close();
                }
                catch { return; }
            }
        }, TestContext.Current.CancellationToken);

        var redirecting = new HttpListener();
        var redirectingPort = FreePort();
        redirecting.Prefixes.Add($"http://127.0.0.1:{redirectingPort}/");
        redirecting.Start();
        _ = Task.Run(async () =>
        {
            while (redirecting.IsListening)
            {
                try
                {
                    var context = await redirecting.GetContextAsync();
                    context.Response.StatusCode = 302;
                    context.Response.RedirectLocation = $"http://127.0.0.1:{otherPort}/";
                    context.Response.Close();
                }
                catch { return; }
            }
        }, TestContext.Current.CancellationToken);

        try
        {
            var services = new ServiceCollection();
            WorktreePreviewService.ConfigureHealthProbeClient(services.AddHttpClient(WorktreePreviewService.HealthProbeClientName));
            using var provider = services.BuildServiceProvider();
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(WorktreePreviewService.HealthProbeClientName);

            using var response = await client.GetAsync($"http://127.0.0.1:{redirectingPort}/", TestContext.Current.CancellationToken);

            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal(0, Volatile.Read(ref hits));
        }
        finally
        {
            redirecting.Close();
            other.Close();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<WorktreePreviewValidationResult> ValidateAsync(string healthUrl)
    {
        var worktree = Path.Combine(Path.GetTempPath(), "ild-health-url-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        try
        {
            var config = new JsonObject
            {
                ["preview"] = new JsonObject
                {
                    ["profiles"] = new JsonObject
                    {
                        ["app"] = new JsonObject
                        {
                            ["services"] = new JsonArray(new JsonObject
                            {
                                ["name"] = "web", ["command"] = "sleep 60", ["port"] = "web", ["healthUrl"] = healthUrl,
                            }),
                        },
                    },
                },
            };
            File.WriteAllText(Path.Combine(worktree, "ild.config.json"), config.ToJsonString());
            var service = new WorktreePreviewService(new Mock<IHttpClientFactory>().Object, new ConfigurationBuilder().Build(),
                PreviewProxyBase.Disabled, NullLogger<WorktreePreviewService>.Instance, new PreviewDataClaims(), LocalRunHost.Shared);
            return await service.ValidateConfigAsync(worktree, cancellationToken: CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(worktree, recursive: true); } catch { /* best effort */ }
        }
    }
}
