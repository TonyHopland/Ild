using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Remote;
using Microsoft.Extensions.DependencyInjection;
using static ILD.Tests.Integration.ChatScheduleTestHost;

namespace ILD.Tests.Integration;

/// <summary>
/// A schedule firing as an ordinary chat turn of its owner's, run by the real
/// scheduler and turn runner on a scripted agent: when it fires, what the turn is
/// given, how each firing is recorded, and what it is credited with.
/// </summary>
public sealed class ChatScheduleFiringTests
{
    private static readonly string[] AllToolGroups = ["read", "write", "execute", "ild"];

    [Fact]
    public async Task A_due_schedule_starts_a_turn_for_its_owner_with_every_tool_group_and_its_context_block()
    {
        await using var host = await StartAsync("2026-07-06T05:59:00Z");
        var provider = await host.SeedProviderAsync("main", isDefault: true);
        var repo = await host.SeedRepositoryAsync("example-repo");
        const string prompt = "Review how the loops performed and file what needs fixing.";
        var schedule = await host.CreateAsync(Body(
            name: "Weekly retro", prompt: prompt, cron: "0 8 * * 1", timeZone: "Europe/Oslo",
            repositoryScope: "selected", repositoryIds: [repo]));
        var id = schedule.GetProperty("id").GetGuid();
        Assert.Equal(ParseUtc("2026-07-06T06:00:00Z"), Utc(schedule.GetProperty("nextFireAt")));

        host.Clock.Set("2026-07-06T06:00:30Z");
        host.Pulse();
        var turn = await host.Adapter.NextTurnAsync();

        Assert.Equal(provider, turn.Provider.Id);
        Assert.Equal(AllToolGroups.ToHashSet(), turn.ToolAllowlist!.ToHashSet());
        Assert.NotNull(turn.ChatTurnId);
        Assert.EndsWith(prompt, turn.Prompt);
        var block = turn.Prompt[..^prompt.Length];
        Assert.Contains("Weekly retro", block);
        Assert.Matches(new Regex(@"2026-07-06[T ](06|08):00"), block);
        Assert.Matches(new Regex(@"\b(none|never)\b", RegexOptions.IgnoreCase), block);
        Assert.True(block.Contains("example-repo") || block.Contains(repo.ToString()), "the block does not name the allowed repository");

        var firing = await host.LastFiringEndedAsync(id, "Completed");
        var chatId = turn.ChatSessionId!.Value;
        Assert.Equal(chatId, firing.GetProperty("chatSessionId").GetGuid());
        var after = await host.GetAsync(id);
        Assert.Equal(chatId, after.GetProperty("latestChatSessionId").GetGuid());
        Assert.Equal(ParseUtc("2026-07-13T06:00:00Z"), Utc(after.GetProperty("nextFireAt")));

        // The owner's own chat, marked with its schedule, holding the message as sent.
        var listed = (await host.ChatHistoryAsync()).Single(c => c.GetProperty("id").GetGuid() == chatId);
        Assert.Equal(id, listed.GetProperty("scheduleId").GetGuid());
        Assert.Equal("Weekly retro", listed.GetProperty("scheduleName").GetString());
        var userMessage = (await host.ChatAsync(chatId)).GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "user");
        Assert.Equal(turn.Prompt, userMessage.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Continue_on_keeps_one_chat_until_it_is_deleted_or_the_tag_changes_and_continue_off_starts_a_chat_per_firing()
    {
        await using var host = await StartAsync("2026-07-06T06:00:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var nightly = await host.SeedProviderAsync("nightly", isDefault: false, "nightly");
        var continued = Body(name: "Continued", continueSession: true);
        var continuing = (await host.CreateAsync(continued)).GetProperty("id").GetGuid();

        var first = await RunAndCompleteAsync(host, continuing);
        Assert.Matches(new Regex(@"\b(none|never)\b", RegexOptions.IgnoreCase), first.Prompt);

        host.Clock.Set("2026-07-07T09:15:00Z");
        var second = await RunAndCompleteAsync(host, continuing);
        Assert.Equal(first.ChatSessionId, second.ChatSessionId);
        Assert.Contains("2026-07-06", second.Prompt);
        Assert.Contains("2026-07-07", second.Prompt);

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

        var fresh = (await host.CreateAsync(Body(name: "Fresh", continueSession: false))).GetProperty("id").GetGuid();
        var one = await RunAndCompleteAsync(host, fresh);
        var two = await RunAndCompleteAsync(host, fresh);
        Assert.NotEqual(one.ChatSessionId, two.ChatSessionId);
        Assert.Equal(two.ChatSessionId, (await host.GetAsync(fresh)).GetProperty("latestChatSessionId").GetGuid());

        // Deleting a schedule keeps its chats, which lose their mark.
        using (var deleted = await host.Client.DeleteAsync($"/api/v1/chat/schedules/{fresh}", TestContext.Current.CancellationToken))
            Assert.True(deleted.IsSuccessStatusCode);
        var history = await host.ChatHistoryAsync();
        foreach (var chat in new[] { one.ChatSessionId, two.ChatSessionId })
        {
            var row = history.Single(c => c.GetProperty("id").GetGuid() == chat);
            Assert.True(row.GetProperty("scheduleId").ValueKind == JsonValueKind.Null, "a chat kept the mark of a deleted schedule");
        }
        Assert.Equal(continuing, history.Single(c => c.GetProperty("id").GetGuid() == again.ChatSessionId).GetProperty("scheduleId").GetGuid());
    }

    [Fact]
    public async Task While_paused_a_due_firing_is_skipped_and_unpausing_fires_it_once_that_day_then_the_cron_resumes()
    {
        await using var host = await StartAsync("2026-07-06T07:59:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var id = (await host.CreateAsync(Body(cron: "0 8 * * 1", timeZone: "UTC"))).GetProperty("id").GetGuid();
        await host.SetPausedAsync(true);

        host.Clock.Set("2026-07-06T08:00:30Z");
        host.Pulse();
        var skipped = await host.LastFiringEndedAsync(id, "Skipped");
        Assert.Contains("paused", Reason(skipped), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, host.Adapter.ChatTurns);

        // A week later it is due again and still paused: skipped again, nothing runs.
        host.Clock.Set("2026-07-13T08:00:30Z");
        host.Pulse();
        await host.LastFiringWhenAsync(id, f => IsOutcome(f, "Skipped") && Utc(f.GetProperty("firedAt")) >= ParseUtc("2026-07-13T08:00:00Z"),
            "the second paused skip");
        Assert.Equal(0, host.Adapter.ChatTurns);

        // Unpaused the next day: the settings change alone brings the scheduler round.
        host.Clock.Set("2026-07-14T10:00:00Z");
        await host.SetPausedAsync(false);
        var turn = await host.Adapter.NextTurnAsync();
        var fired = await host.LastFiringEndedAsync(id, "Completed");
        Assert.Equal(turn.ChatSessionId, fired.GetProperty("chatSessionId").GetGuid());
        Assert.Equal(1, host.Adapter.ChatTurns);
        Assert.Equal(ParseUtc("2026-07-20T08:00:00Z"), Utc((await host.GetAsync(id)).GetProperty("nextFireAt")));

        host.Pulse();
        await AssertNextPassStartsNothingAsync(host, id);
    }

    [Fact]
    public async Task After_downtime_an_overdue_schedule_fires_once_and_then_waits_for_its_next_time()
    {
        await using var host = await StartAsync("2026-07-06T06:00:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var id = (await host.CreateAsync(Body(cron: "0 * * * *", timeZone: "UTC"))).GetProperty("id").GetGuid();

        // Three days of hourly firings missed.
        host.Clock.Set("2026-07-09T06:30:00Z");
        host.Pulse();
        var turn = await host.Adapter.NextTurnAsync();
        var fired = await host.LastFiringEndedAsync(id, "Completed");
        Assert.Equal(ParseUtc("2026-07-09T07:00:00Z"), Utc((await host.GetAsync(id)).GetProperty("nextFireAt")));
        await host.ChatIdleAsync(turn.ChatSessionId!.Value);

        host.Pulse();
        await AssertNextPassStartsNothingAsync(host, id);

        // A firing says whether the schedule or a person started it.
        var manual = await host.RunNowAsync(id);
        Assert.False(string.IsNullOrEmpty(fired.GetProperty("trigger").GetString()));
        Assert.False(string.IsNullOrEmpty(manual.GetProperty("trigger").GetString()));
        Assert.NotEqual(fired.GetProperty("trigger").GetString(), manual.GetProperty("trigger").GetString());
    }

    [Fact]
    public async Task Run_now_fires_while_paused_leaves_the_next_firing_alone_and_skips_a_busy_chat()
    {
        await using var host = await StartAsync("2026-07-06T09:00:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var id = (await host.CreateAsync(Body(cron: "0 8 * * 1", timeZone: "UTC", continueSession: true))).GetProperty("id").GetGuid();
        var nextFireAt = Utc((await host.GetAsync(id)).GetProperty("nextFireAt"));
        await host.SetPausedAsync(true);
        host.Adapter.Behaviour = ScriptedChatAdapter.Mode.Hang;

        // Stopped by the owner.
        var stoppedFiring = await host.RunNowAsync(id);
        var stoppedTurn = await host.Adapter.NextTurnAsync();
        Assert.Equal(stoppedTurn.ChatSessionId, stoppedFiring.GetProperty("chatSessionId").GetGuid());
        Assert.False(IsOutcome(stoppedFiring, "Skipped"));
        await InterruptAsync(host, stoppedTurn.ChatSessionId!.Value);
        await host.LastFiringEndedAsync(id, "Stopped");

        host.Clock.Set("2026-07-07T09:00:00Z");
        var running = await host.RunNowAsync(id);
        var runningTurn = await host.Adapter.NextTurnAsync();
        Assert.Equal(stoppedTurn.ChatSessionId, runningTurn.ChatSessionId);

        // Busy: recorded and answered at once, and the running turn is left alone.
        host.Clock.Set("2026-07-08T09:00:00Z");
        var busy = await host.RunNowAsync(id);
        Assert.True(IsOutcome(busy, "Skipped"), busy.ToString());
        Assert.Contains("busy", Reason(busy), StringComparison.OrdinalIgnoreCase);
        Assert.False(runningTurn.Cancel.IsCancellationRequested);
        Assert.Equal(2, host.Adapter.ChatTurns);
        Assert.Equal(runningTurn.ChatTurnId, (await host.ChatAsync(runningTurn.ChatSessionId!.Value)).GetProperty("activeTurnId").GetGuid());

        host.Adapter.Behaviour = ScriptedChatAdapter.Mode.Succeed;
        host.Adapter.ReleaseHung();
        await host.ChatIdleAsync(runningTurn.ChatSessionId!.Value);

        // The previous firing is the last one that started a turn, not the skip.
        host.Clock.Set("2026-07-09T09:00:00Z");
        await host.RunNowAsync(id);
        var next = await host.Adapter.NextTurnAsync();
        Assert.Contains("2026-07-07", next.Prompt);
        Assert.DoesNotContain("2026-07-08", next.Prompt);
        await host.LastFiringEndedAsync(id, "Completed");

        Assert.Equal(nextFireAt, Utc((await host.GetAsync(id)).GetProperty("nextFireAt")));
        Assert.Equal(ParseUtc("2026-07-13T08:00:00Z"), nextFireAt);
    }

    [Fact]
    public async Task Each_firing_ends_failed_when_its_turn_fails_or_loses_its_chat_or_provider()
    {
        await using var host = await StartAsync("2026-07-06T09:00:00Z");
        var provider = await host.SeedProviderAsync("main", isDefault: true);
        var id = (await host.CreateAsync(Body(continueSession: true))).GetProperty("id").GetGuid();

        host.Adapter.Behaviour = ScriptedChatAdapter.Mode.Fail;
        await host.RunNowAsync(id);
        var failedTurn = await host.Adapter.NextTurnAsync();
        await host.LastFiringEndedAsync(id, "Failed");
        await host.ChatIdleAsync(failedTurn.ChatSessionId!.Value);

        host.Adapter.Behaviour = ScriptedChatAdapter.Mode.Hang;
        await host.RunNowAsync(id);
        var hung = await host.Adapter.NextTurnAsync();
        using (var deleted = await host.Client.DeleteAsync($"/api/v1/chat/{hung.ChatSessionId}", TestContext.Current.CancellationToken))
            Assert.True(deleted.IsSuccessStatusCode);
        var lostChat = await host.LastFiringWhenAsync(id, f => !IsOutcome(f, "Running"), "the firing whose chat was deleted to end");
        Assert.True(IsOutcome(lostChat, "Failed"), lostChat.ToString());
        Assert.False(string.IsNullOrWhiteSpace(Reason(lostChat)));

        host.Adapter.Behaviour = ScriptedChatAdapter.Mode.Succeed;
        await host.RunNowAsync(id);
        var completedTurn = await host.Adapter.NextTurnAsync();
        await host.LastFiringEndedAsync(id, "Completed");
        await host.ChatIdleAsync(completedTurn.ChatSessionId!.Value);

        await host.DeleteProviderAsync(provider);
        await host.RunNowAsync(id);
        var noProvider = await host.LastFiringWhenAsync(id, f => !IsOutcome(f, "Running"), "the firing without a provider to end");
        Assert.True(IsOutcome(noProvider, "Failed"), noProvider.ToString());
    }

    [Fact]
    public async Task A_firing_held_up_creating_its_chat_delays_no_other_schedule_and_never_undoes_an_edit()
    {
        await using var host = await StartAsync("2026-07-06T10:00:00Z");
        var main = await host.SeedProviderAsync("main", isDefault: true);
        var slow = await host.SeedProviderAsync("slow", isDefault: false, "slow");
        var held = Body(name: "Held", aiTag: "slow", cron: "* * * * *");
        var heldId = (await host.CreateAsync(held)).GetProperty("id").GetGuid();
        var free = Body(name: "Free", cron: "* * * * *");
        var freeId = (await host.CreateAsync(free)).GetProperty("id").GetGuid();
        host.Registry.Block(slow);

        host.Clock.Set("2026-07-06T10:01:30Z");
        host.Pulse();
        await host.Registry.EnteredAsync(slow);

        var turn = await host.Adapter.NextTurnAsync();
        Assert.Equal(main, turn.Provider.Id);
        await host.LastFiringEndedAsync(freeId, "Completed");
        free["name"] = "Free, renamed";
        await host.UpdateAsync(freeId, free).WaitAsync(Patience, TestContext.Current.CancellationToken);

        // Re-timed while its own firing is still under way: the edit is what stays.
        held["cronExpression"] = "0 3 * * *";
        var edit = host.UpdateAsync(heldId, held);
        host.Registry.Release(slow);
        await edit;
        await host.LastFiringEndedAsync(heldId, "Completed");
        var after = await host.GetAsync(heldId);
        Assert.Equal("0 3 * * *", after.GetProperty("cronExpression").GetString());
        Assert.Equal(ParseUtc("2026-07-07T03:00:00Z"), Utc(after.GetProperty("nextFireAt")));
    }

    [Fact]
    public async Task On_startup_a_firing_still_running_ends_failed_and_its_chat_says_a_restart_cut_it_off()
    {
        await using var host = await StartAsync("2026-07-06T09:00:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var id = (await host.CreateAsync(Body())).GetProperty("id").GetGuid();
        host.Adapter.Behaviour = ScriptedChatAdapter.Mode.Hang;
        await host.RunNowAsync(id);
        var turn = await host.Adapter.NextTurnAsync();
        var chatId = turn.ChatSessionId!.Value;

        // A scheduler starting on this database finds a firing left Running, as one
        // starting after a crash does.
        var restarted = ActivatorUtilities.CreateInstance<ChatScheduleScheduler>(host.Factory.Services);
        await restarted.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var failed = await host.LastFiringWhenAsync(id, f => IsOutcome(f, "Failed"), "the interrupted firing to be failed");
            Assert.Contains("restart", Reason(failed), StringComparison.OrdinalIgnoreCase);

            JsonElement note = default;
            await EventuallyAsync(async () =>
            {
                var messages = (await host.ChatAsync(chatId)).GetProperty("messages").EnumerateArray().ToArray();
                note = messages.LastOrDefault();
                return messages.Length > 1 && note.GetProperty("role").GetString() == "assistant";
            }, "the interrupted note in the chat");
            Assert.True(note.GetProperty("interrupted").GetBoolean());
            Assert.Contains("restart", note.GetProperty("content").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await restarted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_firing_is_credited_with_exactly_the_items_its_own_turn_created()
    {
        await using var host = await StartAsync("2026-07-06T09:00:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var repo = await host.SeedRepositoryAsync("example-repo");
        var id = (await host.CreateAsync(Body(continueSession: true))).GetProperty("id").GetGuid();
        host.Adapter.Behaviour = ScriptedChatAdapter.Mode.Hang;

        await host.RunNowAsync(id);
        var stopped = await host.Adapter.NextTurnAsync();
        var chatId = stopped.ChatSessionId!.Value;
        await InterruptAsync(host, chatId);
        await host.LastFiringEndedAsync(id, "Stopped");

        await host.RunNowAsync(id);
        var current = await host.Adapter.NextTurnAsync();
        Assert.Equal(chatId, current.ChatSessionId);
        Assert.NotEqual(stopped.ChatTurnId, current.ChatTurnId);

        var credited = await CreateItemAsync(host, repo, chatId, current.ChatTurnId, HttpStatusCode.Created);
        // A late create from the stopped turn, one naming no turn, and one naming a turn that never was.
        await CreateItemAsync(host, repo, chatId, stopped.ChatTurnId, HttpStatusCode.Created);
        await CreateItemAsync(host, repo, chatId, null, HttpStatusCode.Created);
        await CreateItemAsync(host, repo, chatId, Guid.NewGuid(), HttpStatusCode.Created);
        // The right turn's id from another chat.
        await CreateItemAsync(host, repo, Guid.NewGuid(), current.ChatTurnId, HttpStatusCode.Created);

        // Refused outright: nothing was created, so nothing is recorded.
        host.Faults.Next = WorkItemServerFaults.Fault.RefusedBeforeCreating;
        await CreateItemAsync(host, repo, chatId, current.ChatTurnId, expected: null);
        // The answer lost after the item was made: counted, never shown as an item.
        host.Faults.Next = WorkItemServerFaults.Fault.LostAfterCreating;
        await CreateItemAsync(host, repo, chatId, current.ChatTurnId, expected: null);

        host.Adapter.ReleaseHung();
        var firing = await host.LastFiringEndedAsync(id, "Completed");
        Assert.Equal([credited], firing.GetProperty("createdWorkItemIds").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1, firing.GetProperty("unresolvedItems").GetInt32());
    }

    private static async Task<string?> CreateItemAsync(ChatScheduleTestHost host, Guid repo, Guid chatId, Guid? turnId, HttpStatusCode? expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/workitems")
        {
            Content = JsonContent.Create(new { title = "Finding", description = "found by a check", repositoryId = repo.ToString() }),
        };
        request.Headers.Add("X-ILD-Chat-Session-Id", chatId.ToString());
        if (turnId is { } turn) request.Headers.Add("X-ILD-Chat-Turn-Id", turn.ToString());
        using var response = await host.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        if (expected is null)
        {
            Assert.False(response.IsSuccessStatusCode, $"a failed create answered {(int)response.StatusCode}");
            return null;
        }
        Assert.True(response.StatusCode == expected, $"create answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("id").GetString();
    }

    private static async Task<AgentTurn> RunAndCompleteAsync(ChatScheduleTestHost host, Guid scheduleId)
    {
        var turnsBefore = host.Adapter.ChatTurns;
        var firing = await host.RunNowAsync(scheduleId);
        var turn = await host.Adapter.NextTurnAsync();
        Assert.Equal(turnsBefore + 1, host.Adapter.ChatTurns);
        Assert.Equal(turn.ChatSessionId, firing.GetProperty("chatSessionId").GetGuid());
        await host.LastFiringWhenAsync(scheduleId,
            f => f.GetProperty("id").GetGuid() == firing.GetProperty("id").GetGuid() && IsOutcome(f, "Completed"),
            "the run-now firing to complete");
        await host.ChatIdleAsync(turn.ChatSessionId!.Value);
        return new AgentTurn(turn.ChatSessionId!.Value, turn.Prompt, turn.Provider.Id);
    }

    private sealed record AgentTurn(Guid ChatSessionId, string Prompt, Guid ProviderId);

    private static async Task InterruptAsync(ChatScheduleTestHost host, Guid chatId)
    {
        using var response = await host.Client.PostAsync($"/api/v1/chat/{chatId}/interrupt", null, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);
    }

    /// <summary>
    /// A pass that has nothing due: proven to have run by a second schedule, made
    /// due for it, firing in that same pass.
    /// </summary>
    private static async Task AssertNextPassStartsNothingAsync(ChatScheduleTestHost host, Guid scheduleId)
    {
        var before = host.Adapter.ChatTurns;
        var lastBefore = (await host.GetAsync(scheduleId)).GetProperty("lastFiring").GetProperty("id").GetGuid();
        var marker = (await host.CreateAsync(Body(name: "Marker", cron: "* * * * *", continueSession: false))).GetProperty("id").GetGuid();
        var now = host.Clock.GetUtcNow().UtcDateTime;
        host.Clock.Set(now.AddMinutes(1).AddSeconds(1).ToString("o"));
        host.Pulse();
        var markerTurn = await host.Adapter.NextTurnAsync();
        await host.LastFiringEndedAsync(marker, "Completed");
        Assert.Equal(before + 1, host.Adapter.ChatTurns);
        Assert.Equal(lastBefore, (await host.GetAsync(scheduleId)).GetProperty("lastFiring").GetProperty("id").GetGuid());
        Assert.NotNull(markerTurn);
    }
}
