using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// What a preview reports while a full start is still running: the install step, each
/// service's own stage, a dead service failing fast, a start that queues behind
/// another, and the background (wait=false) start. Progress is observed through
/// <c>GetStatusAsync</c> and the <c>PreviewStateChanged</c> pushes, and every
/// interleaving is scripted with named pipes and gated health probes.
/// </summary>
[Collection(PreviewPortsCollection.Name)]
[SupportedOSPlatform("linux")]
public class WorktreePreviewServiceStartProgressTests : IDisposable
{
    private const string WorkItemId = "wi-7";

    private readonly string _root;
    private readonly List<WorktreePreviewService> _services = [];
    private readonly List<string> _fifosToRelease = [];
    private readonly List<GatedHealth> _handlers = [];
    private readonly SemaphoreSlim _changes = new(0);
    private readonly Mock<IWorkItemNotifier> _notifier = new();

    public WorktreePreviewServiceStartProgressTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ild-start-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _notifier.Setup(n => n.PreviewStateChangedAsync(It.IsAny<string>()))
            .Returns(() =>
            {
                _changes.Release();
                return Task.CompletedTask;
            });
    }

    public void Dispose()
    {
        foreach (var handler in _handlers)
            handler.ReleaseAll();
        foreach (var fifo in _fifosToRelease)
            Unblock(fifo);
        foreach (var service in _services)
        {
            try { service.Dispose(); } catch { /* best effort */ }
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WorktreePreviewService BuildService(GatedHealth? health = null)
    {
        var handler = health ?? new GatedHealth();
        _handlers.Add(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        var environment = new TestProcessEnvironment { { "HOME", Path.Combine(_root, "home") } }.WithRealPath();
        var service = new WorktreePreviewService(factory.Object, new ConfigurationBuilder().Build(), PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance,
            agentUser: null, agentGroup: null, agentHome: null,
            environment: environment, notifier: _notifier.Object);
        _services.Add(service);
        return service;
    }

    private sealed record Svc(string Name, string Command, string HealthPath = "/ok");

    private static readonly JsonSerializerOptions ConfigJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private string Worktree(string name, IReadOnlyList<string> install, IReadOnlyList<Svc> services)
    {
        var worktree = Path.Combine(_root, name);
        Directory.CreateDirectory(worktree);
        var config = new
        {
            preview = new
            {
                defaultProfile = "app",
                profiles = new Dictionary<string, object>
                {
                    ["app"] = new
                    {
                        install = install.Select(command => new { command }).ToList(),
                        services = services.Select(s => new
                        {
                            s.Name,
                            Port = s.Name,
                            s.Command,
                            HealthUrl = "http://127.0.0.1:${PORT}" + s.HealthPath,
                        }).ToList(),
                    },
                },
            },
        };
        File.WriteAllText(Path.Combine(worktree, "ild.config.json"), JsonSerializer.Serialize(config, ConfigJson));
        return Path.GetFullPath(worktree);
    }

    private static readonly Svc Web = new("web", "echo up >> \"${WORKTREE}/launches.txt\"; exec sleep 60");

    private static WorktreePreviewStartOptions Options => new(WorkItemId: WorkItemId);

    private static int Lines(string worktree, string file)
    {
        var path = Path.Combine(worktree, file);
        return File.Exists(path) ? File.ReadAllLines(path).Length : 0;
    }

    /// <summary>Re-reads the status each time a PreviewStateChanged is pushed, until it satisfies <paramref name="done"/>.</summary>
    private async Task<WorktreePreviewResponse> Until(WorktreePreviewService service, string worktree, Func<WorktreePreviewResponse, bool> done)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var status = await service.GetStatusAsync(worktree, limit.Token);
            if (done(status))
                return status;
            await _changes.WaitAsync(limit.Token);
        }
    }

    private static async Task UntilJoined(WorktreePreviewService service, string worktree)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        while (service.JoinedStartCount(worktree) == 0)
            await Task.Delay(10, limit.Token);
    }

    private static WorktreePreviewServiceResponse Row(WorktreePreviewResponse status, string name)
        => status.Services.Single(s => s.Name == name);

    // --- install ---

    [Fact]
    public async Task While_the_install_runs_the_status_is_installing_with_the_step_and_every_service_pending_with_its_port()
    {
        var service = BuildService();
        var (started, go) = (Fifo("started"), Fifo("go"));
        var blocked = $"printf x > '{started}'; cat '{go}' > /dev/null; echo ${{WORKTREE}} > /dev/null";
        var worktree = Worktree("wt-a", ["echo one >> \"${WORKTREE}/installs.txt\"", blocked], [Web]);

        var start = service.StartAsync(worktree, Options, Ct);
        await WaitUntilOpenedAsync(started);

        var status = await Until(service, worktree, s => s.State == "installing");
        Assert.NotNull(status.Install);
        Assert.Equal(2, status.Install!.Index);
        Assert.Equal(2, status.Install.Total);
        Assert.Equal(blocked, status.Install.Command);
        var row = Row(status, "web");
        Assert.Equal("pending", row.Status);
        Assert.NotNull(row.Port);
        Assert.Null(row.ProcessId);
        Assert.Equal(0, Lines(worktree, "launches.txt"));
        _notifier.Verify(n => n.PreviewStateChangedAsync(WorkItemId), Times.AtLeastOnce);

        await ReleaseAsync(go);
        var final = await start;

        Assert.Equal("running", final.State);
        Assert.Null(final.Install);
        Assert.Equal("running", Row(await service.GetStatusAsync(worktree, Ct), "web").Status);
    }

    // --- queued / background ---

    [Fact]
    public async Task A_start_queued_behind_another_worktrees_install_reads_starting_with_every_service_pending()
    {
        var service = BuildService();
        var (started, go) = (Fifo("started"), Fifo("go"));
        var a = Worktree("wt-a", [$"printf x > '{started}'; cat '{go}' > /dev/null"], [Web]);
        var b = Worktree("wt-b", [], [Web, Web with { Name = "api" }]);
        var c = Worktree("wt-c", [], [Web]);

        var startA = service.StartAsync(a, Options, Ct);
        await WaitUntilOpenedAsync(started);

        var queuedBackground = await service.BeginStartAsync(b, Options, Ct);
        var queuedBlocking = service.StartAsync(c, Options, Ct);
        var blockingStatus = await Until(service, c, s => s.State == "starting");

        Assert.Equal("starting", queuedBackground.State);
        Assert.Equal(2, queuedBackground.Services.Count);
        Assert.All(queuedBackground.Services, s => Assert.Equal("pending", s.Status));
        Assert.All(blockingStatus.Services, s => Assert.Equal("pending", s.Status));
        var again = await service.GetStatusAsync(b, Ct);
        Assert.Equal("starting", again.State);
        Assert.All(again.Services, s => Assert.Equal("pending", s.Status));

        await ReleaseAsync(go);
        await startA;
        await queuedBlocking;
        var settled = await Until(service, b, s => s.State == "running");
        Assert.All(settled.Services, s => Assert.Equal("running", s.Status));
    }

    [Fact]
    public async Task A_queued_start_whose_wait_is_cancelled_leaves_no_progress_behind()
    {
        var service = BuildService();
        var (started, go) = (Fifo("started"), Fifo("go"));
        var a = Worktree("wt-a", [$"printf x > '{started}'; cat '{go}' > /dev/null"], [Web]);
        var b = Worktree("wt-b", [], [Web]);
        var startA = service.StartAsync(a, Options, Ct);
        await WaitUntilOpenedAsync(started);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var queued = service.StartAsync(b, Options, cancel.Token);
        await Until(service, b, s => s.State == "starting");
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        var status = await service.GetStatusAsync(b, Ct);
        Assert.Equal("stopped", status.State);
        Assert.All(status.Services, s => Assert.Equal("stopped", s.Status));

        await ReleaseAsync(go);
        await startA;
    }

    [Fact]
    public async Task Concurrent_background_starts_of_one_worktree_run_one_install_and_one_launch_and_never_touch_a_running_preview()
    {
        var service = BuildService();
        var (started, go, launched) = (Fifo("started"), Fifo("go"), Fifo("launched"));
        var worktree = Worktree("wt-a",
            [$"echo x >> \"${{WORKTREE}}/installs.txt\"; printf x > '{started}'; cat '{go}' > /dev/null"],
            [Web with { Command = $"echo up >> \"${{WORKTREE}}/launches.txt\"; printf x > '{launched}'; exec sleep 60" }]);

        var calls = await Task.WhenAll(
            Task.Run(() => service.BeginStartAsync(worktree, Options, Ct), Ct),
            Task.Run(() => service.BeginStartAsync(worktree, Options, Ct), Ct));
        await WaitUntilOpenedAsync(started);

        Assert.All(calls, c => Assert.Contains(c.State, new[] { "starting", "installing" }));
        var viaSecond = await service.BeginStartAsync(worktree, Options, Ct);
        Assert.Contains(viaSecond.State, new[] { "starting", "installing" });

        await ReleaseAsync(go);
        var running = await Until(service, worktree, s => s.State == "running");
        await WaitUntilOpenedAsync(launched);
        Assert.True(service.IsPreviewRunning(worktree));
        var again = await service.BeginStartAsync(worktree, Options, Ct);

        Assert.Equal("running", again.State);
        Assert.Equal(running.Services.Single().ProcessId, again.Services.Single().ProcessId);
        Assert.Equal(1, Lines(worktree, "installs.txt"));
        Assert.Equal(1, Lines(worktree, "launches.txt"));
    }

    [Fact]
    public async Task A_background_start_still_installing_counts_as_a_running_preview()
    {
        var service = BuildService();
        var (started, go) = (Fifo("started"), Fifo("go"));
        var worktree = Worktree("wt-a", [$"printf x > '{started}'; cat '{go}' > /dev/null"], [Web]);

        await service.BeginStartAsync(worktree, Options, Ct);
        await WaitUntilOpenedAsync(started);

        Assert.True(service.IsPreviewRunning(worktree));
        Assert.True(service.AnyPreviewRunning());

        await ReleaseAsync(go);
        await Until(service, worktree, s => s.State == "running");
    }

    [Fact]
    public async Task Stopping_a_background_start_tears_down_what_it_launched_and_leaves_it_stopped()
    {
        var health = new GatedHealth();
        var service = BuildService(health);
        var worktree = Worktree("wt-a", [], [Web with { HealthPath = "/never" }]);

        await service.BeginStartAsync(worktree, Options, Ct);
        var waiting = await Until(service, worktree, s => Row(s, "web").Status == "waitingForHealth");
        var pid = Row(waiting, "web").ProcessId!.Value;

        var stopped = await service.StopAsync(worktree, Ct);

        Assert.Equal("stopped", stopped.State);
        Assert.False(IsAlive(pid));
        Assert.False(service.IsPreviewRunning(worktree));
        Assert.Equal("stopped", (await Until(service, worktree, s => s.State != "starting")).State);
    }

    [Fact]
    public async Task Stopping_a_blocking_start_fails_it_with_a_stopped_message()
    {
        var service = BuildService();
        var (started, go) = (Fifo("started"), Fifo("go"));
        var worktree = Worktree("wt-a", [$"printf x > '{started}'; cat '{go}' > /dev/null"], [Web]);

        var start = service.StartAsync(worktree, Options, Ct);
        await WaitUntilOpenedAsync(started);
        await service.StopAsync(worktree, Ct);

        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => start);
        Assert.Contains("stopped", failure.Message);
        Assert.Equal(0, Lines(worktree, "launches.txt"));
        Assert.False(service.IsPreviewRunning(worktree));
    }

    [Fact]
    public async Task A_blocking_start_that_joins_a_background_start_ends_with_it_and_a_stop_launches_nothing_after()
    {
        var service = BuildService();
        var (started, go) = (Fifo("started"), Fifo("go"));
        var worktree = Worktree("wt-a",
            [$"echo x >> \"${{WORKTREE}}/installs.txt\"; printf x > '{started}'; cat '{go}' > /dev/null"], [Web]);

        await service.BeginStartAsync(worktree, Options, Ct);
        await WaitUntilOpenedAsync(started);
        var joined = service.StartAsync(worktree, Options, Ct);
        await UntilJoined(service, worktree);
        await service.StopAsync(worktree, Ct);

        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => joined);
        Assert.Contains("stopped", failure.Message);
        Assert.False(service.IsPreviewRunning(worktree));
        Assert.Equal("stopped", (await service.GetStatusAsync(worktree, Ct)).State);
        Assert.Equal(1, Lines(worktree, "installs.txt"));
        Assert.Equal(0, Lines(worktree, "launches.txt"));
    }

    [Fact]
    public async Task A_blocking_start_that_joins_a_background_start_answers_with_its_running_preview()
    {
        var service = BuildService();
        var (started, go) = (Fifo("started"), Fifo("go"));
        var worktree = Worktree("wt-a",
            [$"echo x >> \"${{WORKTREE}}/installs.txt\"; printf x > '{started}'; cat '{go}' > /dev/null"], [Web]);

        await service.BeginStartAsync(worktree, Options, Ct);
        await WaitUntilOpenedAsync(started);
        var joined = service.StartAsync(worktree, Options, Ct);
        await UntilJoined(service, worktree);
        await ReleaseAsync(go);

        var running = await joined;
        Assert.Equal("running", running.State);
        Assert.Equal(running.Services.Single().ProcessId, Row(await service.GetStatusAsync(worktree, Ct), "web").ProcessId);
        Assert.Equal(1, Lines(worktree, "installs.txt"));
    }

    // --- services ---

    [Fact]
    public async Task Each_service_turns_running_on_its_own_and_a_dead_one_fails_fast_before_the_start_gives_up()
    {
        var health = new GatedHealth();
        var service = BuildService(health);
        var worktree = Worktree("wt-a", [],
        [
            new Svc("fast", "exec sleep 60", "/fast"),
            new Svc("slow", "exec sleep 60", "/slow"),
            new Svc("bad", "echo boom; exit 3", "/bad"),
        ]);

        var start = service.StartAsync(worktree, Options, Ct);
        var status = await Until(service, worktree, s =>
            Row(s, "fast").Status == "running" && Row(s, "bad").Status == "failed" && Row(s, "slow").Status == "waitingForHealth");

        Assert.Equal("starting", status.State);
        var fast = Row(status, "fast");
        Assert.NotNull(fast.Port);
        Assert.NotNull(fast.HealthUrl);
        Assert.NotNull(fast.LogFilePath);
        Assert.NotNull(fast.ProcessId);
        var bad = Row(status, "bad");
        Assert.Equal(3, bad.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(bad.Error));
        Assert.NotNull(await service.GetServiceLogAsync(worktree, "bad", cancellationToken: Ct));
        Assert.False(start.IsCompleted);

        health.Release("/slow");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => start);

        var after = await service.GetStatusAsync(worktree, Ct);
        Assert.Equal("failed", after.State);
        Assert.Equal(failure.Message, after.Message);
        Assert.Equal("failed", Row(after, "bad").Status);
        Assert.Equal(3, Row(after, "bad").ExitCode);
        Assert.Equal("stopped", Row(after, "fast").Status);
        Assert.Equal("stopped", Row(after, "slow").Status);
        Assert.False(service.IsPreviewRunning(worktree));
        Assert.False(IsAlive(fast.ProcessId!.Value));

        var stopped = await service.StopAsync(worktree, Ct);
        Assert.Equal("stopped", stopped.State);
        Assert.Equal("stopped", (await service.GetStatusAsync(worktree, Ct)).State);
    }

    [Fact]
    public async Task A_background_start_that_fails_keeps_showing_the_failure_after_everything_is_torn_down()
    {
        var service = BuildService();
        var worktree = Worktree("wt-a", [], [Web, new Svc("bad", "exit 4", "/bad")]);

        var immediate = await service.BeginStartAsync(worktree, Options, Ct);
        Assert.Contains(immediate.State, new[] { "starting", "installing" });

        var failed = await Until(service, worktree, s => s.State == "failed");

        Assert.Equal(4, Row(failed, "bad").ExitCode);
        Assert.Equal("stopped", Row(failed, "web").Status);
        Assert.False(string.IsNullOrWhiteSpace(failed.Message));
        Assert.False(service.IsPreviewRunning(worktree));
        Assert.Equal("failed", (await service.GetStatusAsync(worktree, Ct)).State);
    }

    [Fact]
    public async Task Disposing_the_service_cancels_a_background_start_and_stops_what_it_launched()
    {
        var health = new GatedHealth();
        var service = BuildService(health);
        var written = Fifo("pid-written");
        var worktree = Worktree("wt-a", [], [new Svc("web", $"echo $$ > \"${{WORKTREE}}/pid.txt\"; printf x > '{written}'; exec sleep 60", "/never")]);

        await service.BeginStartAsync(worktree, Options, Ct);
        var status = await Until(service, worktree, s => Row(s, "web").Status == "waitingForHealth");
        await WaitUntilOpenedAsync(written);
        var pid = status.Services.Single().ProcessId!.Value;
        Assert.Equal(pid, int.Parse(File.ReadAllText(Path.Combine(worktree, "pid.txt")).Trim()));
        Assert.True(IsAlive(pid));

        service.Dispose();

        Assert.False(IsAlive(pid));
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)..].StartsWith('Z') is false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // --- pipes and health ---

    private string Fifo(string name)
    {
        var path = Path.Combine(_root, "fifo-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
        using var mkfifo = Process.Start("mkfifo", path);
        mkfifo.WaitForExit();
        Assert.Equal(0, mkfifo.ExitCode);
        _fifosToRelease.Add(path);
        return path;
    }

    private static Task WaitUntilOpenedAsync(string fifo) => Task.Run(() => File.ReadAllText(fifo), Ct);

    private static Task ReleaseAsync(string fifo) => Task.Run(() => File.WriteAllText(fifo, "go"), Ct);

    private static void Unblock(string fifo)
    {
        try
        {
            using var stream = new FileStream(fifo, FileMode.Open, FileAccess.Write | FileAccess.Read);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Answers a probe by its path: /fast and /ok succeed, /bad is refused, and /slow and
    /// /never hold the probe open (until released / cancelled) so one service can sit in
    /// "waitingForHealth" while its siblings settle.
    /// </summary>
    private sealed class GatedHealth : HttpMessageHandler
    {
        private readonly Dictionary<string, TaskCompletionSource> _gates = new()
        {
            ["/slow"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["/never"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        public void Release(string path) => _gates[path].TrySetResult();

        public void ReleaseAll()
        {
            foreach (var gate in _gates.Values)
                gate.TrySetResult();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (_gates.TryGetValue(path, out var gate))
                await gate.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(path == "/bad" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }
    }
}
