using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ILD.Tests;

public class WorkItemSchedulerPulseTests
{
    /// <summary>
    /// Regression: after several timer-driven passes, a Pulse() must still wake
    /// the scheduler near-instantly. Earlier the loser of the timer/pulse race
    /// was left parked in the SemaphoreSlim queue, so Pulse() releases were
    /// absorbed by orphans and the next pass had to wait for the timer.
    /// </summary>
    [Fact]
    public async Task Pulse_wakes_scheduler_after_several_timer_driven_passes()
    {
        // gate is replaced before each pulse so the assertion sees only the
        // pass triggered by that pulse, not a stale earlier completion.
        var gateRef = new GateRef();

        var coord = new Mock<IRemoteWorkItemCoordinator>();
        coord.Setup(c => c.RunPollCycleAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                gateRef.Current?.TrySetResult(true);
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
            // Short interval so several timer-driven passes happen quickly,
            // each parking a WaitAsync waiter on the semaphore (the bug).
            PollInterval = TimeSpan.FromMilliseconds(50),
        });

        var scheduler = new WorkItemScheduler(
            sp.GetRequiredService<IServiceScopeFactory>(),
            monitor,
            NullLogger<WorkItemScheduler>.Instance,
            TimeProvider.System);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await scheduler.StartAsync(cts.Token);

        // Drive ~5 timer-win passes to accumulate any orphan waiters.
        for (int i = 0; i < 5; i++)
            await WaitForOnePassAsync(gateRef, TimeSpan.FromSeconds(2));

        // Switch to a long interval so the next wait can only be ended by a
        // pulse — a timer-driven pass would mask the bug.
        monitor.Set(new WorkItemSchedulerOptions
        {
            Enabled = true,
            BaseUrl = "http://localhost",
            ApiKey = "k",
            PollInterval = TimeSpan.FromSeconds(30),
        });

        // Give the scheduler a moment to enter the long wait.
        await Task.Delay(150, cts.Token);

        // Pulse — without the fix this is consumed by an orphan and the next
        // pass waits the full 30s timer interval.
        var pulseTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateRef.Current = pulseTcs;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        scheduler.Pulse();
        var winner = await Task.WhenAny(pulseTcs.Task, Task.Delay(TimeSpan.FromSeconds(3), cts.Token));
        sw.Stop();

        Assert.True(winner == pulseTcs.Task,
            $"Pulse did not trigger a pass within 3s (took {sw.ElapsedMilliseconds}ms before timeout)");
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"Pulse-driven pass took {sw.ElapsedMilliseconds}ms; expected near-instant");

        await scheduler.StopAsync(CancellationToken.None);
    }

    private static async Task WaitForOnePassAsync(GateRef gateRef, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateRef.Current = tcs;
        var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        Assert.True(winner == tcs.Task, "Timed out waiting for a poll cycle pass");
    }

    private sealed class GateRef { public TaskCompletionSource<bool>? Current; }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private T _value;
        public StaticOptionsMonitor(T value) { _value = value; }
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public void Set(T value) => _value = value;
        public IDisposable OnChange(Action<T, string?> listener) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}
