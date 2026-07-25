using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using Microsoft.Extensions.DependencyInjection;
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
