using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The opt-in deletion of inactive chats: off by default, and once on, every
/// unstarred chat whose newest message (or, with none, its creation) is older than
/// the window goes the way a manual delete does. A chat that is busy is skipped,
/// never interrupted, and one that came back to life or was starred after it was
/// picked is kept.
/// </summary>
public sealed class ChatInactivitySweeperTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly TestDb _db = new();
    private readonly Mock<IChatNotifier> _notifier = new();
    private readonly ChatLoopScratchpad _loopScratchpad = new();
    private readonly ChatCloneCoordinator _chatClones = new();
    private readonly string _scratchRoot = Path.Combine(Path.GetTempPath(), "ild-chat-inactivity-tests", Guid.NewGuid().ToString("N"));

    // The runner whose turns stand in for a user's: each holds until released.
    private readonly ChatTurnRunner _runner;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> _holdTurn = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationToken> _turnTokens = new();

    public ChatInactivitySweeperTests()
    {
        var turns = new Mock<IChatService>();
        turns.Setup(c => c.ExecuteTurnAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, Guid _, string message, string? _, string? _, CancellationToken ct) =>
            {
                _turnTokens[message] = ct;
                if (_holdTurn.TryGetValue(message, out var hold)) await hold.Task;
            });
        var runnerNotifier = new Mock<IChatNotifier>();
        var runnerServices = new ServiceCollection().AddScoped(_ => turns.Object).BuildServiceProvider();
        _runner = ActivatorUtilities.CreateInstance<ChatTurnRunner>(
            runnerServices, runnerNotifier.Object, NullLogger<ChatTurnRunner>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, true); } catch { }
    }

    private static DateTime DaysAgo(double days) => DateTime.UtcNow.AddDays(-days);

    private async Task TurnOnAsync(string? days = null)
    {
        await _db.Settings.UpsertAsync("chat.autoDeleteInactive", "true", TestContext.Current.CancellationToken);
        if (days is not null)
            await _db.Settings.UpsertAsync("chat.autoDeleteInactiveDays", days, TestContext.Current.CancellationToken);
    }

    private Guid SeedChat(
        string owner,
        DateTime createdAt,
        (string Role, DateTime At)[]? messages = null,
        bool favorite = false,
        Guid? scheduleId = null)
    {
        var id = Guid.NewGuid();
        var scratch = Path.Combine(_scratchRoot, id.ToString("N"));
        Directory.CreateDirectory(scratch);
        using var ctx = _db.Fresh();
        ctx.ChatSessions.Add(new ChatSession
        {
            Id = id,
            UserId = owner,
            AiProviderId = Guid.NewGuid(),
            ProviderType = "claude-code",
            ToolAllowlistCsv = "ild",
            ScratchPath = scratch,
            IsFavorite = favorite,
            ScheduleId = scheduleId,
            CreatedAt = createdAt,
        });
        var sequence = 0;
        foreach (var (role, at) in messages ?? [])
        {
            ctx.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(),
                ChatSessionId = id,
                Role = role,
                Content = $"{role} {sequence}",
                Sequence = sequence++,
                CreatedAt = at,
            });
        }
        ctx.SaveChanges();
        return id;
    }

    private Guid SeedChatLastActive(double daysAgo, string owner = "alice", bool favorite = false)
        => SeedChat(owner, DaysAgo(daysAgo + 30), [("user", DaysAgo(daysAgo + 1)), ("assistant", DaysAgo(daysAgo))], favorite);

    private HashSet<Guid> Remaining()
    {
        using var ctx = _db.Fresh();
        return ctx.ChatSessions.AsNoTracking().Select(c => c.Id).ToHashSet();
    }

    private ChatService NewChatService(AppDbContext ctx)
        => new(ctx, new ProviderStore(ctx), Mock.Of<IAgentAdapterRegistry>(), _notifier.Object,
            new ChatOptions { ScratchRoot = _scratchRoot }, new LoopRunStore(ctx), _loopScratchpad, _chatClones);

    private ChatInactivitySweeper BuildSweeper(IChatTurnRunner? runner = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.Fresh());
        services.AddScoped<IChatService>(sp => NewChatService(sp.GetRequiredService<AppDbContext>()));
        services.AddSingleton<ISchedulerSettingsService>(new SchedulerSettingsService(_db.Settings));
        services.AddSingleton(runner ?? _runner);
        services.AddSingleton(_notifier.Object);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return ActivatorUtilities.CreateInstance<ChatInactivitySweeper>(services.BuildServiceProvider());
    }

    private static Task SweepOnceAsync(ChatInactivitySweeper sweeper) =>
        ((Task)typeof(ChatInactivitySweeper)
            .GetMethod("SweepOnceAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
            .Invoke(sweeper, new object?[] { CancellationToken.None })!)
        .WaitAsync(Patience, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("maybe")]
    public async Task While_the_switch_is_off_or_unreadable_no_chat_is_deleted_however_old(string? stored)
    {
        if (stored is not null)
            await _db.Settings.UpsertAsync("chat.autoDeleteInactive", stored, TestContext.Current.CancellationToken);
        await _db.Settings.UpsertAsync("chat.autoDeleteInactiveDays", "1", TestContext.Current.CancellationToken);
        var ancient = SeedChatLastActive(4000);
        var empty = SeedChat("bob", DaysAgo(4000));

        await SweepOnceAsync(BuildSweeper());

        Assert.Equal(new HashSet<Guid> { ancient, empty }, Remaining());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("two weeks")]
    [InlineData("0")]
    [InlineData("-3")]
    public async Task With_no_readable_window_the_default_of_14_days_applies(string? days)
    {
        await TurnOnAsync(days);
        var stale = SeedChatLastActive(14.5);
        var recent = SeedChatLastActive(13.5);

        await SweepOnceAsync(BuildSweeper());

        Assert.Equal(new HashSet<Guid> { recent }, Remaining());
        Assert.DoesNotContain(stale, Remaining());
    }

    [Fact]
    public async Task Last_activity_is_the_newest_message_of_either_role_or_with_none_the_creation()
    {
        await TurnOnAsync("10");
        // Old chats whose newest message is recent: by the user, and by the assistant.
        var userSpokeLately = SeedChat("alice", DaysAgo(400), [("user", DaysAgo(300)), ("assistant", DaysAgo(300)), ("user", DaysAgo(2))]);
        var replyLately = SeedChat("alice", DaysAgo(400), [("user", DaysAgo(300)), ("assistant", DaysAgo(2))]);
        // A chat whose messages are all older than the window.
        var allOld = SeedChat("alice", DaysAgo(400), [("user", DaysAgo(11)), ("assistant", DaysAgo(11))]);
        // Chats with no messages count from their creation.
        var emptyOld = SeedChat("alice", DaysAgo(11));
        var emptyNew = SeedChat("alice", DaysAgo(9));

        await SweepOnceAsync(BuildSweeper());

        Assert.Equal(new HashSet<Guid> { userSpokeLately, replyLately, emptyNew }, Remaining());
        Assert.DoesNotContain(allOld, Remaining());
        Assert.DoesNotContain(emptyOld, Remaining());
    }

    [Fact]
    public async Task An_empty_chat_whose_creation_time_the_database_wrote_is_judged_by_it()
    {
        await TurnOnAsync("14");
        // StartAsync leaves CreatedAt to the column default, which SQLite writes in its own text form.
        using (var ctx = _db.Fresh())
        {
            foreach (var _ in new[] { 0, 1 })
            {
                ctx.ChatSessions.Add(new ChatSession
                {
                    Id = Guid.NewGuid(),
                    UserId = "alice",
                    AiProviderId = Guid.NewGuid(),
                    ProviderType = "claude-code",
                    ToolAllowlistCsv = "ild",
                    ScratchPath = Path.Combine(_scratchRoot, Guid.NewGuid().ToString("N")),
                });
            }
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var ids = Remaining().ToArray();
        var old = ids[0];
        var fresh = ids[1];
        await _db.Context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ChatSessions\" SET \"CreatedAt\" = datetime('now', '-20 days') WHERE \"Id\" = {old}", TestContext.Current.CancellationToken);
        await _db.Context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ChatSessions\" SET \"CreatedAt\" = datetime('now', '-1 hours') WHERE \"Id\" = {fresh}", TestContext.Current.CancellationToken);

        await SweepOnceAsync(BuildSweeper());

        Assert.Equal(new HashSet<Guid> { fresh }, Remaining());
    }

    [Fact]
    public async Task Renaming_reading_or_starring_and_unstarring_a_chat_is_not_activity()
    {
        await TurnOnAsync("14");
        var touched = SeedChatLastActive(20);
        using (var ctx = _db.Fresh())
        {
            var chat = NewChatService(ctx);
            Assert.True(await chat.RenameAsync("alice", touched, "Deploy loop wiring", TestContext.Current.CancellationToken));
            Assert.True(await chat.MarkReadAsync("alice", touched, 1, TestContext.Current.CancellationToken));
            Assert.True(await chat.SetFavoriteAsync("alice", touched, true, TestContext.Current.CancellationToken));
            Assert.True(await chat.SetFavoriteAsync("alice", touched, false, TestContext.Current.CancellationToken));
        }
        await _db.Context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ChatSessions\" SET \"UpdatedAt\" = {DateTime.UtcNow} WHERE \"Id\" = {touched}", TestContext.Current.CancellationToken);

        await SweepOnceAsync(BuildSweeper());

        Assert.DoesNotContain(touched, Remaining());
    }

    [Fact]
    public async Task A_favorite_is_kept_however_old_until_it_is_unstarred_and_then_goes_on_a_following_pass()
    {
        await TurnOnAsync("14");
        var favorite = SeedChatLastActive(4000, favorite: true);
        var sweeper = BuildSweeper();

        await SweepOnceAsync(sweeper);
        await SweepOnceAsync(sweeper);
        Assert.Contains(favorite, Remaining());

        using (var ctx = _db.Fresh())
            Assert.True(await NewChatService(ctx).SetFavoriteAsync("alice", favorite, false, TestContext.Current.CancellationToken));
        await SweepOnceAsync(sweeper);

        Assert.DoesNotContain(favorite, Remaining());
    }

    [Fact]
    public async Task Every_users_chats_and_scheduled_chats_are_deleted_like_a_manual_delete_and_announced_to_their_owner()
    {
        await TurnOnAsync("14");
        var schedule = new ChatSchedule
        {
            Id = Guid.NewGuid(),
            UserId = "bob",
            Name = "Nightly check",
            Prompt = "Check.",
            CronExpression = "0 3 * * *",
            TimeZone = "UTC",
            CreatedAt = DaysAgo(100),
        };
        using (var ctx = _db.Fresh())
        {
            ctx.ChatSchedules.Add(schedule);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var alices = SeedChatLastActive(30, "alice");
        var bobs = SeedChatLastActive(30, "bob");
        var scheduled = SeedChat("bob", DaysAgo(60), [("user", DaysAgo(40)), ("assistant", DaysAgo(40))], scheduleId: schedule.Id);
        var kept = SeedChatLastActive(1, "bob");
        using (var ctx = _db.Fresh())
        {
            ctx.ChatUsages.Add(new ChatUsage
            {
                Id = Guid.NewGuid(),
                ChatSessionId = alices,
                Kind = ChatUsageKind.Turn,
                AiProvider = "p1",
                InputTokens = 100,
                OutputTokens = 10,
                CostUsd = 0.10m,
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var analyticsBefore = await AnalyticsAsync();
        var scratch = (Path.Combine(_scratchRoot, alices.ToString("N")), Path.Combine(_scratchRoot, scheduled.ToString("N")));

        await SweepOnceAsync(BuildSweeper());

        Assert.Equal(new HashSet<Guid> { kept }, Remaining());
        using (var ctx = _db.Fresh())
            Assert.All(ctx.ChatMessages.AsNoTracking().ToList(), m => Assert.Equal(kept, m.ChatSessionId));
        Assert.False(Directory.Exists(scratch.Item1), "the deleted chat's scratch directory should be removed");
        Assert.False(Directory.Exists(scratch.Item2), "the deleted chat's scratch directory should be removed");
        Assert.True(Directory.Exists(Path.Combine(_scratchRoot, kept.ToString("N"))));

        var analyticsAfter = await AnalyticsAsync();
        Assert.Equal(100, analyticsBefore.TotalInputTokens);
        Assert.Equal(analyticsBefore.TotalInputTokens, analyticsAfter.TotalInputTokens);
        Assert.Equal(analyticsBefore.TotalCostUsd, analyticsAfter.TotalCostUsd);

        _notifier.Verify(n => n.UnreadChangedAsync("alice", alices), Times.AtLeastOnce);
        _notifier.Verify(n => n.UnreadChangedAsync("bob", bobs), Times.AtLeastOnce);
        _notifier.Verify(n => n.UnreadChangedAsync("bob", scheduled), Times.AtLeastOnce);
        _notifier.Verify(n => n.UnreadChangedAsync(It.IsAny<string>(), kept), Times.Never);
    }

    [Fact]
    public async Task The_switch_and_the_window_are_read_on_every_pass()
    {
        var chat = SeedChatLastActive(20);
        var sweeper = BuildSweeper();

        await SweepOnceAsync(sweeper);
        Assert.Contains(chat, Remaining());

        await TurnOnAsync("30");
        await SweepOnceAsync(sweeper);
        Assert.Contains(chat, Remaining());

        await _db.Settings.UpsertAsync("chat.autoDeleteInactive", "false", TestContext.Current.CancellationToken);
        await _db.Settings.UpsertAsync("chat.autoDeleteInactiveDays", "7", TestContext.Current.CancellationToken);
        await SweepOnceAsync(sweeper);
        Assert.Contains(chat, Remaining());

        await _db.Settings.UpsertAsync("chat.autoDeleteInactive", "true", TestContext.Current.CancellationToken);
        await SweepOnceAsync(sweeper);
        Assert.DoesNotContain(chat, Remaining());
    }

    [Fact]
    public async Task A_stored_window_past_the_ceiling_still_sweeps_instead_of_failing_every_pass()
    {
        // Only reachable by editing the row directly; the API refuses it.
        await TurnOnAsync("999999999");
        var ancient = SeedChatLastActive(4000);

        await SweepOnceAsync(BuildSweeper());

        Assert.DoesNotContain(ancient, Remaining());
    }

    [Fact]
    public async Task A_chat_with_a_turn_in_flight_is_skipped_not_interrupted_and_goes_once_it_is_idle()
    {
        await TurnOnAsync("14");
        var busy = SeedChatLastActive(30);
        var idle = SeedChatLastActive(30);
        var release = new TaskCompletionSource();
        _holdTurn["still thinking"] = release;
        await _runner.SubmitAsync(busy, "still thinking");
        await WaitUntilAsync(() => _turnTokens.ContainsKey("still thinking"));
        var sweeper = BuildSweeper();

        await SweepOnceAsync(sweeper);

        Assert.Equal(new HashSet<Guid> { busy }, Remaining());
        Assert.False(_turnTokens["still thinking"].IsCancellationRequested, "the pass interrupted the turn");
        Assert.NotNull(_runner.ActiveTurnId(busy));

        release.SetResult();
        await WaitUntilAsync(() => _runner.ActiveTurnCount == 0 && _runner.GateCount == 0);
        await SweepOnceAsync(sweeper);

        Assert.Empty(Remaining());
        Assert.DoesNotContain(idle, Remaining());
    }

    [Fact]
    public async Task A_chat_whose_gate_a_send_stop_or_delete_holds_is_skipped_without_waiting_for_it()
    {
        await TurnOnAsync("14");
        var held = SeedChatLastActive(30);
        var other = SeedChatLastActive(30);
        var gateHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource();
        var holder = _runner.DeleteAsync(held, async () => { gateHeld.TrySetResult(); await releaseGate.Task; });
        await gateHeld.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var sweeper = BuildSweeper();

        await SweepOnceAsync(sweeper);

        Assert.False(holder.IsCompleted, "the gate was let go before the pass ran, so this proves nothing");
        Assert.Equal(new HashSet<Guid> { held }, Remaining());

        releaseGate.SetResult();
        await holder;
        await SweepOnceAsync(sweeper);

        Assert.Empty(Remaining());
        Assert.DoesNotContain(other, Remaining());
    }

    [Fact]
    public async Task A_chat_that_gets_a_message_or_a_star_after_it_was_picked_is_kept()
    {
        await TurnOnAsync("14");
        var spokenTo = SeedChatLastActive(30);
        var starred = SeedChatLastActive(30);
        var untouched = SeedChatLastActive(30);
        // An idle runner that lets the chat change between the candidate query and
        // the delete it runs under the gate.
        var runner = new Mock<IChatTurnRunner>();
        runner.Setup(r => r.TryDeleteIfIdleAsync(It.IsAny<Guid>(), It.IsAny<Func<Task<bool>>>()))
            .Returns(async (Guid id, Func<Task<bool>> delete) =>
            {
                using (var ctx = _db.Fresh())
                {
                    if (id == spokenTo)
                    {
                        ctx.ChatMessages.Add(new ChatMessage
                        {
                            Id = Guid.NewGuid(),
                            ChatSessionId = id,
                            Role = "user",
                            Content = "still here",
                            Sequence = 10,
                            CreatedAt = DateTime.UtcNow,
                        });
                        await ctx.SaveChangesAsync();
                    }
                    else if (id == starred)
                    {
                        await ctx.ChatSessions.Where(c => c.Id == id)
                            .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsFavorite, true));
                    }
                }
                return await delete();
            });

        await SweepOnceAsync(BuildSweeper(runner.Object));

        Assert.Equal(new HashSet<Guid> { spokenTo, starred }, Remaining());
        Assert.DoesNotContain(untouched, Remaining());
    }

    [Fact]
    public async Task A_chat_that_fails_to_delete_is_kept_and_neither_the_rest_of_the_pass_nor_the_next_one_is_stopped()
    {
        await TurnOnAsync("14");
        var chats = Enumerable.Range(0, 4).Select(_ => SeedChatLastActive(30)).ToHashSet();
        Guid? failed = null;
        var runner = new Mock<IChatTurnRunner>();
        runner.Setup(r => r.TryDeleteIfIdleAsync(It.IsAny<Guid>(), It.IsAny<Func<Task<bool>>>()))
            .Returns(async (Guid id, Func<Task<bool>> delete) =>
            {
                // The first chat the pass reaches cannot be removed, as when its pi files cannot be.
                if (failed is null || failed == id)
                {
                    failed ??= id;
                    throw new IOException("ild.ts could not be removed");
                }
                return await delete();
            });
        var sweeper = BuildSweeper(runner.Object);

        await SweepOnceAsync(sweeper);

        Assert.NotNull(failed);
        Assert.Contains(failed!.Value, chats);
        Assert.Equal(new HashSet<Guid> { failed.Value }, Remaining());

        failed = Guid.Empty;
        await SweepOnceAsync(sweeper);
        Assert.Empty(Remaining());
    }

    private Task<RunAnalyticsOverview> AnalyticsAsync()
        => new RunAnalyticsService(_db.Fresh()).GetOverviewAsync(new AnalyticsQuery(), TestContext.Current.CancellationToken);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition never held");
            await Task.Yield();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}
