using System.Runtime.CompilerServices;
using ILD.Api.Configuration;
using ILD.Api.Services;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The engine half of graceful shutdown: what a SIGTERM does to the runs this
/// process is driving, and what it deliberately does not do. The resume half —
/// three separate paths that bring a shutdown-halted run back — is covered in
/// <c>RecoveryManagerTests</c>, <c>RemoteWorkItemStartupReconcilerTests</c> and
/// <c>StuckRunWatchdogTests</c>.
/// </summary>
public class GracefulShutdownDrainTests
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Drain_parks_an_in_flight_ai_node_as_a_shutdown_halt()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI);
        var blocking = new BlockingExecutor(NodeType.AI);
        h.Registry.Register(blocking);
        var seeded = h.SeedRun("ai");

        await h.LaunchAsync();
        await blocking.Entered;

        await h.Engine.DrainForShutdownAsync(DrainTimeout);

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.True(run.IsHalted);
        Assert.Equal(HaltReason.Shutdown, run.HaltReason);
        Assert.True(run.IsShutdownHalted);
        // Kept so the resume re-runs the same node against the same agent session.
        Assert.Equal(seeded.CurrentNodeId, run.CurrentNodeId);
        // Null on purpose: this park drives no "human input needed" badge,
        // because nobody is being asked for anything.
        Assert.Null(run.HumanFeedbackReason);
    }

    [Fact]
    public async Task Drain_does_not_move_the_work_item_to_HumanFeedback()
    {
        // Leaving the item Running on the server is what lets the startup
        // reconciler recognise the run as still ours and resume it.
        using var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI);
        var blocking = new BlockingExecutor(NodeType.AI);
        h.Registry.Register(blocking);
        h.SeedRun("ai");

        await h.LaunchAsync();
        await blocking.Entered;
        await h.Engine.DrainForShutdownAsync(DrainTimeout);

        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            It.IsAny<string>(),
            It.IsAny<RemoteWorkItemStatus>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task Drain_waits_for_the_driving_loop_to_unwind()
    {
        // The park is written before the agent is killed, but the node's
        // interrupted bookkeeping happens as the loop unwinds. A drain that
        // returned early would leave exactly the half-written state — a node
        // stuck Running with no driver — that this feature exists to remove.
        using var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI);
        var blocking = new BlockingExecutor(NodeType.AI);
        h.Registry.Register(blocking);
        h.SeedRun("ai");

        await h.LaunchAsync();
        await blocking.Entered;
        Assert.Contains(h.RunId, await h.Engine.GetActiveRunIdsAsync());

        await h.Engine.DrainForShutdownAsync(DrainTimeout);

        Assert.Empty(await h.Engine.GetActiveRunIdsAsync());
        var nodes = h.ReloadRunNodes();
        Assert.NotEmpty(nodes);
        Assert.All(nodes, n => Assert.Equal(LoopRunNodeStatus.Interrupted, n.Status));
    }

    [Fact]
    public async Task Drain_leaves_a_non_ai_node_Running_for_ordinary_recovery()
    {
        // A Cmd node is cheap to redo and not worth a park a human might have to
        // clear: cancel it and let the existing crash-recovery path re-drive it.
        using var h = new LoopEngineHarness();
        h.AddNode("cmd", NodeType.Cmd);
        var blocking = new BlockingExecutor(NodeType.Cmd);
        h.Registry.Register(blocking);
        h.SeedRun("cmd");

        await h.LaunchAsync();
        await blocking.Entered;
        await h.Engine.DrainForShutdownAsync(DrainTimeout);

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Running, run.Status);
        Assert.False(run.IsHalted);
        Assert.Null(run.HaltReason);
        // Cancelled all the same — the in-flight work must still stop.
        Assert.Empty(await h.Engine.GetActiveRunIdsAsync());
    }

    [Fact]
    public async Task Drain_with_nothing_in_flight_is_a_no_op()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI);
        h.SeedRun("ai");

        await h.Engine.DrainForShutdownAsync(DrainTimeout);

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Running, run.Status);
        Assert.False(run.IsHalted);
        Assert.Empty(h.ReloadRunNodes());
    }

    [Fact]
    public async Task Nothing_launches_once_the_host_is_stopping()
    {
        // A claim, resume or webhook landing mid-shutdown would otherwise spawn a
        // driver the drain has already walked past — one nothing parks and
        // nothing waits for.
        var stopping = new ShutdownState();
        stopping.SignalStopping();

        using var h = new LoopEngineHarness(shutdown: stopping);
        h.AddNode("ai", NodeType.AI);
        var blocking = new BlockingExecutor(NodeType.AI);
        h.Registry.Register(blocking);
        h.SeedRun("ai");

        await h.LaunchAsync();

        Assert.Empty(await h.Engine.GetActiveRunIdsAsync());
        Assert.False(blocking.Entered.IsCompleted);
        Assert.Empty(h.ReloadRunNodes());
    }

    [Fact]
    public async Task Resuming_a_shutdown_halted_run_clears_the_stamp()
    {
        // Left set, the next halt a human presses would look like a shutdown park
        // and be auto-resumed out from under them on the following restart.
        using var h = new LoopEngineHarness();
        h.Registry.Register(new ScriptedExecutor(NodeType.AI));
        h.AddNode("ai", NodeType.AI);
        h.SeedRun("ai", LoopRunStatus.WaitingHuman, isHalted: true, haltReason: HaltReason.Shutdown);

        await h.Engine.ResumeFromHaltAsync(h.RunId, null);
        await h.WaitUntilIdleAsync();

        var run = h.ReloadRun();
        Assert.False(run.IsHalted);
        Assert.Null(run.HaltReason);
        Assert.False(run.IsShutdownHalted);
        // A null note becomes the empty steer the AI executor turns into
        // "Continue where you left off." against the captured session.
        Assert.Equal(string.Empty, run.SteeringNote);
    }

    [Fact]
    public async Task Retrying_a_node_clears_the_stamp_too()
    {
        using var h = new LoopEngineHarness();
        h.Registry.Register(new ScriptedExecutor(NodeType.AI));
        h.AddNode("ai", NodeType.AI);
        h.SeedRun("ai", LoopRunStatus.WaitingHuman, isHalted: true, haltReason: HaltReason.Shutdown);
        var runNode = new ILD.Data.Entities.LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = h.RunId,
            LoopNodeId = h.NodesById["ai"].Id,
            Status = LoopRunNodeStatus.Interrupted,
        };
        h.Db.Context.LoopRunNodes.Add(runNode);
        h.Db.Context.SaveChanges();

        await h.Engine.RetryFromNodeAsync(h.RunId, runNode.Id);
        await h.WaitUntilIdleAsync();

        var run = h.ReloadRun();
        Assert.False(run.IsHalted);
        Assert.Null(run.HaltReason);
    }

    [Fact]
    public async Task Scheduler_stops_claiming_ready_items_once_stopping()
    {
        // The rest of the pass — heartbeats above all — must keep running, so
        // live runs hold their work-item claims until the drain parks them.
        var claimReadyValues = new List<bool>();
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var coord = new Mock<IRemoteWorkItemCoordinator>();
        coord.Setup(c => c.RunPollCycleAsync(
                It.IsAny<WorkItemServerOptions>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<WorkItemServerOptions, int, bool, CancellationToken>((_, _, claimReady, _) =>
            {
                claimReadyValues.Add(claimReady);
                polled.TrySetResult();
            })
            .ReturnsAsync(new PollCycleResult());

        var settings = new Mock<ISchedulerSettingsService>();
        settings.Setup(s => s.GetIsPausedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        settings.Setup(s => s.GetMaxConcurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(5);

        var services = new ServiceCollection();
        services.AddScoped<ISchedulerSettingsService>(_ => settings.Object);
        services.AddScoped<IRemoteWorkItemCoordinator>(_ => coord.Object);
        using var sp = services.BuildServiceProvider();

        var stopping = new ShutdownState();
        stopping.SignalStopping();

        var scheduler = new WorkItemScheduler(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<WorkItemSchedulerOptions>(new WorkItemSchedulerOptions
            {
                Enabled = true,
                BaseUrl = "http://localhost",
                ApiKey = "k",
                PollInterval = TimeSpan.FromMilliseconds(50),
            }),
            NullLogger<WorkItemScheduler>.Instance,
            TimeProvider.System,
            stopping);

        await scheduler.StartAsync(CancellationToken.None);
        var winner = await Task.WhenAny(polled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        await scheduler.StopAsync(CancellationToken.None);

        Assert.True(winner == polled.Task, "Scheduler never invoked the poll cycle");
        Assert.NotEmpty(claimReadyValues);
        Assert.All(claimReadyValues, v => Assert.False(v));
    }

    [Fact]
    public async Task ApplicationStopping_raises_the_flag_before_any_service_has_stopped()
    {
        // The flag has to rise here rather than at the drain's own StopAsync:
        // ApplicationStopping fires once, before any hosted service stops, and
        // the window between it and the drain is precisely when the scheduler
        // would otherwise claim a work item and launch a run nobody parks.
        var engine = new Mock<ILoopEngine>();
        var shutdown = new ShutdownState();
        var lifetime = new NoopLifetime();
        var service = new GracefulRunDrainService(
            engine.Object, shutdown, new ShutdownOptions(), lifetime,
            NullLogger<GracefulRunDrainService>.Instance);

        await service.StartAsync(CancellationToken.None);
        Assert.False(shutdown.IsStopping);

        lifetime.StopApplication();

        Assert.True(shutdown.IsStopping);
        // Nothing has been drained yet — the host has only announced the stop.
        engine.Verify(e => e.DrainForShutdownAsync(It.IsAny<TimeSpan>()), Times.Never);
    }

    [Fact]
    public async Task The_hosted_service_signals_stopping_and_drains_on_stop()
    {
        var engine = new Mock<ILoopEngine>();
        engine.Setup(e => e.DrainForShutdownAsync(It.IsAny<TimeSpan>())).Returns(Task.CompletedTask);
        var shutdown = new ShutdownState();
        var options = new ShutdownOptions { DrainTimeout = TimeSpan.FromSeconds(7) };
        var service = new GracefulRunDrainService(
            engine.Object, shutdown, options, new NoopLifetime(),
            NullLogger<GracefulRunDrainService>.Instance);

        await service.StartAsync(CancellationToken.None);
        Assert.False(shutdown.IsStopping);

        // A host stopped programmatically can reach StopAsync without the
        // lifetime callback ever firing, so the stop signals defensively.
        await service.StopAsync(CancellationToken.None);

        Assert.True(shutdown.IsStopping);
        engine.Verify(e => e.DrainForShutdownAsync(TimeSpan.FromSeconds(7)), Times.Once);
    }

    [Fact]
    public async Task A_failing_drain_never_blocks_process_exit()
    {
        var engine = new Mock<ILoopEngine>();
        engine.Setup(e => e.DrainForShutdownAsync(It.IsAny<TimeSpan>()))
            .ThrowsAsync(new InvalidOperationException("drain blew up"));
        var service = new GracefulRunDrainService(
            engine.Object, new ShutdownState(), new ShutdownOptions(), new NoopLifetime(),
            NullLogger<GracefulRunDrainService>.Instance);

        await service.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(null, 20)]
    [InlineData("", 20)]
    [InlineData("not-a-number", 20)]
    [InlineData("0", 20)]      // would silently restore the hard kill
    [InlineData("-5", 20)]
    [InlineData("45", 45)]
    public void Drain_timeout_falls_back_to_the_default_for_anything_unusable(string? raw, int expectedSeconds)
    {
        var options = ShutdownOptions.FromEnvironment(_ => raw);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.DrainTimeout);
        // The host must be willing to wait strictly longer than the drain that
        // runs inside its stop, or it abandons the unwinding it just asked for.
        Assert.True(options.HostShutdownTimeout > options.DrainTimeout);
    }

    /// <summary>
    /// Hosted services stop in reverse registration order, so the drain being
    /// registered last is what has it run first — while the notifier it
    /// publishes through, the scopes it opens and the scheduler whose heartbeats
    /// hold the work-item claims are all still standing. A merge or a sort that
    /// moves the line silently reopens that, and nothing else would fail.
    /// </summary>
    [Fact]
    public void The_drain_is_the_last_hosted_service_registered()
    {
        var hosted = new ServiceCollection().AddIldServices()
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(ImplementationTypeOf)
            .ToList();

        Assert.Equal(typeof(GracefulRunDrainService), hosted[^1]);
    }

    private static Type? ImplementationTypeOf(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationType is { } type) return type;
        if (descriptor.ImplementationInstance is { } instance) return instance.GetType();
        if (descriptor.ImplementationFactory is not { } factory) return null;
        try { return factory(new UninitializedProvider())?.GetType(); }
        catch { return null; }
    }

    private sealed class UninitializedProvider : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType.IsAbstract || serviceType.IsInterface
                ? null
                : RuntimeHelpers.GetUninitializedObject(serviceType);
    }

    private sealed class NoopLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;
        public StaticOptionsMonitor(T value) { _value = value; }
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable OnChange(Action<T, string?> listener) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}
