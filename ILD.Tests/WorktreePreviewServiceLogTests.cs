using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Exercises <see cref="WorktreePreviewService.GetServiceLogAsync"/> — the path the
/// Preview tab's per-service Log column uses to surface what a service printed,
/// especially the failure output of a service that exited.
/// </summary>
[Collection("EnvironmentPath")]
public class WorktreePreviewServiceLogTests : IDisposable
{
    private readonly string _worktree;
    private WorktreePreviewService? _service;

    public WorktreePreviewServiceLogTests()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "ild-log-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_worktree);
    }

    public void Dispose()
    {
        try { _service?.StopAsync(_worktree).GetAwaiter().GetResult(); } catch { /* best effort */ }
        try { _service?.Dispose(); } catch { /* best effort */ }
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private WorktreePreviewService BuildService()
    {
        // The health probe must really succeed, so back the factory with a live
        // HttpClient rather than a mock.
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
        var configuration = new ConfigurationBuilder().Build();
        _service = new WorktreePreviewService(factory.Object, configuration, PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance);
        return _service;
    }

    private void WriteConfig(int suggestedPort)
    {
        // A node one-liner that answers 200 on the health URL keeps the service
        // healthy so StartAsync stores the runtime and writes the service's log.
        var command = "node -e \\\"require('http').createServer((q,r)=>{r.end('ok')}).listen(process.env.PORT)\\\"";
        var config = $$"""
        {
          "preview": {
            "defaultProfile": "app",
            "profiles": {
              "app": {
                "services": [
                  {
                    "name": "web",
                    "port": "web",
                    "suggestedPort": {{suggestedPort}},
                    "command": "PORT=${PORT} {{command}}",
                    "healthUrl": "http://127.0.0.1:${PORT}/"
                  }
                ]
              }
            }
          }
        }
        """;
        File.WriteAllText(Path.Combine(_worktree, "ild.config.json"), config);
    }

    [Fact]
    public async Task GetServiceLogAsync_returns_the_started_services_captured_output()
    {
        WriteConfig(FindFreePort());
        var service = BuildService();

        var started = await service.StartAsync(_worktree, cancellationToken: CancellationToken.None);
        Assert.Equal("running", started.State);

        var log = await service.GetServiceLogAsync(_worktree, "web");

        // StartServiceAsync echoes the resolved command into the log before the
        // process runs, so the captured log always contains that command line.
        Assert.NotNull(log);
        Assert.Contains("node -e", log);
    }

    [Fact]
    public async Task GetServiceLogAsync_reads_a_log_that_lives_under_the_shared_scratch_root()
    {
        // The preview's state directory moved out of the orchestrator-private root
        // when its steps moved to the agent uid (ADR-0016). The orchestrator still
        // owns and reads these files — that is what get_preview_logs and the
        // Preview tab's Log column depend on — so assert the new location rather
        // than assuming the move left reading intact.
        WriteConfig(FindFreePort());
        var service = BuildService();

        var started = await service.StartAsync(_worktree, cancellationToken: CancellationToken.None);
        Assert.Equal("running", started.State);
        Assert.StartsWith(
            Path.TrimEndingDirectorySeparator(AgentIsolation.ScratchRoot) + Path.DirectorySeparatorChar,
            started.Services.Single().LogFilePath);

        Assert.NotNull(await service.GetServiceLogAsync(_worktree, "web"));
    }

    [Fact]
    public async Task GetServiceLogAsync_returns_null_when_the_preview_was_never_started()
    {
        // Configured worktree, but nothing started yet — there is no log file on
        // disk, so the reader reports null rather than throwing.
        WriteConfig(FindFreePort());
        var service = BuildService();

        var log = await service.GetServiceLogAsync(_worktree, "web");

        Assert.Null(log);
    }

    [Fact]
    public async Task GetServiceLogAsync_returns_null_for_a_name_that_escapes_the_state_directory()
    {
        // A service name carrying a path separator must not be allowed to read an
        // arbitrary file outside the preview state directory.
        var service = BuildService();

        var log = await service.GetServiceLogAsync(_worktree, "../secret");

        Assert.Null(log);
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
