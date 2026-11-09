using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A preview on a container host lives in the run, not on ILD's disk: config edits are written
/// through the host, and stopping a service whose process will not die returns anyway.
/// </summary>
public sealed class WorktreePreviewServiceRunHostTests
{
    private const string Worktree = "/fake-worktrees/x";
    private const string ConfigPath = Worktree + "/ild.config.json";

    private static string Config(string command) => $$"""
        {
          "preview": {
            "defaultProfile": "app",
            "profiles": {
              "app": {
                "services": [
                  { "name": "web", "port": "frontend", "suggestedPort": 45871, "command": "{{command}}", "healthUrl": "http://127.0.0.1:${PORT}/" }
                ]
              }
            }
          }
        }
        """;

    private static WorktreePreviewService Build(FakeRunHost host)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new AlwaysOk()));
        return new WorktreePreviewService(
            factory.Object, new ConfigurationBuilder().Build(), PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance, new PreviewDataClaims(), host: host);
    }

    [Fact]
    public async Task A_config_edit_is_written_through_the_run_host_not_to_local_disk()
    {
        var host = new FakeRunHost().WithDirectory(Worktree).WithFile(ConfigPath, Config("npm start"));
        using var service = Build(host);

        await service.UpdateServiceConfigAsync(Worktree, "web",
            """{ "name": "web", "port": "frontend", "suggestedPort": 45871, "command": "npm run dev", "healthUrl": "http://127.0.0.1:${PORT}/" }""",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("npm run dev", host.ReadText(ConfigPath));
        Assert.False(File.Exists(ConfigPath));
        Assert.False(Directory.Exists(Worktree));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stopping_a_service_that_will_not_die_returns_within_the_bound(bool killFails)
    {
        var host = new FakeRunHost().WithDirectory(Worktree).WithFile(ConfigPath, Config("sleep 1000"));
        host.Script = _ => killFails ? FakeCommandScript.RefusesKill() : FakeCommandScript.SurvivesKill();
        using var service = Build(host);
        service.KillGrace = TimeSpan.FromMilliseconds(200);
        var ct = TestContext.Current.CancellationToken;

        await service.StartAsync(Worktree, cancellationToken: ct);

        var stop = service.StopServiceAsync(Worktree, "web", ct);
        var finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(10), ct));
        Assert.Same(stop, finished);
        await stop;
    }

    /// <summary>The service inside the fake run answers every health probe.</summary>
    private sealed class AlwaysOk : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
