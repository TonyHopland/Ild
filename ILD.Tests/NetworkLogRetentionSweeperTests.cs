using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

public class NetworkLogRetentionSweeperTests
{
    [Fact]
    public async Task Sweep_deletes_entries_past_the_window_and_keeps_the_rest()
    {
        using var db = new TestDb();
        var stale = SeedLine(db, "stale.example", DateTime.UtcNow.AddDays(-40));
        var recent = SeedLine(db, "recent.example", DateTime.UtcNow.AddDays(-2));

        var notifier = new Mock<INetworkNotifier>();
        await InvokeSweepOnceAsync(BuildSweeper(db, notifier.Object, retentionDays: 30));

        var remaining = await db.Network.GetLogAsync(100);
        Assert.Equal(new[] { recent }, remaining.Select(l => l.Id));
        Assert.DoesNotContain(stale, remaining.Select(l => l.Id));
    }

    [Fact]
    public async Task Sweep_keeps_an_entry_that_is_exactly_inside_the_window()
    {
        using var db = new TestDb();
        var edge = SeedLine(db, "edge.example", DateTime.UtcNow.AddDays(-30).AddMinutes(5));

        await InvokeSweepOnceAsync(BuildSweeper(db, new Mock<INetworkNotifier>().Object, retentionDays: 30));

        Assert.Equal(new[] { edge }, (await db.Network.GetLogAsync(100)).Select(l => l.Id));
    }

    [Fact]
    public async Task Sweep_disabled_when_retention_is_zero()
    {
        using var db = new TestDb();
        var ancient = SeedLine(db, "ancient.example", DateTime.UtcNow.AddDays(-400));

        var notifier = new Mock<INetworkNotifier>();
        await InvokeSweepOnceAsync(BuildSweeper(db, notifier.Object, retentionDays: 0));

        Assert.Equal(new[] { ancient }, (await db.Network.GetLogAsync(100)).Select(l => l.Id));
        notifier.Verify(n => n.LogClearedAsync(), Times.Never);
    }

    [Fact]
    public async Task Sweep_announces_only_when_it_removed_something()
    {
        using var db = new TestDb();
        SeedLine(db, "recent.example", DateTime.UtcNow.AddDays(-1));

        var notifier = new Mock<INetworkNotifier>();
        await InvokeSweepOnceAsync(BuildSweeper(db, notifier.Object, retentionDays: 30));
        notifier.Verify(n => n.LogClearedAsync(), Times.Never);

        SeedLine(db, "stale.example", DateTime.UtcNow.AddDays(-40));
        await InvokeSweepOnceAsync(BuildSweeper(db, notifier.Object, retentionDays: 30));
        notifier.Verify(n => n.LogClearedAsync(), Times.Once);
    }

    private static Guid SeedLine(TestDb db, string host, DateTime timestamp)
    {
        var entry = new NetworkLogEntry
        {
            Id = Guid.NewGuid(),
            Host = host,
            Port = 443,
            Timestamp = timestamp,
            Decision = NetworkDecision.Allowed,
        };
        db.Context.NetworkLogEntries.Add(entry);
        db.Context.SaveChanges();
        return entry.Id;
    }

    private static NetworkLogRetentionSweeper BuildSweeper(TestDb db, INetworkNotifier notifier, int retentionDays)
    {
        var settings = new Mock<ISchedulerSettingsService>();
        settings.Setup(s => s.GetNetworkLogRetentionDaysAsync(It.IsAny<CancellationToken>())).ReturnsAsync(retentionDays);

        var services = new ServiceCollection();
        services.AddSingleton(db.Network);
        services.AddSingleton(settings.Object);
        var provider = services.BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        return new NetworkLogRetentionSweeper(scopes, notifier, NullLogger<NetworkLogRetentionSweeper>.Instance);
    }

    private static Task InvokeSweepOnceAsync(NetworkLogRetentionSweeper sweeper) =>
        (Task)typeof(NetworkLogRetentionSweeper)
            .GetMethod("SweepOnceAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(sweeper, new object?[] { CancellationToken.None })!;
}
