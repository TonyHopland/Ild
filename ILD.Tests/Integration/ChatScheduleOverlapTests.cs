using System.Net;
using static ILD.Tests.Integration.ChatScheduleTestHost;

namespace ILD.Tests.Integration;

/// <summary>
/// A schedule runs one turn at a time, even when each of its firings gets a chat
/// of its own; and only the repository scopes the API names are accepted.
/// </summary>
public sealed class ChatScheduleOverlapTests
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
}
