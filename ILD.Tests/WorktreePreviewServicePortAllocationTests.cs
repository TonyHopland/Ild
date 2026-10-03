using System.Net;
using System.Net.Sockets;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Pins how a preview's ports are handed out. A port the preview chooses for itself
/// is probed once, when it is chosen: once released, any other socket on the host
/// may be given it, so probing it again can only fail the start over a port that was
/// free when it was picked. A port the caller asks for by name is still refused when
/// something holds it.
/// </summary>
[Collection(PreviewPortsCollection.Name)]
public class WorktreePreviewServicePortAllocationTests : IDisposable
{
    private readonly string _worktree;
    private WorktreePreviewService? _service;

    public WorktreePreviewServicePortAllocationTests()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "ild-port-allocation-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_worktree);
    }

    public void Dispose()
    {
        try { _service?.StopAsync(_worktree).GetAwaiter().GetResult(); } catch { /* best effort */ }
        try { _service?.Dispose(); } catch { /* best effort */ }
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private WorktreePreviewService BuildService(Func<int, bool>? isPortAvailable = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());

        _service = new WorktreePreviewService(
            factory.Object,
            new ConfigurationBuilder().Build(),
            PreviewProxyBase.Parse(null),
            NullLogger<WorktreePreviewService>.Instance,
            agentUser: null,
            agentGroup: null,
            agentHome: null,
            isPortAvailable: isPortAvailable);
        return _service;
    }

    private void WriteConfig(params (string Name, string Alias, int SuggestedPort)[] services)
    {
        var command = "node -e \\\"require('http').createServer((q,r)=>{r.end('ok')}).listen(process.env.PORT)\\\"";
        var entries = services.Select(s => $$"""
            {
              "name": "{{s.Name}}",
              "port": "{{s.Alias}}",
              "suggestedPort": {{s.SuggestedPort}},
              "command": "PORT=${PORT} {{command}}",
              "healthUrl": "http://127.0.0.1:${PORT}/"
            }
            """);
        var config = $$"""
        {
          "preview": {
            "defaultProfile": "app",
            "profiles": {
              "app": {
                "services": [{{string.Join(",", entries)}}]
              }
            }
          }
        }
        """;
        File.WriteAllText(Path.Combine(_worktree, "ild.config.json"), config);
    }

    [Fact]
    public async Task A_suggested_port_taken_by_another_socket_after_it_was_chosen_does_not_fail_the_start()
    {
        var suggested = FindFreePort();
        WriteConfig(("app", "frontend", suggested));
        // Free the first time it is probed, held by someone else from then on — what a
        // busy host does to a port between two probes.
        var probes = 0;
        var service = BuildService(port => port != suggested || ++probes == 1);

        var response = await service.StartAsync(_worktree, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(suggested, response.Services.Single(s => s.Name == "app").Port);
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task A_requested_port_that_is_in_use_fails_the_start_and_names_the_port()
    {
        WriteConfig(("app", "frontend", FindFreePort()));
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        var held = ((IPEndPoint)holder.LocalEndpoint).Port;
        var service = BuildService();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(
            _worktree,
            new WorktreePreviewStartOptions(PortOverrides: new Dictionary<string, int> { ["frontend"] = held }),
            TestContext.Current.CancellationToken));

        Assert.Equal($"Preview port '{held}' for alias 'frontend' is already in use.", error.Message);
    }

    [Fact]
    public async Task A_requested_port_that_is_in_use_fails_a_service_added_to_a_running_preview()
    {
        var appPort = FindFreePort();
        WriteConfig(("app", "frontend", appPort));
        var service = BuildService();
        await service.StartAsync(_worktree, cancellationToken: TestContext.Current.CancellationToken);
        WriteConfig(("app", "frontend", appPort), ("docs", "docs", FindFreePort()));
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        var held = ((IPEndPoint)holder.LocalEndpoint).Port;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartServiceAsync(
            _worktree,
            "docs",
            new WorktreePreviewStartOptions(PortOverrides: new Dictionary<string, int> { ["docs"] = held }),
            TestContext.Current.CancellationToken));

        Assert.Equal($"Preview port '{held}' for alias 'docs' is already in use.", error.Message);
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
