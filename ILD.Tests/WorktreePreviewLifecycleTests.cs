using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Tests.Fakes;
using ILD.Data.DTOs;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The start protocol and teardown of a worktree's preview, through <see cref="WorktreePreviewLifecycle"/> on a
/// <see cref="FakeRunHost"/> and a scripted health probe: nothing here launches a process or waits on a clock, so
/// every interleaving is the test's to order.
/// </summary>
public sealed class WorktreePreviewLifecycleTests : IDisposable
{
    private const string A = "/fake-worktrees/wt-a";
    private const string B = "/fake-worktrees/wt-b";

    private readonly FakeRunHost _host = new();
    private readonly FakePreviewHealthProbe _probe = new();
    private readonly PreviewDataClaims _claims = new();
    private readonly PreviewLifecycleContext _context;
    private readonly Guid _repository = Guid.NewGuid();

    public WorktreePreviewLifecycleTests()
    {
        _host.Script = _ => FakeCommandScript.Hangs();
        _context = new PreviewLifecycleContext(
            _host, _probe, ports: new PreviewPorts(_ => true), claims: _claims, logger: NullLogger.Instance);
    }

    public void Dispose() => _context.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Svc(string Name, string Command = "exec sleep 60");

    private void Config(string worktree, params Svc[] services) => Config(worktree, [], services);

    private void Config(string worktree, string[] install, params Svc[] services)
    {
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
                            name = s.Name,
                            port = s.Name,
                            command = s.Command,
                            healthUrl = "http://127.0.0.1:${PORT}/health",
                        }).ToList(),
                    },
                },
            },
        };
        _host.WithDirectory(worktree).WithFile(worktree + "/ild.config.json", JsonSerializer.Serialize(config));
    }

    private static readonly Svc Web = new("web");
    private static readonly Svc Api = new("api");

    private WorktreePreviewLifecycle Lifecycle(string worktree) => _context.For(worktree);

    private async Task<LoadedPreviewConfig> LoadAsync(string worktree) => await _context.Config.LoadAsync(worktree, Ct);

    private async Task<WorktreePreviewResponse> StartAsync(string worktree, PreviewStartScope? scope = null, WorktreePreviewStartOptions? options = null)
        => await Lifecycle(worktree).StartAsync(await LoadAsync(worktree), scope ?? PreviewStartScope.Whole, options, Ct);

    private async Task<WorktreePreviewResponse> BeginAsync(string worktree, WorktreePreviewStartOptions? options = null)
        => await Lifecycle(worktree).BeginAsync(await LoadAsync(worktree), PreviewStartScope.Whole, options, Ct);

    private async Task<WorktreePreviewResponse> StatusAsync(string worktree)
        => Lifecycle(worktree).Status(await LoadAsync(worktree));

    private static WorktreePreviewServiceResponse Row(WorktreePreviewResponse status, string name)
        => status.Services.Single(s => s.Name == name);

    private static PreviewStartScope Service(string name, bool dependencies = false) => new(name, dependencies);

    private static string CommandOf(RunCommand command) => command.Start.ArgumentList[1];

    private PreviewVolumeScope Scope(string workItemId) => new(_repository, true, workItemId, "Title");

    private bool Holds(string workItemId, string worktree) => !_claims.TryAcquire(Scope(workItemId), worktree, out var lease, out _) || Release(lease);

    private static bool Release(PreviewDataClaims.Lease lease)
    {
        lease.Dispose();
        return false;
    }

    private async Task WaitUntilOvertakenAsync(string worktree)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        // The overtaken start is listed before the wider one is swapped in, so both must be seen: a stop in
        // between would find only the overtaken start and leave the wider one running.
        while (Lifecycle(worktree) is not { OvertakenStartCount: > 0, IsStartingWhole: true })
            await Task.Delay(10, timeout.Token);
    }

    private static async Task WaitUntilAsync(Func<bool> reached)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        while (!reached())
            await Task.Delay(5, limit.Token);
    }

    private async Task WaitUntilJoinedAsync(string worktree)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        while (Lifecycle(worktree).JoinedStartCount == 0)
            await Task.Delay(5, limit.Token);
    }

    // --- one start ---

    [Fact]
    public async Task A_whole_start_reads_starting_until_every_service_is_healthy_and_only_then_has_a_runtime()
    {
        Config(A, Web, Api);
        _probe.Hold("api");

        var start = StartAsync(A);
        await _probe.ReachedAsync("api");

        var status = await StatusAsync(A);
        Assert.Equal("starting", status.State);
        Assert.Equal("starting", Lifecycle(A).State());
        Assert.Null(Lifecycle(A).Runtime);
        Assert.NotNull(Row(status, "web").ProcessId);
        Assert.Equal("waitingForHealth", Row(status, "api").Status);

        _probe.Release("api");
        var running = await start;

        Assert.Equal("running", running.State);
        Assert.Equal("running", Lifecycle(A).State());
        Assert.NotNull(Lifecycle(A).Runtime);
        Assert.Equal(2, _host.Started.Count);
    }

    [Fact]
    public async Task A_failed_start_takes_down_everything_it_launched_and_keeps_the_failure_on_show()
    {
        Config(A, Web, Api);
        _probe.Fail("api", "api is sick");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(A));

        Assert.Equal("api is sick", failure.Message);
        Assert.All(_host.Processes, p => Assert.True(p.WasKilled));
        Assert.Null(Lifecycle(A).Runtime);
        Assert.False(Lifecycle(A).IsStarting);
        Assert.Equal("stopped", Lifecycle(A).State());
        var status = await StatusAsync(A);
        Assert.Equal("failed", status.State);
        Assert.Equal("api is sick", status.Message);
        Assert.Equal("failed", Row(status, "api").Status);
        Assert.Equal("stopped", Row(status, "web").Status);
    }

    [Fact]
    public async Task A_failed_start_lets_go_of_the_preview_data_it_claimed()
    {
        Config(A, Web);
        Config(B, Web);
        _probe.Fail("web", "never healthy");

        await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(A, options: new(Volumes: Scope("1"))));
        Assert.False(Holds("2", B));
    }

    [Fact]
    public async Task A_start_that_succeeds_holds_the_preview_data_until_it_is_stopped()
    {
        Config(A, Web);

        await StartAsync(A, options: new(Volumes: Scope("1")));
        Assert.True(Holds("2", B));

        await Lifecycle(A).StopAsync(Ct);
        Assert.False(Holds("2", B));
    }

    [Fact]
    public async Task A_start_of_a_preview_that_is_already_running_answers_with_it_and_launches_nothing()
    {
        Config(A, Web);
        var first = await StartAsync(A);

        var again = await StartAsync(A);

        Assert.Equal("running", again.State);
        Assert.Equal(Row(first, "web").ProcessId, Row(again, "web").ProcessId);
        Assert.Single(_host.Started);
        Assert.False(Lifecycle(A).IsStarting);
    }

    // --- a wider start during a single-service start ---

    [Fact]
    public async Task A_stop_also_ends_a_whole_start_queued_behind_a_single_service_start()
    {
        Config(A, Web, Api);
        _probe.Hold("web");
        var service = StartAsync(A, Service("web"));
        await _probe.ReachedAsync("web");
        var launched = _host.Started.Count;
        var whole = StartAsync(A);
        await WaitUntilOvertakenAsync(A);

        var stopped = await Lifecycle(A).StopAsync(Ct);

        Assert.Equal("stopped", stopped.State);
        await Assert.ThrowsAnyAsync<Exception>(() => whole);
        await Assert.ThrowsAnyAsync<Exception>(() => service);
        Assert.False(Lifecycle(A).IsStarting);
        Assert.Null(Lifecycle(A).Runtime);
        Assert.Equal(launched, _host.Started.Count);
        Assert.Equal(0, Lifecycle(A).OvertakenStartCount);
    }

    [Fact]
    public async Task A_background_start_answers_at_once_while_a_single_service_start_is_in_flight()
    {
        Config(A, Web, Api);
        _probe.Hold("web");
        var service = StartAsync(A, Service("web"));
        await _probe.ReachedAsync("web");

        var begun = await BeginAsync(A).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.False(service.IsCompleted);
        Assert.True(Lifecycle(A).IsStarting);
        Assert.Contains(begun.State, new[] { "starting", "installing", "partial" });

        _probe.Release("web");
        await service;
        await Lifecycle(A).StopAsync(Ct);
    }

    // --- joining ---

    [Fact]
    public async Task A_blocking_start_joins_the_start_in_progress_and_ends_with_it()
    {
        Config(A, Web);
        _probe.Hold("web");
        await BeginAsync(A);
        await _probe.ReachedAsync("web");

        var joined = StartAsync(A);
        await WaitUntilJoinedAsync(A);
        _probe.Release("web");

        var running = await joined;
        Assert.Equal("running", running.State);
        Assert.Single(_host.Started);
    }

    [Fact]
    public async Task A_second_background_start_follows_the_first_instead_of_running_again()
    {
        Config(A, Web);
        _probe.Hold("web");

        var calls = await Task.WhenAll(BeginAsync(A), BeginAsync(A));
        await _probe.ReachedAsync("web");

        Assert.All(calls, c => Assert.Equal("starting", c.State));
        Assert.True(Lifecycle(A).IsStarting);
        _probe.Release("web");
        Assert.Equal("running", (await StartAsync(A)).State);
        Assert.Single(_host.Started);
    }

    [Fact]
    public async Task A_single_service_start_joins_a_whole_start_in_progress()
    {
        Config(A, Web, Api);
        _probe.Hold("api");
        await BeginAsync(A);
        await _probe.ReachedAsync("api");

        var joined = StartAsync(A, Service("web"));
        await WaitUntilJoinedAsync(A);
        _probe.Release("api");

        Assert.Equal("running", (await joined).State);
        Assert.Equal(2, _host.Started.Count);
    }

    [Fact]
    public async Task A_whole_start_waits_for_a_single_service_start_and_then_brings_everything_up()
    {
        Config(A, Web, Api);
        _probe.Hold("web");
        var service = StartAsync(A, Service("web"));
        await _probe.ReachedAsync("web");

        var whole = StartAsync(A);
        await Task.Delay(50, Ct);
        Assert.False(whole.IsCompleted);
        Assert.Equal(0, Lifecycle(A).JoinedStartCount);

        _probe.Release("web");
        await service;
        var running = await whole;

        Assert.Equal("running", running.State);
        Assert.Equal(3, _host.Started.Count);
        Assert.True(_host.Processes[0].WasKilled);
        Assert.Equal(2, Lifecycle(A).Runtime!.Processes.Count);
    }

    // --- stopping ---

    [Fact]
    public async Task Stopping_a_start_in_progress_tears_down_what_it_launched_and_leaves_the_preview_stopped()
    {
        Config(A, Web);
        _probe.Hold("web");
        await BeginAsync(A);
        await _probe.ReachedAsync("web");

        var stopped = await Lifecycle(A).StopAsync(Ct);

        Assert.Equal("stopped", stopped.State);
        Assert.All(_host.Processes, p => Assert.True(p.WasKilled));
        Assert.Null(Lifecycle(A).Runtime);
        Assert.False(Lifecycle(A).IsStarting);
        Assert.Equal("stopped", (await StatusAsync(A)).State);
    }

    [Fact]
    public async Task Stopping_a_blocking_start_fails_it_with_a_stopped_message()
    {
        Config(A, Web);
        _probe.Hold("web");
        var start = StartAsync(A);
        await _probe.ReachedAsync("web");

        await Lifecycle(A).StopAsync(Ct);

        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => start);
        Assert.Contains("stopped", failure.Message);
        Assert.False(Lifecycle(A).IsStarting);
    }

    [Fact]
    public async Task A_stop_during_the_install_step_ends_the_start_and_no_service_is_launched_after_it()
    {
        Config(A, ["install-the-tools"], Web);
        await BeginAsync(A);
        await WaitUntilAsync(() => _host.Started.Count == 1);
        var joined = StartAsync(A);
        await WaitUntilJoinedAsync(A);

        var stopped = await Lifecycle(A).StopAsync(Ct);

        Assert.Equal("stopped", stopped.State);
        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => joined);
        Assert.Contains("stopped", failure.Message);
        Assert.Contains("install-the-tools", CommandOf(Assert.Single(_host.Started)));
        Assert.Null(Lifecycle(A).Runtime);
        Assert.False(Lifecycle(A).IsStarting);
        Assert.Equal("stopped", (await StatusAsync(A)).State);
    }

    [Fact]
    public async Task A_start_that_joined_one_stopped_during_its_launch_ends_with_it_and_nothing_is_launched_after_the_stop()
    {
        Config(A, Web);
        _probe.Hold("web");
        await BeginAsync(A);
        await _probe.ReachedAsync("web");
        var joined = StartAsync(A);
        await WaitUntilJoinedAsync(A);

        await Lifecycle(A).StopAsync(Ct);

        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => joined);
        Assert.Contains("stopped", failure.Message);
        Assert.Single(_host.Started);
        Assert.Null(Lifecycle(A).Runtime);
    }

    [Fact]
    public async Task Disposing_cancels_a_start_in_progress_and_stops_what_it_launched()
    {
        Config(A, Web);
        _probe.Hold("web");
        var start = StartAsync(A);
        await _probe.ReachedAsync("web");

        _context.Dispose();

        Assert.All(_host.Processes, p => Assert.True(p.WasKilled));
        await Assert.ThrowsAnyAsync<Exception>(() => start);
        Assert.Null(Lifecycle(A).Runtime);
        Assert.False(Lifecycle(A).IsStarting);
    }

    [Fact]
    public async Task Disposing_stops_the_preview_of_every_worktree()
    {
        Config(A, Web);
        Config(B, Web);
        await StartAsync(A);
        await StartAsync(B);

        _context.Dispose();

        Assert.Equal(2, _host.Processes.Count);
        Assert.All(_host.Processes, p => Assert.True(p.WasKilled));
        Assert.False(Lifecycle(A).IsStarting);
    }

    // --- queuing ---

    [Fact]
    public async Task A_start_queued_behind_another_worktrees_start_reads_starting_with_every_service_pending()
    {
        Config(A, Web);
        Config(B, Web, Api);
        _probe.Hold("web");
        var first = StartAsync(A);
        await _probe.ReachedAsync("web");

        var queued = await BeginAsync(B);

        Assert.Equal("starting", queued.State);
        Assert.All(queued.Services, s => Assert.Equal("pending", s.Status));
        var again = await StatusAsync(B);
        Assert.Equal("starting", again.State);
        Assert.All(again.Services, s => Assert.Equal("pending", s.Status));
        Assert.Single(_host.Started);

        _probe.Release("web");
        await first;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        while ((await StatusAsync(B)).State != "running")
            await Task.Delay(5, limit.Token);
    }

    [Fact]
    public async Task A_queued_start_whose_wait_is_cancelled_leaves_no_progress_behind()
    {
        Config(A, Web);
        Config(B, Web);
        _probe.Hold("web");
        var first = StartAsync(A);
        await _probe.ReachedAsync("web");

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var queued = Lifecycle(B).StartAsync(await LoadAsync(B), PreviewStartScope.Whole, null, cancel.Token);
        while (!Lifecycle(B).IsStarting)
            await Task.Delay(5, Ct);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        Assert.False(Lifecycle(B).IsStarting);
        var status = await StatusAsync(B);
        Assert.Equal("stopped", status.State);
        Assert.All(status.Services, s => Assert.Equal("stopped", s.Status));

        _probe.Release("web");
        await first;
    }

    // --- a single service ---

    [Fact]
    public async Task A_single_service_start_shows_starting_and_its_service_running_while_it_waits_for_health()
    {
        Config(A, Web, Api);
        _probe.Hold("web");

        var start = StartAsync(A, Service("web"));
        await _probe.ReachedAsync("web");

        Assert.Equal("starting", Lifecycle(A).State());
        Assert.NotNull(Lifecycle(A).Runtime);
        var status = await StatusAsync(A);
        Assert.Equal("running", Row(status, "web").Status);
        Assert.Equal("stopped", Row(status, "api").Status);
        Assert.False(start.IsCompleted);

        _probe.Release("web");
        var done = await start;

        Assert.Equal("partial", done.State);
        Assert.Equal("partial", Lifecycle(A).State());
        Assert.False(Lifecycle(A).IsStarting);
    }

    [Fact]
    public async Task A_single_service_start_announces_the_launched_service_before_its_health_check_passes()
    {
        Config(A, Web, Api);
        _probe.Hold("web");
        var announced = new List<string>();
        var notifier = new Mock<IWorkItemNotifier>();
        using var context = new PreviewLifecycleContext(
            _host, _probe, ports: new PreviewPorts(_ => true), notifier: new PreviewNotifier(notifier.Object, NullLogger.Instance), logger: NullLogger.Instance);
        var loaded = await context.Config.LoadAsync(A, Ct);
        notifier.Setup(n => n.PreviewStateChangedAsync("wi-7"))
            .Returns(() =>
            {
                lock (announced) announced.Add(Row(context.For(A).Status(loaded), "web").Status);
                return Task.CompletedTask;
            });

        var start = context.For(A).StartAsync(loaded, Service("web"), new WorktreePreviewStartOptions(WorkItemId: "wi-7"), Ct);
        await _probe.ReachedAsync("web");

        Assert.False(start.IsCompleted);
        lock (announced) Assert.Contains("running", announced);

        _probe.Release("web");
        await start;
    }

    [Fact]
    public async Task A_single_service_that_is_not_healthy_stays_registered_with_its_process_running()
    {
        Config(A, Web, Api);
        _probe.Fail("web", "web is sick");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(A, Service("web")));

        Assert.Equal("web is sick", failure.Message);
        var process = Assert.Single(Lifecycle(A).Runtime!.Processes);
        Assert.True(process.IsRunning);
        Assert.False(Assert.Single(_host.Processes).WasKilled);
        Assert.False(Lifecycle(A).IsStarting);
        Assert.Equal("running", Row(await StatusAsync(A), "web").Status);
    }

    [Fact]
    public async Task A_single_service_whose_launch_fails_leaves_no_runtime_and_holds_no_preview_data()
    {
        Config(A, Web);
        _host.Script = _ => throw new IOException("spawn failed");

        await Assert.ThrowsAsync<IOException>(() => StartAsync(A, Service("web"), new(Volumes: Scope("1"))));

        Assert.Null(Lifecycle(A).Runtime);
        Assert.False(Lifecycle(A).IsStarting);
        Assert.Equal("stopped", Lifecycle(A).State());
        Assert.False(Holds("2", B));
    }

    [Fact]
    public async Task A_single_service_start_joins_the_runtime_that_is_running_and_replaces_an_exited_process()
    {
        Config(A, Web, Api);
        await StartAsync(A);
        var exited = _host.Processes[0];
        Assert.True(exited.TryKill());

        var started = await StartAsync(A, Service("web"));

        Assert.Equal("running", started.State);
        Assert.Equal(3, _host.Started.Count);
        Assert.Equal(2, Lifecycle(A).Runtime!.Processes.Count);
    }

    [Fact]
    public async Task A_single_service_that_is_already_running_is_answered_without_launching_again()
    {
        Config(A, Web, Api);
        await StartAsync(A);

        var again = await StartAsync(A, Service("web"));

        Assert.Equal("running", again.State);
        Assert.Equal(2, _host.Started.Count);
        Assert.False(Lifecycle(A).IsStarting);
    }

    [Fact]
    public async Task A_single_service_that_names_the_port_of_one_that_is_not_running_is_refused_before_anything_is_created()
    {
        Config(A, Web, new Svc("api", "serve ${PORT} --upstream ${PORT:web}"));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(A, Service("api")));

        Assert.Contains("Start 'web' first", failure.Message);
        Assert.Empty(_host.Started);
        Assert.Null(Lifecycle(A).Runtime);
        Assert.False(Lifecycle(A).IsStarting);
    }

    [Fact]
    public async Task A_single_service_start_with_dependencies_launches_the_owners_of_the_ports_it_names_first()
    {
        Config(A, new Svc("api", "serve ${PORT} --upstream ${PORT:web}"), new Svc("web", "serve-web ${PORT}"));

        var started = await StartAsync(A, Service("api", dependencies: true));

        Assert.Equal("running", started.State);
        Assert.StartsWith("serve-web", CommandOf(_host.Started[0]));
        Assert.StartsWith("serve ", CommandOf(_host.Started[1]));
        Assert.Equal(["web", "api"], _probe.Probed);
    }

    [Fact]
    public async Task A_single_service_that_is_not_in_the_profile_is_refused_without_a_record()
    {
        Config(A, Web);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(A, Service("nope")));

        Assert.Contains("'nope' not found", failure.Message);
        Assert.False(Lifecycle(A).IsStarting);
    }

    [Fact]
    public async Task Stopping_the_last_service_retires_the_runtime_and_lets_go_of_the_preview_data()
    {
        Config(A, Web);
        await StartAsync(A, Service("web"), new(Volumes: Scope("1")));
        Assert.True(Holds("2", B));

        var stopped = await Lifecycle(A).StopServiceAsync("web", Ct);

        Assert.Equal("stopped", stopped.State);
        Assert.Null(Lifecycle(A).Runtime);
        Assert.False(Holds("2", B));
    }

    // --- a preview an earlier ILD left running ---

    private static readonly Guid RunId = Guid.Parse("0a2b6c3d-1111-4222-8333-944455566677");

    [Fact]
    public async Task A_start_with_no_runtime_ends_what_an_earlier_ild_left_running_whichever_scope_it_has()
    {
        var worktree = RunPaths.WorkingDirectory(RunId);
        _host.Kind = RunHostKind.Container;
        _host.StateDirectoryFor = path => $"{RunPaths.RunRoot(RunPaths.OwnerOf(path)!.Value)}/preview-state";
        Config(worktree, Web, Api);

        await StartAsync(worktree, Service("web"));
        Assert.Equal([RunId], _host.KillUnownedCalls);

        // With a preview of this ILD's own in the run there is nothing left behind to end.
        await StartAsync(worktree, Service("api"));
        Assert.Equal([RunId], _host.KillUnownedCalls);
    }


    [Fact]
    public void A_start_of_one_service_covers_no_more_than_that_service_and_a_whole_start_covers_everything()
    {
        Assert.True(PreviewStartScope.Whole.Covers(PreviewStartScope.Whole));
        Assert.True(PreviewStartScope.Whole.Covers(Service("web", dependencies: true)));
        Assert.True(Service("web").Covers(Service("WEB")));
        Assert.True(Service("web", dependencies: true).Covers(Service("web")));
        Assert.False(Service("web").Covers(Service("web", dependencies: true)));
        Assert.False(Service("web").Covers(Service("api")));
        Assert.False(Service("web").Covers(PreviewStartScope.Whole));
    }
}
