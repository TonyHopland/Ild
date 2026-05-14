using System.Text.Json;
using FluentAssertions;
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
        started.Configured.Should().BeTrue();
        started.State.Should().Be("running");
        started.Services.Should().ContainSingle();
        started.Services[0].PublicUrl.Should().NotBeNullOrEmpty();
        started.Services[0].SuggestedPort.Should().Be(3100);
        started.TimeoutSeconds.Should().Be(600);
        started.AutoStopAt.Should().NotBeNull();

        var stopped = await svc.StopAsync(worktree);
        stopped.State.Should().Be("stopped");
        stopped.Services[0].SuggestedPort.Should().Be(3100);
        stopped.TimeoutSeconds.Should().Be(600);
        stopped.AutoStopAt.Should().BeNull();
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
        started.Services.Should().ContainSingle();
        started.Services[0].Port.Should().Be(requestedPort);
        started.TimeoutSeconds.Should().Be(120);

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

        started.State.Should().Be("running");

        await Task.Delay(TimeSpan.FromSeconds(2));

        var status = await svc.GetStatusAsync(worktree);
        status.State.Should().Be("stopped");
        status.TimeoutSeconds.Should().Be(600);
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

        started.TimeoutSeconds.Should().Be(0);
        started.AutoStopAt.Should().BeNull();

        await Task.Delay(TimeSpan.FromSeconds(2));

        var status = await svc.GetStatusAsync(worktree);
        status.State.Should().Be("running");
        status.TimeoutSeconds.Should().Be(0);

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