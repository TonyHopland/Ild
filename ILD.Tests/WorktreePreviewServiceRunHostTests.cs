using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Isolated;
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

        var stop = service.StopAsync(Worktree, ct);
        var finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(10), ct));
        Assert.Same(stop, finished);
        var stopped = await stop;

        Assert.False(service.IsPreviewRunning(Worktree));
        Assert.All(stopped.Services, s => Assert.Equal("stopped", s.Status));
    }

    // -- When runs are isolated --------------------------------------------------

    private static readonly Guid RunId = Guid.Parse("0a2b6c3d-1111-4222-8333-944455566677");
    private static readonly string RunWorktree = RunPaths.WorkingDirectory(RunId);

    private static string InstallConfig(string cwd) => $$"""
        {
          "preview": {
            "defaultProfile": "app",
            "profiles": {
              "app": {
                "volumes": ["data"],
                "install": [ { "command": "npm ci", "cwd": "{{cwd}}", "env": { "DATA": "${VOLUME:data}" } } ],
                "services": []
              }
            }
          }
        }
        """;

    // An isolated host as the container and Kubernetes hosts are: a run's state directory is in the run.
    private static FakeRunHost IsolatedHost(string worktree, string cwd) => new FakeRunHost
    {
        Kind = RunHostKind.Container,
        StateDirectoryFor = path => $"{RunPaths.RunRoot(RunPaths.OwnerOf(path)!.Value)}/preview-state",
        PreviewDataFolder = _ => RunPaths.PreviewData(RunId),
    }.WithDirectory(worktree).WithFile(worktree + "/ild.config.json", InstallConfig(cwd));

    [Fact]
    public async Task What_an_earlier_ild_left_running_in_the_run_is_ended_before_a_start_and_on_a_stop()
    {
        var host = new FakeRunHost
        {
            Kind = RunHostKind.Container,
            StateDirectoryFor = path => $"{RunPaths.RunRoot(RunPaths.OwnerOf(path)!.Value)}/preview-state",
        }.WithDirectory(RunWorktree).WithFile(RunWorktree + "/ild.config.json", Config("sleep 1000"));
        using var service = Build(host);
        var ct = TestContext.Current.CancellationToken;

        await service.StopAsync(RunWorktree, ct);
        Assert.Equal([RunId], host.KillUnownedCalls);

        await service.StartAsync(RunWorktree, cancellationToken: ct);
        Assert.Equal([RunId, RunId], host.KillUnownedCalls);

        // The preview this ILD started is stopped as its own, not as something left behind.
        await service.StopAsync(RunWorktree, ct);
        Assert.Equal([RunId, RunId], host.KillUnownedCalls);
    }

    [Theory]
    [InlineData("${STATE_DIR}/work", false)]
    [InlineData("${VOLUME:data}", false)]
    [InlineData("${VOLUME:data}", true)]
    public async Task A_step_whose_directory_is_its_state_directory_or_a_volume_runs_in_the_run(string cwd, bool keep)
    {
        var host = IsolatedHost(RunWorktree, cwd);
        using var service = Build(host);

        await service.InstallAsync(RunWorktree, volumes: new PreviewVolumeScope(Guid.NewGuid(), keep, "wi-1", "Title"),
            cancellationToken: TestContext.Current.CancellationToken);

        var step = Assert.Single(host.Started);
        Assert.Equal(RunId, step.RunIn);
        var root = RunPaths.RunRoot(RunId) + "/";
        Assert.StartsWith(root, step.Start.WorkingDirectory);
        Assert.Equal(root + "preview-state/npm-cache", step.Environment!["NPM_CONFIG_CACHE"]);
        Assert.Equal(keep ? root + "preview-volumes/data" : root + "preview-state/volumes/data", step.Environment!["DATA"]);
        if (cwd.StartsWith("${STATE_DIR}", StringComparison.Ordinal))
            Assert.Equal(root + "preview-state/work", step.Start.WorkingDirectory);
    }

    [Fact]
    public async Task A_preview_of_a_worktree_left_on_ilds_disk_is_refused_and_starts_nothing()
    {
        const string onIldsDisk = "/data/worktrees/wt-from-before";
        var host = IsolatedHost(onIldsDisk, "${STATE_DIR}");
        using var service = Build(host);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(
            onIldsDisk, cancellationToken: TestContext.Current.CancellationToken));

        Assert.StartsWith("Refusing to preview", error.Message);
        Assert.Empty(host.Started);
    }

    /// <summary>The service inside the fake run answers every health probe.</summary>
    private sealed class AlwaysOk : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
