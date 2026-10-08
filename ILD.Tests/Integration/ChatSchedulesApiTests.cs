using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static ILD.Tests.Integration.ChatScheduleTestHost;

namespace ILD.Tests.Integration;

/// <summary>
/// A user's schedules over HTTP: what they hold, what is refused, and when each
/// fires next. Schedules belong to the user who made them and nobody else can
/// reach them.
/// </summary>
public sealed class ChatSchedulesApiTests
{
    [Fact]
    public async Task A_user_creates_lists_updates_and_deletes_their_own_schedules()
    {
        await using var host = await StartAsync("2026-07-06T05:00:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        await host.SeedProviderAsync("tagged", isDefault: false, "nightly");
        var repo = await host.SeedRepositoryAsync("example-repo");

        var id = await host.CreateAsync(Body(
            name: "Weekly retro", prompt: "Review the week.", aiTag: "", cron: "0 8 * * 1",
            timeZone: "Europe/Oslo", enabled: true, repositoryScope: "Selected", repositoryIds: [repo], continueSession: false));

        var page = await host.ListPageAsync();
        Assert.False(page.GetProperty("schedulerPaused").GetBoolean());
        var listed = await host.GetAsync(id);
        Assert.Equal("Weekly retro", listed.GetProperty("name").GetString());
        Assert.Equal("Review the week.", listed.GetProperty("prompt").GetString());
        Assert.True(string.IsNullOrEmpty(listed.GetProperty("aiTag").GetString()));
        Assert.Equal("0 8 * * 1", listed.GetProperty("cronExpression").GetString());
        Assert.Equal("Europe/Oslo", listed.GetProperty("timeZone").GetString());
        Assert.True(listed.GetProperty("enabled").GetBoolean());
        Assert.Equal("Selected", listed.GetProperty("repositoryScope").GetString(), ignoreCase: true);
        Assert.Equal([repo], listed.GetProperty("repositoryIds").EnumerateArray().Select(e => e.GetGuid()));
        Assert.False(listed.GetProperty("continueSession").GetBoolean());
        // 08:00 in Oslo on a summer Monday is 06:00Z.
        Assert.Equal(ParseUtc("2026-07-06T06:00:00Z"), Utc(listed.GetProperty("nextFireAt")));
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("lastFiring").ValueKind);
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("latestChatSessionId").ValueKind);

        // A new cron and zone count from now.
        await host.UpdateAsync(id, Body(
            name: "Backlog grooming", prompt: "Groom the backlog.", aiTag: "nightly", cron: "30 9 * * *",
            timeZone: "UTC", enabled: true, repositoryScope: "None", continueSession: true));
        var updated = await host.GetAsync(id);
        Assert.Equal("Backlog grooming", updated.GetProperty("name").GetString());
        Assert.Equal("Groom the backlog.", updated.GetProperty("prompt").GetString());
        Assert.Equal("nightly", updated.GetProperty("aiTag").GetString());
        Assert.Equal("30 9 * * *", updated.GetProperty("cronExpression").GetString());
        Assert.Equal("UTC", updated.GetProperty("timeZone").GetString());
        Assert.Equal("None", updated.GetProperty("repositoryScope").GetString(), ignoreCase: true);
        Assert.Empty(updated.GetProperty("repositoryIds").EnumerateArray());
        Assert.True(updated.GetProperty("continueSession").GetBoolean());
        Assert.Equal(ParseUtc("2026-07-06T09:30:00Z"), Utc(updated.GetProperty("nextFireAt")));

        await host.DeleteAsync(id);
        Assert.DoesNotContain(await host.ListAsync(), s => s.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task Another_users_schedule_is_not_found_for_every_verb_and_an_agent_token_is_forbidden()
    {
        await using var host = await StartAsync("2026-07-06T05:00:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var bobs = await host.CreateAsync(Body(name: "Bob's"));
        var mine = await host.CreateAsync(Body(name: "Mine"));
        await host.WithDbAsync(db => db.Set<ChatSchedule>().Where(s => s.Id == bobs)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UserId, "bob"), TestContext.Current.CancellationToken));

        Assert.Equal([mine], (await host.ListAsync()).Select(s => s.GetProperty("id").GetGuid()));
        foreach (var target in new[] { bobs, Guid.NewGuid() })
        {
            using var put = await host.PutScheduleAsync(target, Body(name: "taken over"));
            Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
            using var run = await host.PostRunAsync(target);
            Assert.Equal(HttpStatusCode.NotFound, run.StatusCode);
            using var delete = await host.Client.DeleteAsync($"/api/v1/chat/schedules/{target}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        }

        Assert.Equal(0, host.Adapter.ChatTurns);
        var stored = await host.WithDbAsync(db => db.Set<ChatSchedule>().AsNoTracking()
            .SingleAsync(s => s.Id == bobs, TestContext.Current.CancellationToken));
        Assert.Equal("Bob's", stored.Name);
        Assert.Equal(0, await host.WithDbAsync(db => db.Set<ChatScheduleFiring>().CountAsync(TestContext.Current.CancellationToken)));

        // The agent service token authenticates, but it is not a user.
        using var agent = host.Factory.CreateClient();
        agent.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            host.Factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token);
        var ct = TestContext.Current.CancellationToken;
        using (var r = await agent.GetAsync("/api/v1/chat/schedules", ct)) Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using (var r = await agent.PostAsJsonAsync("/api/v1/chat/schedules", Body(name: "agent's"), ct)) Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using (var r = await agent.PutAsJsonAsync($"/api/v1/chat/schedules/{mine}", Body(name: "agent's"), ct)) Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using (var r = await agent.PostAsync($"/api/v1/chat/schedules/{mine}/run", null, ct)) Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using (var r = await agent.DeleteAsync($"/api/v1/chat/schedules/{mine}", ct)) Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("Mine", (await host.GetAsync(mine)).GetProperty("name").GetString());
        Assert.Equal(2, await host.WithDbAsync(db => db.Set<ChatSchedule>().CountAsync(ct)));
    }

    private static readonly (string Cron, bool Accepted)[] Crons =
    [
        ("0 0 29 2 *", true),
        ("0 0 31 * *", true),
        ("*/5 * * * *", true),
        ("0 0 * * 0", true),
        ("0 0 * * 7", true),
        ("0 0 30 2 1", true),
        ("59 23 31 12 *", true),
        ("10-30/10 1-3/2 1,15 */3 0-7", true),
        ("0,15,30,45 8-17 * * 1-5", true),
        ("", false),
        ("0 8 * *", false),
        ("0 8 * * 1 1", false),
        ("60 * * * *", false),
        ("-1 * * * *", false),
        ("* 24 * * *", false),
        ("* * 0 * *", false),
        ("* * 32 * *", false),
        ("* * * 0 *", false),
        ("* * * 13 *", false),
        ("* * * * 8", false),
        ("5-1 * * * *", false),
        ("*/0 * * * *", false),
        ("10-30/0 * * * *", false),
        ("*/x * * * *", false),
        ("1,,2 * * * *", false),
        ("1, * * * *", false),
        (",1 * * * *", false),
        ("0 8 * * MON", false),
        ("0 8 * JAN *", false),
        ("0 8 * * 1#2", false),
        ("0 8 ? * 1", false),
        ("@daily", false),
        ("0 0 30 2 *", false),
        ("0 0 31 2 *", false),
        ("0 0 31 4,6,9,11 *", false),
    ];

    [Fact]
    public async Task Only_crons_that_parse_and_can_fire_are_accepted_and_every_refusal_is_a_400_with_an_error()
    {
        await using var host = await StartAsync("2026-07-06T05:00:00Z");
        await host.SeedProviderAsync("main", isDefault: true);

        foreach (var (cron, accepted) in Crons)
        {
            using var response = await host.PostScheduleAsync(Body(cron: cron));
            if (accepted)
                Assert.True(response.StatusCode == HttpStatusCode.Created,
                    $"'{cron}' was refused: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
            else
                await AssertRefusedAsync(response, $"cron '{cron}'");
        }
    }

    [Fact]
    public async Task Every_other_invalid_field_is_a_400_with_an_error_on_create_and_on_update()
    {
        await using var host = await StartAsync("2026-07-06T05:00:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var repo = await host.SeedRepositoryAsync("example-repo");

        var existing = await host.CreateAsync(Body());
        using (var ok = await host.PostScheduleAsync(Body(name: new string('n', 120))))
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        using (var ok = await host.PostScheduleAsync(Body(repositoryScope: "Selected", repositoryIds: [repo])))
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        var invalid = new (string What, Dictionary<string, object?> Body)[]
        {
            ("an unknown zone", Body(timeZone: "Mars/Olympus_Mons")),
            ("an empty name", Body(name: "")),
            ("a name over 120", Body(name: new string('n', 121))),
            ("an empty prompt", Body(prompt: "")),
            ("selected with no repositories", Body(repositoryScope: "Selected")),
            ("selected with an id that is no repository", Body(repositoryScope: "Selected", repositoryIds: [repo, Guid.NewGuid()])),
            ("ids with all", Body(repositoryScope: "All", repositoryIds: [repo])),
            ("ids with none", Body(repositoryScope: "None", repositoryIds: [repo])),
            ("a cron that never fires", Body(cron: "0 0 30 2 *")),
            ("an invalid cron", Body(cron: "0 8 * * MON")),
        };

        foreach (var (what, body) in invalid)
        {
            using (var created = await host.PostScheduleAsync(body))
                await AssertRefusedAsync(created, "create with " + what);
            using (var updated = await host.PutScheduleAsync(existing, body))
                await AssertRefusedAsync(updated, "update with " + what);
        }

        Assert.Equal(3, (await host.ListAsync()).Length);
        var unchanged = await host.GetAsync(existing);
        Assert.Equal("Weekly retro", unchanged.GetProperty("name").GetString());
        Assert.Equal("All", unchanged.GetProperty("repositoryScope").GetString(), ignoreCase: true);
        Assert.Equal("UTC", unchanged.GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task A_disabled_schedule_has_no_next_firing_and_re_enabling_counts_from_now()
    {
        await using var host = await StartAsync("2026-07-06T05:00:00Z");
        await host.SeedProviderAsync("main", isDefault: true);
        var body = Body(cron: "0 8 * * 1", timeZone: "Europe/Oslo", enabled: false);
        var id = await host.CreateAsync(body);
        Assert.Null(Utc((await host.GetAsync(id)).GetProperty("nextFireAt")));

        body["enabled"] = true;
        await host.UpdateAsync(id, body);
        Assert.Equal(ParseUtc("2026-07-06T06:00:00Z"), Utc((await host.GetAsync(id)).GetProperty("nextFireAt")));

        body["enabled"] = false;
        await host.UpdateAsync(id, body);
        Assert.Null(Utc((await host.GetAsync(id)).GetProperty("nextFireAt")));

        // Three Mondays go by while it is off, and the scheduler passes over it.
        host.Clock.Set("2026-07-22T12:00:00Z");
        await host.RunDueAsync();
        body["enabled"] = true;
        await host.UpdateAsync(id, body);
        Assert.Equal(ParseUtc("2026-07-27T06:00:00Z"), Utc((await host.GetAsync(id)).GetProperty("nextFireAt")));
        await host.RunDueAsync();

        // Moved to another zone in winter: counted from now in that zone.
        host.Clock.Set("2026-12-01T00:00:00Z");
        body["timeZone"] = "America/New_York";
        await host.UpdateAsync(id, body);
        Assert.Equal(ParseUtc("2026-12-07T13:00:00Z"), Utc((await host.GetAsync(id)).GetProperty("nextFireAt")));
        body["timeZone"] = "Europe/Oslo";
        await host.UpdateAsync(id, body);
        Assert.Equal(ParseUtc("2026-12-07T07:00:00Z"), Utc((await host.GetAsync(id)).GetProperty("nextFireAt")));

        Assert.Equal(JsonValueKind.Null, (await host.GetAsync(id)).GetProperty("lastFiring").ValueKind);
        Assert.Equal(0, host.Adapter.ChatTurns);
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string what)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{what} answered {(int)response.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()), $"{what} gave no error");
    }
}
