using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static ILD.Tests.Integration.ChatScheduleTestHost;

namespace ILD.Tests.Integration;

/// <summary>
/// Rules of schedules beyond the acceptance tests: one turn at a time, what an
/// edit keeps, and how the list is read.
/// </summary>
public sealed class ChatScheduleRuleTests
{
    [Fact]
    public async Task A_firing_while_the_previous_firings_turn_runs_in_another_chat_is_skipped_as_busy()
    {
        await using var host = await StartAsync("2026-07-06T09:00:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var id = (await host.CreateAsync(Body(continueSession: false))).GetProperty("id").GetGuid();
        host.Adapter.Behaviour = ScriptedChatAdapter.Mode.Hang;

        var first = await host.RunNowAsync(id);
        var running = await host.Adapter.NextTurnAsync();
        Assert.False(IsOutcome(first, "Skipped"), first.ToString());

        var second = await host.RunNowAsync(id);
        Assert.True(IsOutcome(second, "Skipped"), second.ToString());
        Assert.Contains("busy", Reason(second), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(running.ChatSessionId, second.GetProperty("chatSessionId").GetGuid());
        Assert.Equal(1, host.Adapter.ChatTurns);
        Assert.False(running.Cancel.IsCancellationRequested);

        host.Adapter.Behaviour = ScriptedChatAdapter.Mode.Succeed;
        host.Adapter.ReleaseHung();
        await host.ChatIdleAsync(running.ChatSessionId!.Value);

        var third = await host.RunNowAsync(id);
        Assert.False(IsOutcome(third, "Skipped"), third.ToString());
        var next = await host.Adapter.NextTurnAsync();
        Assert.NotEqual(running.ChatSessionId, next.ChatSessionId);
    }

    [Fact]
    public async Task A_repository_scope_outside_all_selected_and_none_is_a_400()
    {
        await using var host = await StartAsync("2026-07-06T09:00:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var body = Body();
        body["repositoryScope"] = 7;

        using var response = await host.PostScheduleAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"error\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_edit_that_leaves_the_timing_alone_keeps_the_firing_a_pause_skipped()
    {
        await using var host = await StartAsync("2026-07-06T07:59:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var body = Body(cron: "0 8 * * 1", timeZone: "UTC");
        var id = (await host.CreateAsync(body)).GetProperty("id").GetGuid();
        await host.SetPausedAsync(true);
        host.Clock.Set("2026-07-06T08:00:30Z");
        host.Pulse();
        await host.LastFiringEndedAsync(id, "Skipped");

        body["name"] = "Renamed while paused";
        await host.UpdateAsync(id, body);
        await host.SetPausedAsync(false);

        var turn = await host.Adapter.NextTurnAsync();
        Assert.Contains("Renamed while paused", turn.Prompt);
        await host.LastFiringEndedAsync(id, "Completed");
    }

    [Fact]
    public async Task A_tag_change_keeps_the_latest_chat_linked_until_the_next_firing_starts_a_new_one()
    {
        await using var host = await StartAsync("2026-07-06T09:00:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var nightly = await host.SeedProviderAsync("nightly", isDefault: false, "nightly");
        var body = Body(continueSession: true);
        var id = (await host.CreateAsync(body)).GetProperty("id").GetGuid();
        await host.RunNowAsync(id);
        var first = await host.Adapter.NextTurnAsync();
        await host.LastFiringEndedAsync(id, "Completed");
        await host.ChatIdleAsync(first.ChatSessionId!.Value);

        body["aiTag"] = "nightly";
        await host.UpdateAsync(id, body);
        Assert.Equal(first.ChatSessionId, (await host.GetAsync(id)).GetProperty("latestChatSessionId").GetGuid());

        await host.RunNowAsync(id);
        var second = await host.Adapter.NextTurnAsync();
        Assert.NotEqual(first.ChatSessionId, second.ChatSessionId);
        Assert.Equal(nightly, second.Provider.Id);
        Assert.Equal(second.ChatSessionId, (await host.GetAsync(id)).GetProperty("latestChatSessionId").GetGuid());
    }

    [Fact]
    public async Task The_list_is_read_in_pages()
    {
        await using var host = await StartAsync("2026-07-06T09:00:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        foreach (var name in new[] { "a", "b", "c" })
            await host.CreateAsync(Body(name: name));

        var first = await host.Client.GetFromJsonAsync<JsonElement[]>("/api/v1/chat/schedules?take=2", TestContext.Current.CancellationToken);
        var rest = await host.Client.GetFromJsonAsync<JsonElement[]>("/api/v1/chat/schedules?skip=2&take=2", TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b"], first!.Select(s => s.GetProperty("name").GetString()));
        Assert.Equal(["c"], rest!.Select(s => s.GetProperty("name").GetString()));
    }
}
