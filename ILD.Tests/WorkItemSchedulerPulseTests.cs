using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ILD.Tests;

public class WorkItemSchedulerPulseTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Regression: after several timer-driven passes, a Pulse() must still wake
    /// the scheduler on its own. Earlier the loser of the timer/pulse race was
    /// left parked in the SemaphoreSlim queue, so Pulse() releases were absorbed
    /// by orphans and the next pass had to wait for the timer.
    /// </summary>
    [Fact]
    public async Task Pulse_wakes_scheduler_after_several_timer_driven_passes()
    {
        var passes = new SemaphoreSlim(0);
        var coord = new Mock<IRemoteWorkItemCoordinator>();
        coord.Setup(c => c.RunPollCycleAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                passes.Release();
                return new PollCycleResult();
            });

        var settings = new Mock<ISchedulerSettingsService>();
        settings.Setup(s => s.GetIsPausedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
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
            PollInterval = TimeSpan.FromSeconds(30),
        });

        var time = new ManualTimeProvider();
        var scheduler = new WorkItemScheduler(
            sp.GetRequiredService<IServiceScopeFactory>(),
            monitor,
            NullLogger<WorkItemScheduler>.Instance,
            time);

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            await PassAsync(passes);

            // Timer-driven passes, each of which used to leave its pulse waiter
            // parked on the semaphore (the bug).
            for (int i = 0; i < 5; i++)
            {
                await time.TimerCreatedAsync();
                time.Fire();
                await PassAsync(passes);
            }

            // The scheduler is waiting on a timer that is never fired from here on,
            // so only the pulse can end this wait — without the fix it is consumed
            // by an orphan and no pass follows.
            await time.TimerCreatedAsync();
            var firedBeforePulse = time.Fired;
            scheduler.Pulse();
            await PassAsync(passes);

            Assert.Equal(firedBeforePulse, time.Fired);

            // A Pulse() that arrives just after a timer win, while the cancelled
            // pulse wait may still be queued on the semaphore, must still get a pass
            // of its own. The scheduler re-reads its options right after the win, so
            // pulsing from that read puts the pulse in that gap.
            await time.TimerCreatedAsync();
            monitor.OnNextRead = scheduler.Pulse;
            time.Fire();
            var firedAfterWin = time.Fired;
            await PassAsync(passes);
            await PassAsync(passes);

            Assert.Equal(firedAfterWin, time.Fired);
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    private static async Task PassAsync(SemaphoreSlim passes)
        => Assert.True(await passes.WaitAsync(Patience), "the scheduler never ran the expected pass");

    /// <summary>
    /// A clock whose timers fire only when the test says so. The scheduler's wait is
    /// Task.Delay on this provider, so every timer it creates lands here.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _pending = new();
        private readonly SemaphoreSlim _created = new(0);
        private int _fired;

        public int Fired => Volatile.Read(ref _fired);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate) _pending.Add(timer);
            _created.Release();
            return timer;
        }

        public async Task TimerCreatedAsync()
            => Assert.True(await _created.WaitAsync(Patience), "the scheduler never started waiting on a timer");

        /// <summary>Fires every timer that is still pending.</summary>
        public void Fire()
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                due = _pending.ToList();
                _pending.Clear();
            }

            foreach (var timer in due)
            {
                Interlocked.Increment(ref _fired);
                timer.Invoke();
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate) _pending.Remove(timer);
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public void Invoke() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => owner.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private T _value;
        public StaticOptionsMonitor(T value) { _value = value; }
        public Action? OnNextRead;

        public T CurrentValue
        {
            get
            {
                Interlocked.Exchange(ref OnNextRead, null)?.Invoke();
                return _value;
            }
        }
        public T Get(string? name) => _value;
        public void Set(T value) => _value = value;
        public IDisposable OnChange(Action<T, string?> listener) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}
