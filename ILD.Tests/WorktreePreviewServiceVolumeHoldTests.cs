using System.Net;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A preview that never got a service running holds nothing: a single-service start
/// whose launch fails takes down the runtime it just created, so its keep-on hold
/// does not outlive it behind a "stopped" state that offers no stop.
/// </summary>
public sealed class WorktreePreviewServiceVolumeHoldTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ild-volume-hold-").FullName;
    private readonly Guid _repository = Guid.NewGuid();
    private readonly WorktreePreviewService _service;

    public WorktreePreviewServiceVolumeHoldTests()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new Healthy()));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:WorktreesPath"] = Path.Combine(_root, "worktrees") })
            .Build();
        _service = new WorktreePreviewService(factory.Object, configuration, PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance, agentUser: null, agentGroup: null, agentHome: null,
            environment: new TestProcessEnvironment { { "HOME", Path.Combine(_root, "home") } }.WithRealPath());
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task A_single_service_start_whose_launch_fails_leaves_no_preview_holding_the_volumes()
    {
        var ct = TestContext.Current.CancellationToken;
        // A cwd outside the worktree is refused while the service is being launched.
        var broken = Worktree("wt-a", cwd: "/");
        var other = Worktree("wt-b", cwd: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.StartServiceAsync(broken, "web", new WorktreePreviewStartOptions(Volumes: Scope("305")), ct));

        Assert.False(_service.IsPreviewRunning(broken));
        await _service.StartAsync(other, new WorktreePreviewStartOptions(Volumes: Scope("306")), ct);
        Assert.True(_service.IsPreviewRunning(other));
    }

    private PreviewVolumeScope Scope(string workItemId) => new(_repository, true, workItemId, "Title");

    private string Worktree(string name, string? cwd)
    {
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "worktrees", name)).FullName;
        var cwdField = cwd is null ? "" : $"\"cwd\": \"{cwd}\",";
        File.WriteAllText(Path.Combine(worktree, "ild.config.json"), $$"""
            {
              "preview": {
                "profiles": {
                  "app": {
                    "volumes": ["data"],
                    "services": [
                      { "name": "web", "port": "web", {{cwdField}} "command": "exec sleep 60",
                        "env": { "DATA": "${VOLUME:data}" }, "healthUrl": "http://127.0.0.1:${PORT}/" }
                    ]
                  }
                }
              }
            }
            """);
        return worktree;
    }

    private sealed class Healthy : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
