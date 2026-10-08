using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Api.Hubs;
using ILD.Core.Services.Implementations;
using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static ILD.Tests.Integration.ChatScheduleTestHost;

namespace ILD.Tests.Integration;

/// <summary>
/// A schedule firing as an ordinary chat turn of its owner's, run by the real
/// scheduler pass and turn runner on a scripted agent: when it fires, what the
/// turn is given, which chat it lands in, and how each firing is recorded.
/// </summary>
public sealed class ChatScheduleFiringTests
{
    private static readonly string[] AllToolGroups = ["read", "write", "execute", "ild"];
    private const string Owner = "admin";

    [Fact]
    public async Task A_due_schedule_starts_a_turn_in_its_owners_chat_with_every_tool_group_and_its_context_block()
    {
        await using var host = await StartAsync("2026-07-06T05:59:00Z");
        var provider = await host.SeedProviderAsync("main", isDefault: true);
        var repo = await host.SeedRepositoryAsync("example-repo");
        await host.SeedRepositoryAsync("other-repo");
        const string prompt = "Review how the loops performed and file what needs fixing.";
        var id = await host.CreateAsync(Body(
            name: "Weekly retro", prompt: prompt, cron: "0 8 * * 1", timeZone: "Europe/Oslo",
            repositoryScope: "Selected", repositoryIds: [repo]));

        await host.RunDueAsync();
        Assert.Equal(JsonValueKind.Null, (await host.LastFiringAsync(id)).ValueKind);

        host.Clock.Set("2026-07-06T06:00:30Z");
        await host.RunDueAsync();
        var turn = await host.Adapter.NextTurnAsync();

        Assert.Equal(provider, turn.Provider.Id);
        Assert.Equal(AllToolGroups.ToHashSet(), turn.ToolAllowlist!.ToHashSet());
        Assert.EndsWith(prompt, turn.Prompt);
        var block = turn.Prompt[..^prompt.Length];
        Assert.Contains("Weekly retro", block);
        Assert.Matches(new Regex(@"2026-07-06[T ](06|08):00"), block);
        Assert.Matches(new Regex(@"\bnone\b", RegexOptions.IgnoreCase), block);
        Assert.Contains("example-repo", block);
        Assert.DoesNotContain("other-repo", block);

        var firing = await host.LastFiringEndedAsync(id, "Completed");
        var chatId = turn.ChatSessionId!.Value;
        Assert.Equal(chatId, firing.GetProperty("chatSessionId").GetGuid());
        Assert.Equal("Cron", firing.GetProperty("trigger").GetString(), ignoreCase: true);
        var firedAt = Utc(firing.GetProperty("firedAt"))!.Value;
        Assert.InRange(firedAt, ParseUtc("2026-07-06T06:00:00Z"), ParseUtc("2026-07-06T06:00:30Z"));
        var after = await host.GetAsync(id);
        Assert.Equal(chatId, after.GetProperty("latestChatSessionId").GetGuid());
        Assert.Equal(ParseUtc("2026-07-13T06:00:00Z"), Utc(after.GetProperty("nextFireAt")));

        // The owner's own chat, named after and marked with its schedule, holding the message as sent.
        var listed = (await host.ChatHistoryAsync()).Single(c => c.GetProperty("id").GetGuid() == chatId);
        Assert.Equal("Weekly retro", listed.GetProperty("name").GetString());
        Assert.Equal(id, listed.GetProperty("scheduleId").GetGuid());
        Assert.Equal("Weekly retro", listed.GetProperty("scheduleName").GetString());
        var chat = await host.ChatAsync(chatId);
        Assert.Equal(AllToolGroups.ToHashSet(), chat.GetProperty("tools").EnumerateArray().Select(t => t.GetString()!).ToHashSet());
        var userMessage = chat.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "user");
        Assert.Equal(turn.Prompt, userMessage.GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_new_scheduled_chat_reaches_the_owners_sidebar_before_its_turn_ends_and_every_change_hints_the_schedules_list()
    {
        await using var host = await StartAsync("2026-07-06T09:00:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var inbox = ChatHub.InboxGroup(Owner);
        int SchedulesHints(Guid scheduleId) => host.Hub.SentTo(inbox, "ChatSchedulesChanged")
            .Count(s => s.Payload.GetProperty("scheduleId").GetGuid() == scheduleId);

        var body = Body(continueSession: false);
        var id = await host.CreateAsync(body);
        await EventuallyAsync(() => Task.FromResult(SchedulesHints(id) >= 1), "a hint that the schedule was created");

        host.Adapter.Behaviour = ScriptedChatAdapter.Mode.Hang;
        var hintsBeforeFiring = SchedulesHints(id);
        var firing = await host.RunNowAsync(id);
        var turn = await host.Adapter.NextTurnAsync();
        var chatId = turn.ChatSessionId!.Value;
        Assert.Equal(chatId, firing.GetProperty("chatSessionId").GetGuid());
        Assert.True(IsOutcome(firing, "Running"), firing.ToString());

        await EventuallyAsync(() => Task.FromResult(host.Hub.SentTo(inbox, "ChatActivityChanged")
            .Any(s => s.Payload.GetProperty("chatSessionId").GetGuid() == chatId)), "the owner's inbox to hear of the new chat");
        Assert.Contains(await host.ChatHistoryAsync(), c => c.GetProperty("id").GetGuid() == chatId);
        await EventuallyAsync(() => Task.FromResult(SchedulesHints(id) > hintsBeforeFiring), "a hint that the firing started");
        Assert.Equal("Running", await host.FiringOutcomeAsync(FiringId(firing)));

        var hintsWhileRunning = SchedulesHints(id);
        host.Adapter.ReleaseHung();
        await host.LastFiringEndedAsync(id, "Completed");
        await EventuallyAsync(() => Task.FromResult(SchedulesHints(id) > hintsWhileRunning), "a hint that the firing ended");

        var hintsBeforeEdit = SchedulesHints(id);
        body["name"] = "Renamed";
        await host.UpdateAsync(id, body);
        await EventuallyAsync(() => Task.FromResult(SchedulesHints(id) > hintsBeforeEdit), "a hint that the schedule was edited");

        var hintsBeforeDelete = SchedulesHints(id);
        await host.DeleteAsync(id);
        await EventuallyAsync(() => Task.FromResult(SchedulesHints(id) > hintsBeforeDelete), "a hint that the schedule was deleted");
    }

    [Fact]
    public async Task Continue_on_keeps_one_chat_until_it_is_deleted_or_the_tag_changes_and_continue_off_starts_a_chat_per_firing()
    {
        await using var host = await StartAsync("2026-07-06T06:00:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var nightly = await host.SeedProviderAsync("nightly", isDefault: false, "nightly");
        var continued = Body(name: "Continued", continueSession: true, repositoryScope: "None");
        var continuing = await host.CreateAsync(continued);

        var first = await RunAndCompleteAsync(host, continuing);

        host.Clock.Set("2026-07-07T09:15:00Z");
        var second = await RunAndCompleteAsync(host, continuing);
        Assert.Equal(first.ChatSessionId, second.ChatSessionId);
        Assert.Contains("2026-07-06", second.Prompt);
        Assert.Contains("2026-07-07", second.Prompt);
        // The previous firing is named now, so "none" is the repository allowlist speaking.
        Assert.Matches(new Regex(@"\bnone\b", RegexOptions.IgnoreCase), second.Prompt);

        using (var deleted = await host.Client.DeleteAsync($"/api/v1/chat/{second.ChatSessionId}", TestContext.Current.CancellationToken))
            Assert.True(deleted.IsSuccessStatusCode);
        var afterDelete = await RunAndCompleteAsync(host, continuing);
        Assert.NotEqual(first.ChatSessionId, afterDelete.ChatSessionId);
        Assert.Equal(afterDelete.ChatSessionId, (await host.GetAsync(continuing)).GetProperty("latestChatSessionId").GetGuid());

        continued["aiTag"] = "nightly";
        await host.UpdateAsync(continuing, continued);
        var afterTagChange = await RunAndCompleteAsync(host, continuing);
        Assert.NotEqual(afterDelete.ChatSessionId, afterTagChange.ChatSessionId);
        Assert.Equal(nightly, afterTagChange.ProviderId);
        var again = await RunAndCompleteAsync(host, continuing);
        Assert.Equal(afterTagChange.ChatSessionId, again.ChatSessionId);

        var fresh = await host.CreateAsync(Body(name: "Fresh", continueSession: false));
        var one = await RunAndCompleteAsync(host, fresh);
        var two = await RunAndCompleteAsync(host, fresh);
        Assert.NotEqual(one.ChatSessionId, two.ChatSessionId);
        Assert.Equal(two.ChatSessionId, (await host.GetAsync(fresh)).GetProperty("latestChatSessionId").GetGuid());

        // Deleting a schedule takes its firings and keeps its chats, which lose their mark.
        await host.DeleteAsync(fresh);
        Assert.Equal(0, await host.WithDbAsync(db => db.Set<ChatScheduleFiring>().CountAsync(f => f.ScheduleId == fresh, TestContext.Current.CancellationToken)));
        Assert.True(await host.WithDbAsync(db => db.Set<ChatScheduleFiring>().AnyAsync(f => f.ScheduleId == continuing, TestContext.Current.CancellationToken)));
        var history = await host.ChatHistoryAsync();
        foreach (var chat in new[] { one.ChatSessionId, two.ChatSessionId })
        {
            var row = history.Single(c => c.GetProperty("id").GetGuid() == chat);
            Assert.True(row.GetProperty("scheduleId").ValueKind == JsonValueKind.Null, "a chat kept the mark of a deleted schedule");
        }
        Assert.Equal(continuing, history.Single(c => c.GetProperty("id").GetGuid() == again.ChatSessionId).GetProperty("scheduleId").GetGuid());
    }

    [Fact]
    public async Task While_paused_a_due_firing_is_skipped_and_unpausing_fires_it_once_then_the_cron_resumes()
    {
        await using var host = await StartAsync("2026-07-06T07:59:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var id = await host.CreateAsync(Body(cron: "0 8 * * 1", timeZone: "UTC", repositoryScope: "All"));
        await host.SetPausedAsync(true);
        Assert.True((await host.ListPageAsync()).GetProperty("schedulerPaused").GetBoolean());

        host.Clock.Set("2026-07-06T08:00:30Z");
        await host.RunDueAsync();
        var skipped = await host.LastFiringAsync(id);
        Assert.True(IsOutcome(skipped, "Skipped"), skipped.ToString());
        Assert.Contains("scheduler paused", Reason(skipped), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JsonValueKind.Null, skipped.GetProperty("chatSessionId").ValueKind);

        // A week later it is due again and still paused: skipped again, nothing runs.
        host.Clock.Set("2026-07-13T08:00:30Z");
        await host.RunDueAsync();
        var skippedAgain = await host.LastFiringAsync(id);
        Assert.True(IsOutcome(skippedAgain, "Skipped"), skippedAgain.ToString());
        Assert.NotEqual(FiringId(skipped), FiringId(skippedAgain));
        Assert.Equal(0, host.Adapter.ChatTurns);
        Assert.Empty(await host.ChatHistoryAsync());

        // Unpaused the next day, before its next time: it fires once now.
        host.Clock.Set("2026-07-14T10:00:00Z");
        await host.SetPausedAsync(false);
        Assert.False((await host.ListPageAsync()).GetProperty("schedulerPaused").GetBoolean());
        await host.RunDueAsync();
        var turn = await host.Adapter.NextTurnAsync();
        Assert.Matches(new Regex(@"\ball\b", RegexOptions.IgnoreCase), turn.Prompt);
        var fired = await host.LastFiringEndedAsync(id, "Completed");
        Assert.Equal(turn.ChatSessionId, fired.GetProperty("chatSessionId").GetGuid());
        Assert.Equal(ParseUtc("2026-07-20T08:00:00Z"), Utc((await host.GetAsync(id)).GetProperty("nextFireAt")));

        await host.RunDueAsync();
        Assert.Equal(FiringId(fired), FiringId(await host.LastFiringAsync(id)));
        Assert.Equal(1, host.Adapter.ChatTurns);
    }

    [Fact]
    public async Task After_downtime_an_overdue_schedule_fires_once_and_then_waits_for_its_next_time()
    {
        await using var host = await StartAsync("2026-07-06T06:00:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var id = await host.CreateAsync(Body(cron: "0 * * * *", timeZone: "UTC"));

        // Three days of hourly firings missed.
        host.Clock.Set("2026-07-09T06:30:00Z");
        await host.RunDueAsync();
        var turn = await host.Adapter.NextTurnAsync();
        var fired = await host.LastFiringEndedAsync(id, "Completed");
        Assert.Equal("Cron", fired.GetProperty("trigger").GetString(), ignoreCase: true);
        Assert.Equal(ParseUtc("2026-07-09T07:00:00Z"), Utc((await host.GetAsync(id)).GetProperty("nextFireAt")));
        await host.ChatIdleAsync(turn.ChatSessionId!.Value);

        await host.RunDueAsync();
        Assert.Equal(1, await host.WithDbAsync(db => db.Set<ChatScheduleFiring>().CountAsync(f => f.ScheduleId == id, TestContext.Current.CancellationToken)));
        Assert.Equal(1, host.Adapter.ChatTurns);

        var manual = await host.RunNowAsync(id);
        Assert.Equal("RunNow", manual.GetProperty("trigger").GetString(), ignoreCase: true);
    }

    [Fact]
    public async Task Run_now_fires_while_paused_or_disabled_without_moving_the_cron_and_skips_a_turn_still_running_or_a_busy_chat()
    {
        await using var host = await StartAsync("2026-07-06T09:00:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var body = Body(cron: "0 8 * * 1", timeZone: "UTC", continueSession: true, enabled: false);
        var id = await host.CreateAsync(body);
        await host.SetPausedAsync(true);

        // Disabled and paused, and it still runs, leaving the schedule without a next firing.
        var disabledRun = await RunAndCompleteAsync(host, id);
        Assert.Null(Utc((await host.GetAsync(id)).GetProperty("nextFireAt")));
        var chatId = disabledRun.ChatSessionId;
        body["enabled"] = true;
        await host.UpdateAsync(id, body);
        var nextFireAt = Utc((await host.GetAsync(id)).GetProperty("nextFireAt"));
        Assert.Equal(ParseUtc("2026-07-13T08:00:00Z"), nextFireAt);

        // Stopped by the owner from the chat.
        host.Adapter.Behaviour = ScriptedChatAdapter.Mode.Hang;
        var stoppedFiring = await host.RunNowAsync(id);
        var stoppedTurn = await host.Adapter.NextTurnAsync();
        Assert.Equal(chatId, stoppedTurn.ChatSessionId);
        Assert.Equal(chatId, stoppedFiring.GetProperty("chatSessionId").GetGuid());
        await InterruptAsync(host, chatId);
        await host.LastFiringEndedAsync(id, "Stopped");
        await host.ChatIdleAsync(chatId);

        // Still running: the next Run now is skipped and the running turn left alone.
        host.Clock.Set("2026-07-07T09:00:00Z");
        var running = await host.RunNowAsync(id);
        var runningTurn = await host.Adapter.NextTurnAsync();
        var turnsSoFar = host.Adapter.ChatTurns;
        host.Clock.Set("2026-07-07T09:30:00Z");
        var stillRunning = await host.RunNowAsync(id);
        Assert.True(IsOutcome(stillRunning, "Skipped"), stillRunning.ToString());
        Assert.Contains("still running", Reason(stillRunning), StringComparison.OrdinalIgnoreCase);
        Assert.False(runningTurn.Cancel.IsCancellationRequested);
        Assert.Equal(turnsSoFar, host.Adapter.ChatTurns);
        Assert.Equal("Running", await host.FiringOutcomeAsync(FiringId(running)));

        // Replaced by a message the owner sends: that firing is stopped.
        using (var sent = await host.Client.PostAsJsonAsync($"/api/v1/chat/{chatId}/messages", new { content = "Hold on, look at this instead." }, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        var ownersTurn = await host.Adapter.NextTurnAsync();
        await EventuallyAsync(async () => await host.FiringOutcomeAsync(FiringId(running)) == "Stopped", "the replaced firing to end Stopped");
        var ownersTurnId = (await host.ChatAsync(chatId)).GetProperty("activeTurnId").GetGuid();

        // The owner's turn is running in the schedule's chat: busy, recorded and answered at once.
        host.Clock.Set("2026-07-08T09:00:00Z");
        var busy = await host.RunNowAsync(id);
        Assert.True(IsOutcome(busy, "Skipped"), busy.ToString());
        Assert.Contains("chat busy", Reason(busy), StringComparison.OrdinalIgnoreCase);
        Assert.False(ownersTurn.Cancel.IsCancellationRequested);
        Assert.Equal(ownersTurnId, (await host.ChatAsync(chatId)).GetProperty("activeTurnId").GetGuid());

        host.Adapter.Behaviour = ScriptedChatAdapter.Mode.Succeed;
        host.Adapter.ReleaseHung();
        await host.ChatIdleAsync(chatId);

        // The previous firing is the last one that started a turn, not a skip.
        host.Clock.Set("2026-07-09T09:00:00Z");
        await host.RunNowAsync(id);
        var next = await host.Adapter.NextTurnAsync();
        Assert.Contains("2026-07-07", next.Prompt);
        Assert.DoesNotContain("2026-07-08", next.Prompt);
        await host.LastFiringEndedAsync(id, "Completed");

        Assert.Equal(nextFireAt, Utc((await host.GetAsync(id)).GetProperty("nextFireAt")));
    }

    [Fact]
    public async Task A_run_now_that_starts_a_turn_settles_a_firing_owed_from_a_pause()
    {
        await using var host = await StartAsync("2026-07-06T07:59:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var id = await host.CreateAsync(Body(cron: "0 8 * * 1", timeZone: "UTC", continueSession: false));
        await host.SetPausedAsync(true);
        host.Clock.Set("2026-07-06T08:00:30Z");
        await host.RunDueAsync();
        Assert.True(IsOutcome(await host.LastFiringAsync(id), "Skipped"));

        host.Clock.Set("2026-07-06T09:00:00Z");
        var manual = await RunAndCompleteAsync(host, id);

        await host.SetPausedAsync(false);
        await host.RunDueAsync();
        Assert.Equal(manual.FiringId, FiringId(await host.LastFiringAsync(id)));
        Assert.Equal(1, host.Adapter.ChatTurns);
        Assert.Equal(ParseUtc("2026-07-13T08:00:00Z"), Utc((await host.GetAsync(id)).GetProperty("nextFireAt")));
    }

    [Fact]
    public async Task A_firing_fails_when_its_turn_fails_and_when_its_provider_cannot_be_resolved_without_creating_a_chat()
    {
        await using var host = await StartAsync("2026-07-06T09:00:00Z");
        var nightly = await host.SeedProviderAsync("nightly", isDefault: false, "nightly");
        var id = await host.CreateAsync(Body(aiTag: "nightly", continueSession: false));

        host.Adapter.Behaviour = ScriptedChatAdapter.Mode.Fail;
        await host.RunNowAsync(id);
        var failedTurn = await host.Adapter.NextTurnAsync();
        var failed = await host.LastFiringEndedAsync(id, "Failed");
        Assert.False(string.IsNullOrWhiteSpace(Reason(failed)));
        await host.ChatIdleAsync(failedTurn.ChatSessionId!.Value);

        await host.DeleteProviderAsync(nightly);
        var chatsBefore = (await host.ChatHistoryAsync()).Length;
        var unresolved = await host.RunNowAsync(id);
        Assert.True(IsOutcome(unresolved, "Failed"), unresolved.ToString());
        Assert.Contains("no provider has tag 'nightly'", Reason(unresolved));
        Assert.Equal(JsonValueKind.Null, unresolved.GetProperty("chatSessionId").ValueKind);
        Assert.Equal(chatsBefore, (await host.ChatHistoryAsync()).Length);
        Assert.Equal(1, host.Adapter.ChatTurns);
    }

    [Fact]
    public async Task While_a_firing_is_starting_its_chat_another_run_now_and_edits_are_turned_away_without_waiting()
    {
        await using var host = await StartAsync("2026-07-06T10:00:00Z");
        var slow = await host.SeedProviderAsync("slow", isDefault: true);
        var body = Body(cron: "0 3 * * *", continueSession: false);
        var id = await host.CreateAsync(body);
        host.Registry.Block(slow);

        var first = host.PostRunAsync(id);
        await host.Registry.EnteredAsync(slow);

        using (var second = await host.PostRunAsync(id).WaitAsync(Patience, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        body["name"] = "Renamed while firing";
        using (var edit = await host.PutScheduleAsync(id, body).WaitAsync(Patience, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        using (var delete = await host.Client.DeleteAsync($"/api/v1/chat/schedules/{id}", TestContext.Current.CancellationToken).WaitAsync(Patience, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);

        host.Registry.Release(slow);
        using (var started = await first)
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        await host.LastFiringEndedAsync(id, "Completed");
        Assert.Equal(1, await host.WithDbAsync(db => db.Set<ChatScheduleFiring>().CountAsync(f => f.ScheduleId == id, TestContext.Current.CancellationToken)));
        Assert.Equal("Weekly retro", (await host.GetAsync(id)).GetProperty("name").GetString());
        Assert.Equal(1, host.Adapter.ChatTurns);
    }

    [Fact]
    public async Task After_a_restart_a_firing_left_running_ends_failed_and_its_chat_says_so_but_a_live_turn_is_never_touched()
    {
        await using var host = await StartAsync("2026-07-06T09:00:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var id = await host.CreateAsync(Body());
        host.Adapter.Behaviour = ScriptedChatAdapter.Mode.Hang;
        var running = await host.RunNowAsync(id);
        var turn = await host.Adapter.NextTurnAsync();
        var chatId = turn.ChatSessionId!.Value;

        // Recovery running in the process whose turn this is (the API can start a
        // turn before startup recovery is done) leaves it alone.
        await host.RecoverInterruptedAsync();
        var stillRunning = await host.LastFiringAsync(id);
        Assert.Equal(FiringId(running), FiringId(stillRunning));
        Assert.True(IsOutcome(stillRunning, "Running"), stillRunning.ToString());
        Assert.False(turn.Cancel.IsCancellationRequested);
        Assert.Single((await host.ChatAsync(chatId)).GetProperty("messages").EnumerateArray());

        // Another ILD starts on the same database, as one does after a crash, and
        // finds a firing whose turn nothing is running any more.
        await using var restarted = await RestartOnAsync(host, "2026-07-06T09:05:00Z");
        var scheduler = restarted.Factory.Services.GetRequiredService<ChatScheduleScheduler>();
        await scheduler.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var failed = await restarted.LastFiringWhenAsync(id, f => IsOutcome(f, "Failed"), "the cut-off firing to be failed");
            Assert.Equal(FiringId(running), FiringId(failed));
            Assert.Contains("restart", Reason(failed), StringComparison.OrdinalIgnoreCase);

            JsonElement note = default;
            await EventuallyAsync(async () =>
            {
                var messages = (await restarted.ChatAsync(chatId)).GetProperty("messages").EnumerateArray().ToArray();
                note = messages.LastOrDefault();
                return messages.Length > 1;
            }, "the note in the chat");
            Assert.Equal("assistant", note.GetProperty("role").GetString());
            Assert.True(note.GetProperty("interrupted").GetBoolean());
            Assert.Contains("restart", note.GetProperty("content").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    private sealed record CompletedRun(Guid FiringId, Guid ChatSessionId, string Prompt, Guid ProviderId);

    private static async Task<CompletedRun> RunAndCompleteAsync(ChatScheduleTestHost host, Guid scheduleId)
    {
        var turnsBefore = host.Adapter.ChatTurns;
        var firing = await host.RunNowAsync(scheduleId);
        var turn = await host.Adapter.NextTurnAsync();
        Assert.Equal(turnsBefore + 1, host.Adapter.ChatTurns);
        Assert.Equal(turn.ChatSessionId, firing.GetProperty("chatSessionId").GetGuid());
        await host.LastFiringWhenAsync(scheduleId,
            f => FiringId(f) == FiringId(firing) && IsOutcome(f, "Completed"),
            "the run-now firing to complete");
        await host.ChatIdleAsync(turn.ChatSessionId!.Value);
        return new CompletedRun(FiringId(firing), turn.ChatSessionId!.Value, turn.Prompt, turn.Provider.Id);
    }

    private static async Task InterruptAsync(ChatScheduleTestHost host, Guid chatId)
    {
        using var response = await host.Client.PostAsync($"/api/v1/chat/{chatId}/interrupt", null, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);
    }
}
