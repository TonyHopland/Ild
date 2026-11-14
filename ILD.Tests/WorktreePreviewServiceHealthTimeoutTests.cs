using ILD.Core.Services.RunHosts.Local;
using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// How long a preview service has to become healthy: a slow start inside its limit succeeds, a service that
/// exits fails at once with its exit code, and one that never answers fails with the timeout. The health
/// timeout's second is shortened so the tests do not wait.
/// </summary>
[Collection(PreviewPortsCollection.Name)]
[SupportedOSPlatform("linux")]
public class WorktreePreviewServiceHealthTimeoutTests : IDisposable
{
    private static readonly TimeSpan Unit = TimeSpan.FromMilliseconds(20);

    private readonly string _root;
    private readonly List<WorktreePreviewService> _services = [];

    public WorktreePreviewServiceHealthTimeoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ild-health-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            try { service.Dispose(); } catch { /* best effort */ }
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class SlowHealth(TimeSpan healthyAfter) : HttpMessageHandler
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_clock.Elapsed >= healthyAfter ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));
    }

    private WorktreePreviewService BuildService(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        var environment = new TestProcessEnvironment { { "HOME", Path.Combine(_root, "home") } }.WithRealPath();
        var service = new WorktreePreviewService(factory.Object, new ConfigurationBuilder().Build(), PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance, environment: environment, host: LocalRunHost.Shared)
        {
            HealthTimeoutUnit = Unit,
            HealthPollDelay = TimeSpan.FromMilliseconds(2),
        };
        _services.Add(service);
        return service;
    }

    private string Worktree(string name, string command, JsonNode? healthTimeoutSeconds)
    {
        var worktree = Path.Combine(_root, name);
        Directory.CreateDirectory(worktree);
        var config = new JsonObject
        {
            ["preview"] = new JsonObject
            {
                ["defaultProfile"] = "app",
                ["profiles"] = new JsonObject
                {
                    ["app"] = new JsonObject
                    {
                        ["services"] = new JsonArray(new JsonObject
                        {
                            ["name"] = "web", ["port"] = "web", ["command"] = command,
                            ["healthUrl"] = "http://127.0.0.1:${PORT}/", ["healthTimeoutSeconds"] = healthTimeoutSeconds,
                        }),
                    },
                },
            },
        };
        File.WriteAllText(Path.Combine(worktree, "ild.config.json"), config.ToJsonString());
        return worktree;
    }

    [Fact]
    public async Task A_service_that_is_healthy_after_the_old_sixty_second_limit_but_within_its_own_starts()
    {
        // 100 "seconds" of 20 ms each: past the former fixed 60, inside the 150 configured.
        var service = BuildService(new SlowHealth(Unit * 100));

        var response = await service.StartAsync(Worktree("slow", "sleep 600", JsonValue.Create(150)), cancellationToken: Ct);

        Assert.Equal("running", response.State);
        await service.StopAsync(Path.Combine(_root, "slow"), Ct);
    }

    [Fact]
    public async Task A_service_that_never_becomes_healthy_fails_with_the_timeout_after_its_limit()
    {
        var service = BuildService(new SlowHealth(TimeSpan.FromDays(1)));
        var clock = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(Worktree("never", "sleep 600", JsonValue.Create(10)), cancellationToken: Ct));

        Assert.Contains("did not become healthy at", error.Message);
        Assert.Contains("within 10s", error.Message);
        Assert.DoesNotContain("exited with code", error.Message);
        Assert.True(clock.Elapsed >= Unit * 10);
    }

    [Fact]
    public async Task A_service_that_exits_fails_at_once_with_its_exit_code()
    {
        var service = BuildService(new SlowHealth(TimeSpan.FromDays(1)));
        service.HealthTimeoutUnit = TimeSpan.FromSeconds(1);
        var clock = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(Worktree("exits", "exit 3", JsonValue.Create(300)), cancellationToken: Ct));

        Assert.Contains("exited with code 3", error.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task A_service_started_on_its_own_that_exits_fails_at_once_with_its_exit_code()
    {
        var service = BuildService(new SlowHealth(TimeSpan.FromDays(1)));
        var worktree = Worktree("own", "exit 4", JsonValue.Create(300));
        service.HealthTimeoutUnit = TimeSpan.FromSeconds(1);
        var clock = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartServiceAsync(worktree, "web", cancellationToken: Ct));

        Assert.Contains("exited with code 4", error.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task A_service_started_on_its_own_that_never_becomes_healthy_times_out()
    {
        var service = BuildService(new SlowHealth(TimeSpan.FromDays(1)));
        var worktree = Worktree("own-never", "sleep 600", JsonValue.Create(5));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartServiceAsync(worktree, "web", cancellationToken: Ct));

        Assert.Contains("within 5s", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(3601)]
    public async Task An_invalid_health_timeout_is_refused(int seconds)
    {
        var service = BuildService(new SlowHealth(TimeSpan.Zero));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(Worktree("invalid" + seconds, "sleep 600", JsonValue.Create(seconds)), cancellationToken: Ct));

        Assert.Contains("healthTimeoutSeconds", error.Message);
    }

    [Fact]
    public void The_default_health_timeout_is_longer_than_the_old_sixty_seconds()
        => Assert.True(WorktreePreviewService.DefaultHealthTimeoutSeconds > 60);
}
