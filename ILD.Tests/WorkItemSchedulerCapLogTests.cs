using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Being at the concurrency cap with Ready work waiting is a state, not an
/// event: the coordinator reports it on every pass it holds, and the scheduler
/// — which owns the cadence, and drops to a 5s grace interval whenever anything
/// is parked at a human gate — decides how often to say so. It logs the
/// transitions: entering the state, and every change of who is holding the
/// slots.
/// </summary>
public class WorkItemSchedulerCapLogTests
{
    [Fact]
    public async Task Repeated_blocked_passes_announce_the_cap_once_and_then_trace_it()
    {
        var log = new RecordingLogger<WorkItemScheduler>();

        await RunPassesAsync(log, passes: 4, _ => Blocked("wi-1", "wi-2"));

        var line = Assert.Single(CapLines(log, LogLevel.Information));
        Assert.Contains("wi-1", line, StringComparison.Ordinal);
        Assert.Contains("wi-2", line, StringComparison.Ordinal);
        // The passes in between are still traceable, just not at Information.
        Assert.NotEmpty(CapLines(log, LogLevel.Debug));
    }

    [Fact]
    public async Task A_change_in_who_holds_the_slots_logs_again()
    {
        // The set changing is the interesting moment — it means the board moved
        // and is still stuck, which a once-only line would hide.
        var log = new RecordingLogger<WorkItemScheduler>();

        await RunPassesAsync(log, passes: 4, pass => pass < 2
            ? Blocked("wi-1", "wi-2")
            : Blocked("wi-2", "wi-3"));

        Assert.Equal(2, CapLines(log, LogLevel.Information).Count);
    }

    [Fact]
    public async Task Recovering_and_hitting_the_cap_again_logs_again()
    {
        // Otherwise the second stall is silent for as long as the process lives.
        var log = new RecordingLogger<WorkItemScheduler>();

        await RunPassesAsync(log, passes: 4, pass => pass == 1
            ? new PollCycleResult()
            : Blocked("wi-1"));

        Assert.Equal(2, CapLines(log, LogLevel.Information).Count);
    }

    [Fact]
    public async Task A_pass_that_is_not_blocked_says_nothing_about_the_cap()
    {
        // Slots can be full with an empty Ready queue — nothing is being held
        // up, so there is nothing to report.
        var log = new RecordingLogger<WorkItemScheduler>();

        await RunPassesAsync(log, passes: 3,
            _ => new PollCycleResult { SlotHolders = new[] { "wi-1" } });

        Assert.Empty(CapLines(log, LogLevel.Information));
        Assert.Empty(CapLines(log, LogLevel.Debug));
    }

    // ----- plumbing -----

    private static PollCycleResult Blocked(params string[] slotHolders)
        => new() { BlockedByCap = true, SlotHolders = slotHolders };

    private static List<string> CapLines(RecordingLogger<WorkItemScheduler> log, LogLevel level)
        => log.Messages
            .Where(m => m.Level == level && m.Text.Contains("cap", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Text)
            .ToList();

    /// <summary>
    /// Drives the scheduler until it has run at least <paramref name="passes"/>
    /// poll cycles, handing each one the result <paramref name="resultFor"/>
    /// returns for its zero-based index, then stops it. Extra passes may slip in
    /// before the stop lands, so every case here asserts on behaviour that is
    /// stable under more passes of the same shape.
    /// </summary>
    private static async Task RunPassesAsync(
        ILogger<WorkItemScheduler> log, int passes, Func<int, PollCycleResult> resultFor)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = 0;

        var coord = new Mock<IRemoteWorkItemCoordinator>();
        coord.Setup(c => c.RunPollCycleAsync(
                It.IsAny<WorkItemServerOptions>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var pass = next++;
                if (pass >= passes - 1) done.TrySetResult(true);
                return resultFor(Math.Min(pass, passes - 1));
            });

        var settings = new Mock<ISchedulerSettingsService>();
        settings.Setup(s => s.GetIsPausedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        settings.Setup(s => s.GetMaxConcurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);

        var services = new ServiceCollection();
        services.AddScoped<ISchedulerSettingsService>(_ => settings.Object);
        services.AddScoped<IRemoteWorkItemCoordinator>(_ => coord.Object);
        using var sp = services.BuildServiceProvider();

        var monitor = new StaticOptionsMonitor<WorkItemSchedulerOptions>(new WorkItemSchedulerOptions
        {
            Enabled = true,
            BaseUrl = "http://localhost",
            ApiKey = "k",
            PollInterval = TimeSpan.FromMilliseconds(20),
            GracePollInterval = TimeSpan.FromMilliseconds(20),
        });

        var scheduler = new WorkItemScheduler(
            sp.GetRequiredService<IServiceScopeFactory>(), monitor, log, TimeProvider.System);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await scheduler.StartAsync(cts.Token);
        var winner = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(5), cts.Token));
        await scheduler.StopAsync(CancellationToken.None);

        Assert.True(winner == done.Task, $"Scheduler ran only {next} of {passes} expected passes");
    }

    /// <summary>
    /// Records level as well as text: what distinguishes the announcement from
    /// the trace is the level, and only the Information one is on in production.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
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
