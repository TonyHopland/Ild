using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Data.DTOs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Tests;

public class WorktreePreviewServiceTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    [Fact]
    public async Task StartAsync_starts_and_stops_preview_from_ild_config()
    {
        var worktree = CreateTempWorktree();
        await File.WriteAllTextAsync(Path.Combine(worktree, "ild.config.json"), JsonSerializer.Serialize(new
        {
            preview = new
            {
                defaultProfile = "web",
                profiles = new
                {
                    web = new
                    {
                        services = new[]
                        {
                            new
                            {
                                name = "app",
                                cwd = ".",
                                command = "node -e \"require('http').createServer((_,res)=>res.end('ok')).listen(${PORT}, '127.0.0.1')\"",
                                port = "frontend",
                                suggestedPort = 3100,
                                healthUrl = "http://127.0.0.1:${PORT}/",
                                @public = true
                            }
                        }
                    }
                }
            }
        }));

        var svc = CreateService();

        var started = await svc.StartAsync(worktree);
        Assert.True(started.Configured);
        Assert.Equal("running", started.State);
        Assert.Single(started.Services);
        Assert.False(string.IsNullOrEmpty(started.Services[0].PublicUrl));
        Assert.Equal(3100, started.Services[0].SuggestedPort);
        Assert.Equal(600, started.TimeoutSeconds);
        Assert.NotNull(started.AutoStopAt);

        var stopped = await svc.StopAsync(worktree);
        Assert.Equal("stopped", stopped.State);
        Assert.Equal(3100, stopped.Services[0].SuggestedPort);
        Assert.Equal(600, stopped.TimeoutSeconds);
        Assert.Null(stopped.AutoStopAt);
    }

    [Fact]
    public async Task StartAsync_honors_port_override_when_requested()
    {
        var worktree = CreateTempWorktree();
        var requestedPort = GetFreePort();

        await File.WriteAllTextAsync(Path.Combine(worktree, "ild.config.json"), JsonSerializer.Serialize(new
        {
            preview = new
            {
                defaultProfile = "web",
                profiles = new
                {
                    web = new
                    {
                        services = new[]
                        {
                            new
                            {
                                name = "app",
                                cwd = ".",
                                command = "node -e \"require('http').createServer((_,res)=>res.end('ok')).listen(${PORT}, '127.0.0.1')\"",
                                port = "frontend",
                                suggestedPort = 3100,
                                healthUrl = "http://127.0.0.1:${PORT}/",
                                @public = true
                            }
                        }
                    }
                }
            }
        }));

        var svc = CreateService();

        var started = await svc.StartAsync(
            worktree,
            new ILD.Core.Services.Interfaces.WorktreePreviewStartOptions(
                PortOverrides: new Dictionary<string, int> { ["frontend"] = requestedPort },
                TimeoutSeconds: 120));
        Assert.Single(started.Services);
        Assert.Equal(requestedPort, started.Services[0].Port);
        Assert.Equal(120, started.TimeoutSeconds);

        await svc.StopAsync(worktree);
    }

    [Fact]
    public async Task StartAsync_auto_stops_after_timeout()
    {
        var worktree = CreateTempWorktree();
        await File.WriteAllTextAsync(Path.Combine(worktree, "ild.config.json"), JsonSerializer.Serialize(new
        {
            preview = new
            {
                defaultProfile = "web",
                profiles = new
                {
                    web = new
                    {
                        services = new[]
                        {
                            new
                            {
                                name = "app",
                                cwd = ".",
                                command = "node -e \"require('http').createServer((_,res)=>res.end('ok')).listen(${PORT}, '127.0.0.1')\"",
                                port = "frontend",
                                suggestedPort = 3100,
                                healthUrl = "http://127.0.0.1:${PORT}/",
                                @public = true
                            }
                        }
                    }
                }
            }
        }));

        var svc = CreateService();

        var started = await svc.StartAsync(
            worktree,
            new ILD.Core.Services.Interfaces.WorktreePreviewStartOptions(TimeoutSeconds: 1));

        Assert.Equal("running", started.State);

        await Task.Delay(TimeSpan.FromSeconds(2));

        var status = await svc.GetStatusAsync(worktree);
        Assert.Equal("stopped", status.State);
        Assert.Equal(600, status.TimeoutSeconds);
    }

    [Fact]
    public async Task StartAsync_keeps_running_when_timeout_is_zero()
    {
        var worktree = CreateTempWorktree();
        await File.WriteAllTextAsync(Path.Combine(worktree, "ild.config.json"), JsonSerializer.Serialize(new
        {
            preview = new
            {
                defaultProfile = "web",
                profiles = new
                {
                    web = new
                    {
                        services = new[]
                        {
                            new
                            {
                                name = "app",
                                cwd = ".",
                                command = "node -e \"require('http').createServer((_,res)=>res.end('ok')).listen(${PORT}, '127.0.0.1')\"",
                                port = "frontend",
                                suggestedPort = 3100,
                                healthUrl = "http://127.0.0.1:${PORT}/",
                                @public = true
                            }
                        }
                    }
                }
            }
        }));

        var svc = CreateService();

        var started = await svc.StartAsync(
            worktree,
            new ILD.Core.Services.Interfaces.WorktreePreviewStartOptions(TimeoutSeconds: 0));

        Assert.Equal(0, started.TimeoutSeconds);
        Assert.Null(started.AutoStopAt);

        await Task.Delay(TimeSpan.FromSeconds(2));

        var status = await svc.GetStatusAsync(worktree);
        Assert.Equal("running", status.State);
        Assert.Equal(0, status.TimeoutSeconds);

        await svc.StopAsync(worktree);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private WorktreePreviewService CreateService()
    {
        var config = new ConfigurationBuilder().Build();
        return new WorktreePreviewService(new TestHttpClientFactory(), config, NullLogger<WorktreePreviewService>.Instance);
    }

    private string CreateTempWorktree()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ild-preview-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string? name = null) => new();
    }
}