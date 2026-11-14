using ILD.Data.DTOs;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts.Local;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Starting one preview service at a time: a service that references another's port
/// alias is refused by default, starts its dependency first when asked to, and a
/// service dying on its own is pushed to the UI and reads as a failed preview.
/// </summary>
[Collection(PreviewPortsCollection.Name)]
public class WorktreePreviewServiceDependencyStartTests : IDisposable
{
    private const string WorkItemId = "wi-dep";
    private readonly string _worktree;
    private readonly Mock<IWorkItemNotifier> _notifier = new();
    private WorktreePreviewService? _service;

    public WorktreePreviewServiceDependencyStartTests()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "ild-depstart-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_worktree);
        _notifier.Setup(n => n.PreviewStateChangedAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        try { _service?.StopAsync(_worktree).GetAwaiter().GetResult(); } catch { /* best effort */ }
        try { _service?.Dispose(); } catch { /* best effort */ }
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WorktreePreviewService BuildService()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
        _service = new WorktreePreviewService(factory.Object, new ConfigurationBuilder().Build(), PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance, notifier: _notifier.Object, host: LocalRunHost.Shared);
        return _service;
    }

    // api serves and exits once its "die" file exists; web references api's port.
    private void WriteConfig()
    {
        var config = """
        {
          "preview": {
            "defaultProfile": "app",
            "profiles": {
              "app": {
                "services": [
                  {
                    "name": "api",
                    "port": "api",
                    "command": "PORT=${PORT} node -e \"const fs=require('fs');require('http').createServer((q,r)=>{r.end('ok')}).listen(process.env.PORT);setInterval(()=>{if(fs.existsSync(process.env.DIE_FILE))process.exit(3)},50)\"",
                    "healthUrl": "http://127.0.0.1:${PORT}/",
                    "env": { "DIE_FILE": "${WORKTREE}/die" }
                  },
                  {
                    "name": "web",
                    "port": "web",
                    "command": "PORT=${PORT} node -e \"require('http').createServer((q,r)=>{r.end('ok')}).listen(process.env.PORT)\"",
                    "healthUrl": "http://127.0.0.1:${PORT}/",
                    "env": { "API_URL": "http://127.0.0.1:${PORT:api}" }
                  }
                ]
              }
            }
          }
        }
        """;
        File.WriteAllText(Path.Combine(_worktree, "ild.config.json"), config);
    }

    private static string StatusOf(WorktreePreviewResponse response, string service)
        => response.Services.Single(s => s.Name == service).Status;

    [Fact]
    public async Task Starting_a_dependent_service_while_its_dependency_is_down_is_refused_by_default()
    {
        WriteConfig();
        var service = BuildService();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartServiceAsync(_worktree, "web", new WorktreePreviewStartOptions(WorkItemId: WorkItemId), Ct));

        Assert.Contains("Start 'api' first", refusal.Message);
        Assert.False(service.IsPreviewRunning(_worktree));
    }

    [Fact]
    public async Task Starting_a_dependent_service_with_StartDependencies_launches_the_dependency_first()
    {
        WriteConfig();
        var service = BuildService();

        var response = await service.StartServiceAsync(_worktree, "web",
            new WorktreePreviewStartOptions(WorkItemId: WorkItemId, StartDependencies: true), Ct);

        Assert.Equal("running", StatusOf(response, "api"));
        Assert.Equal("running", StatusOf(response, "web"));
        Assert.Equal("running", response.State);
        Assert.Equal("running", service.PreviewState(_worktree));
        Assert.Equal(["api"], response.Services.Single(s => s.Name == "web").DependsOn);
        Assert.Empty(response.Services.Single(s => s.Name == "api").DependsOn);
    }

    [Fact]
    public async Task A_service_that_exits_on_its_own_is_pushed_and_the_preview_reads_failed()
    {
        WriteConfig();
        var service = BuildService();
        await service.StartServiceAsync(_worktree, "web",
            new WorktreePreviewStartOptions(WorkItemId: WorkItemId, StartDependencies: true), Ct);
        Assert.Equal("running", service.PreviewState(_worktree));
        _notifier.Invocations.Clear();

        await File.WriteAllTextAsync(Path.Combine(_worktree, "die"), "x", Ct);

        await WaitForAsync(() => service.PreviewState(_worktree) == "failed");
        await WaitForAsync(() => _notifier.Invocations.Count > 0);
        _notifier.Verify(n => n.PreviewStateChangedAsync(WorkItemId), Times.AtLeastOnce);

        var status = await service.GetStatusAsync(_worktree, Ct);
        Assert.Equal("failed", status.State);
        Assert.Equal("exited", StatusOf(status, "api"));
        Assert.Equal(3, status.Services.Single(s => s.Name == "api").ExitCode);
    }

    [Fact]
    public async Task A_partly_started_preview_reads_partial_and_an_unstarted_one_stopped()
    {
        WriteConfig();
        var service = BuildService();
        Assert.Equal("stopped", service.PreviewState(_worktree));

        await service.StartServiceAsync(_worktree, "api", new WorktreePreviewStartOptions(WorkItemId: WorkItemId), Ct);

        Assert.Equal("partial", service.PreviewState(_worktree));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(50, Ct);
        }
    }
}
