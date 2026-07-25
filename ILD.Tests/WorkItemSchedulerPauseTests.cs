using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ILD.Tests;

public class WorkItemSchedulerPauseTests
{
    /// <summary>
    /// While paused the scheduler must still run the poll cycle — heartbeats,
    /// WaitingForIld resumes, and closing the runs behind items the server has
    /// finished all keep working — but it tells the coordinator not to
    /// auto-promote Ready items, by passing <c>claimReadyItems: false</c>.
    /// Previously a paused scheduler skipped the whole pass, which also froze
    /// every other side of the loop.
    /// </summary>
    [Fact]
    public async Task Paused_scheduler_still_polls_but_suppresses_ready_claims()
    {
        var claimReadyValues = new List<bool>();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var coord = new Mock<IRemoteWorkItemCoordinator>();
        coord.Setup(c => c.RunPollCycleAsync(
                It.IsAny<WorkItemServerOptions>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<WorkItemServerOptions, int, bool, CancellationToken>((_, _, claimReady, _) =>
            {
                claimReadyValues.Add(claimReady);
                tcs.TrySetResult(true);
            })
            .ReturnsAsync(new PollCycleResult());

        var settings = new Mock<ISchedulerSettingsService>();
        settings.Setup(s => s.GetIsPausedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        settings.Setup(s => s.GetMaxConcurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(5);

        var services = new ServiceCollection();
        services.AddScoped<ISchedulerSettingsService>(_ => settings.Object);
        services.AddScoped<IRemoteWorkItemCoordinator>(_ => coord.Object);
        var sp = services.BuildServiceProvider();

        var monitor = new StaticOptionsMonitor<WorkItemSchedulerOptions>(new WorkItemSchedulerOptions
        {
            Enabled = true,
            BaseUrl = "http://localhost",
            ApiKey = "k",
            PollInterval = TimeSpan.FromMilliseconds(50),
        });

        var scheduler = new WorkItemScheduler(
            sp.GetRequiredService<IServiceScopeFactory>(),
            monitor,
            NullLogger<WorkItemScheduler>.Instance,
            TimeProvider.System);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await scheduler.StartAsync(cts.Token);

        var winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(3), cts.Token));
        await scheduler.StopAsync(CancellationToken.None);

        Assert.True(winner == tcs.Task, "Paused scheduler never invoked the poll cycle");
        Assert.All(claimReadyValues, v => Assert.False(v));
    }

    /// <summary>
    /// The observability AC lives across two objects — the pass decides it is
    /// blocked and by whom, <see cref="CapStallReporter"/> decides how loudly to
    /// say so — and each half is covered on its own. This is the joint: without
    /// it the scheduler could stop calling the reporter and every other test
    /// would still pass while the signal disappeared.
    /// </summary>
    [Fact]
    public async Task Scheduler_reports_a_pass_blocked_by_the_concurrency_cap()
    {
        var log = new RecordingLogger();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var coord = new Mock<IRemoteWorkItemCoordinator>();
        coord.Setup(c => c.RunPollCycleAsync(
                It.IsAny<WorkItemServerOptions>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                tcs.TrySetResult(true);
                return new PollCycleResult { BlockedByCap = true, SlotHolders = new[] { "wi-1" } };
            });

        var settings = new Mock<ISchedulerSettingsService>();
        settings.Setup(s => s.GetIsPausedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        settings.Setup(s => s.GetMaxConcurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var services = new ServiceCollection();
        services.AddScoped<ISchedulerSettingsService>(_ => settings.Object);
        services.AddScoped<IRemoteWorkItemCoordinator>(_ => coord.Object);
        using var sp = services.BuildServiceProvider();

        var monitor = new StaticOptionsMonitor<WorkItemSchedulerOptions>(new WorkItemSchedulerOptions
        {
            Enabled = true,
            BaseUrl = "http://localhost",
            ApiKey = "k",
            PollInterval = TimeSpan.FromMilliseconds(50),
        });

        var scheduler = new WorkItemScheduler(
            sp.GetRequiredService<IServiceScopeFactory>(), monitor, log, TimeProvider.System);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await scheduler.StartAsync(cts.Token);
        var winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(3), cts.Token));
        await scheduler.StopAsync(CancellationToken.None);

        Assert.True(winner == tcs.Task, "Scheduler never invoked the poll cycle");
        Assert.Contains(log.Messages, m =>
            m.Level == LogLevel.Information &&
            m.Text.Contains("wi-1", StringComparison.Ordinal) &&
            m.Text.Contains("cap", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RecordingLogger : ILogger<WorkItemScheduler>
    {
        private readonly List<(LogLevel Level, string Text)> _messages = new();
        private readonly Lock _gate = new();

        /// <summary>Snapshot — the scheduler writes from its own loop task.</summary>
        public IReadOnlyList<(LogLevel Level, string Text)> Messages
        {
            get { lock (_gate) return _messages.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _messages.Add((logLevel, formatter(state, exception)));
        }
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
