using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using static ILD.Tests.Integration.ChatScheduleTestHost;

namespace ILD.Tests.Integration;

/// <summary>
/// A continued schedule runs on the provider its tag picks at each firing. A chat
/// keeps its provider for life, so when the tag has moved, the next firing starts
/// a new chat, as editing the tag does.
/// </summary>
public sealed class ChatScheduleProviderTests
{
    [Fact]
    public async Task A_continued_schedule_starts_a_new_chat_once_its_tag_has_moved_to_another_provider()
    {
        await using var host = await StartAsync("2026-07-06T09:00:00Z");
        var first = await host.SeedProviderAsync("first", isDefault: false, "nightly");
        var second = await host.SeedProviderAsync("second", isDefault: false);
        var id = await host.CreateAsync(Body(aiTag: "nightly", continueSession: true));

        var before = await RunAsync(host, id);
        Assert.Equal(first, before.Provider);
        Assert.Equal(before.Chat, (await RunAsync(host, id)).Chat);

        using (var scope = host.Factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IProviderStore>();
            await store.UpdateAiProviderAsync((await store.GetAiProviderByIdAsync(second))!, ["nightly"]);
        }

        var after = await RunAsync(host, id);
        Assert.Equal(second, after.Provider);
        Assert.NotEqual(before.Chat, after.Chat);
        Assert.Equal(after.Chat, (await RunAsync(host, id)).Chat);
    }

    private static async Task<(Guid Chat, Guid Provider)> RunAsync(ChatScheduleTestHost host, Guid scheduleId)
    {
        var firing = await host.RunNowAsync(scheduleId);
        var turn = await host.Adapter.NextTurnAsync();
        await host.LastFiringWhenAsync(scheduleId,
            f => FiringId(f) == FiringId(firing) && IsOutcome(f, "Completed"), "the firing to complete");
        await host.ChatIdleAsync(turn.ChatSessionId!.Value);
        return (turn.ChatSessionId!.Value, turn.Provider.Id);
    }
}
