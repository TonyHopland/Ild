using ILD.Core.Services.RunHosts.Local;
using System.Net;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// An install holds the repository's preview data against every other work item while it runs, and is refused
/// at once, without waiting for a start in progress, when another work item holds it.
/// </summary>
public sealed class WorktreePreviewServiceVolumeHoldTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ild-volume-hold-").FullName;
    private readonly Guid _repository = Guid.NewGuid();
    private readonly WorktreePreviewService _service;
    private readonly List<string> _fifos = [];

    public WorktreePreviewServiceVolumeHoldTests()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new Healthy()));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:WorktreesPath"] = Path.Combine(_root, "worktrees") })
            .Build();
        _service = new WorktreePreviewService(factory.Object, configuration, PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance,
            environment: new TestProcessEnvironment { { "HOME", Path.Combine(_root, "home") } }.WithRealPath(), host: LocalRunHost.Shared);
    }

    public void Dispose()
    {
        // Lets an install step still blocked on a pipe finish, so a failed test does not hang its teardown.
        foreach (var fifo in _fifos)
        {
            try { using var _ = new FileStream(fifo, FileMode.Open, FileAccess.ReadWrite); } catch { /* best effort */ }
        }
        _service.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact(Timeout = 120_000)]
    public async Task An_install_another_work_item_holds_against_is_refused_without_waiting_for_a_start_in_progress()
    {
        var ct = TestContext.Current.CancellationToken;
        var (started, go) = (Fifo("started"), Fifo("go"));
        var holder = Worktree("wt-a", cwd: null, install: $"printf x > '{started}'; cat '{go}' > /dev/null");
        var other = Worktree("wt-b", cwd: null);
        var start = _service.StartAsync(holder, new WorktreePreviewStartOptions(Volumes: Scope("305")), ct);
        await Task.Run(() => File.ReadAllText(started), ct);

        // The start holds the gate in its install step and is only let go once the install has answered.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.InstallAsync(other, volumes: Scope("306"), cancellationToken: ct));

        Assert.StartsWith("A preview is already running for work item 305", error.Message);
        await Task.Run(() => File.WriteAllText(go, "go"), ct);
        await start;
    }

    private string Fifo(string name)
    {
        var path = Path.Combine(_root, "fifo-" + name);
        using var mkfifo = System.Diagnostics.Process.Start("mkfifo", path);
        mkfifo.WaitForExit();
        Assert.Equal(0, mkfifo.ExitCode);
        _fifos.Add(path);
        return path;
    }

    private PreviewVolumeScope Scope(string workItemId) => new(_repository, true, workItemId, "Title");

    private string Worktree(string name, string? cwd, string? install = null)
    {
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "worktrees", name)).FullName;
        var cwdField = cwd is null ? "" : $"\"cwd\": \"{cwd}\",";
        var installField = install is null ? "" : $"\"install\": [{{ \"command\": \"{install}\" }}],";
        File.WriteAllText(Path.Combine(worktree, "ild.config.json"), $$"""
            {
              "preview": {
                "profiles": {
                  "app": {
                    "volumes": ["data"],
                    {{installField}}
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
