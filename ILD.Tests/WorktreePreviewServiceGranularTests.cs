using System.Text.Json;
using ILD.Core.Services.Implementations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Exercises the granular preview surface the Preview tab's per-row Start/Stop and
/// Config buttons drive: <see cref="WorktreePreviewService.StartServiceAsync"/>,
/// <see cref="WorktreePreviewService.StopServiceAsync"/>,
/// <see cref="WorktreePreviewService.GetServiceConfigAsync"/> and
/// <see cref="WorktreePreviewService.UpdateServiceConfigAsync"/>.
/// </summary>
[Collection("EnvironmentPath")]
public class WorktreePreviewServiceGranularTests : IDisposable
{
    private readonly string _worktree;
    private WorktreePreviewService? _service;

    public WorktreePreviewServiceGranularTests()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "ild-granular-tests-" + Guid.NewGuid().ToString("N"));
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
        // A real HttpClient so the health probe genuinely succeeds against the node
        // one-liner each service runs.
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
        var configuration = new ConfigurationBuilder().Build();
        _service = new WorktreePreviewService(factory.Object, configuration, NullLogger<WorktreePreviewService>.Instance);
        return _service;
    }

    // A profile with two independent services, each a node HTTP server that answers
    // 200 on its own port so per-service start/stop can be observed in isolation.
    private void WriteTwoServiceConfig(int webPort, int apiPort)
    {
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
                    "suggestedPort": {{webPort}},
                    "command": "PORT=${PORT} {{command}}",
                    "healthUrl": "http://127.0.0.1:${PORT}/"
                  },
                  {
                    "name": "api",
                    "port": "api",
                    "suggestedPort": {{apiPort}},
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
    public async Task StartServiceAsync_starts_only_the_requested_service()
    {
        WriteTwoServiceConfig(FindFreePort(), FindFreePort());
        var service = BuildService();

        var response = await service.StartServiceAsync(_worktree, "web");

        // Both services are listed (so the tab can start the other one too), but only
        // the requested one is running — the overall state reflects that mix.
        Assert.Equal("partial", response.State);
        Assert.Equal("running", response.Services.Single(s => s.Name == "web").Status);
        Assert.Equal("stopped", response.Services.Single(s => s.Name == "api").Status);
    }

    [Fact]
    public async Task StopServiceAsync_stops_only_the_requested_service_and_leaves_the_others_running()
    {
        WriteTwoServiceConfig(FindFreePort(), FindFreePort());
        var service = BuildService();

        await service.StartServiceAsync(_worktree, "web");
        await service.StartServiceAsync(_worktree, "api");

        var afterStop = await service.StopServiceAsync(_worktree, "web");

        Assert.Equal("stopped", afterStop.Services.Single(s => s.Name == "web").Status);
        Assert.Equal("running", afterStop.Services.Single(s => s.Name == "api").Status);

        // The runtime survives because a service is still up; stopping the last one
        // tears it down and the whole preview reports stopped.
        var afterStopAll = await service.StopServiceAsync(_worktree, "api");
        Assert.Equal("stopped", afterStopAll.State);
        Assert.All(afterStopAll.Services, s => Assert.Equal("stopped", s.Status));
    }

    [Fact]
    public async Task StartServiceAsync_throws_for_an_unknown_service()
    {
        WriteTwoServiceConfig(FindFreePort(), FindFreePort());
        var service = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartServiceAsync(_worktree, "does-not-exist"));
        Assert.Contains("does-not-exist", ex.Message);
    }

    [Fact]
    public async Task GetServiceConfigAsync_returns_the_services_raw_entry()
    {
        WriteTwoServiceConfig(4101, 4102);
        var service = BuildService();

        var json = await service.GetServiceConfigAsync(_worktree, "api");

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.Equal("api", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(4102, doc.RootElement.GetProperty("suggestedPort").GetInt32());
    }

    [Fact]
    public async Task UpdateServiceConfigAsync_persists_the_edit_without_touching_other_services()
    {
        WriteTwoServiceConfig(4201, 4202);
        var service = BuildService();

        var edited = """
        {
          "name": "web",
          "port": "web",
          "suggestedPort": 4999,
          "command": "PORT=${PORT} node -e \"process.exit(0)\"",
          "healthUrl": "http://127.0.0.1:${PORT}/"
        }
        """;
        await service.UpdateServiceConfigAsync(_worktree, "web", edited);

        var webJson = await service.GetServiceConfigAsync(_worktree, "web");
        using var web = JsonDocument.Parse(webJson!);
        Assert.Equal(4999, web.RootElement.GetProperty("suggestedPort").GetInt32());

        // The sibling service's entry is left exactly as it was.
        var apiJson = await service.GetServiceConfigAsync(_worktree, "api");
        using var api = JsonDocument.Parse(apiJson!);
        Assert.Equal(4202, api.RootElement.GetProperty("suggestedPort").GetInt32());
    }

    [Fact]
    public async Task UpdateServiceConfigAsync_rejects_invalid_json()
    {
        WriteTwoServiceConfig(4301, 4302);
        var service = BuildService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateServiceConfigAsync(_worktree, "web", "{ not valid json "));
    }

    [Fact]
    public async Task UpdateServiceConfigAsync_rejects_a_name_that_does_not_match_the_target()
    {
        WriteTwoServiceConfig(4401, 4402);
        var service = BuildService();

        var renamed = """
        {
          "name": "renamed",
          "port": "web",
          "suggestedPort": 4401,
          "command": "echo hi",
          "healthUrl": "http://127.0.0.1:${PORT}/"
        }
        """;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateServiceConfigAsync(_worktree, "web", renamed));
        Assert.Contains("must match", ex.Message);
    }

    [Fact]
    public async Task UpdateServiceConfigAsync_rejects_a_config_that_fails_validation()
    {
        WriteTwoServiceConfig(4501, 4502);
        var service = BuildService();

        // Missing healthUrl — the same rule the preview-start path enforces.
        var invalid = """
        {
          "name": "web",
          "port": "web",
          "suggestedPort": 4501,
          "command": "echo hi"
        }
        """;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateServiceConfigAsync(_worktree, "web", invalid));
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
