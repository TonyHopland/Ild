using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ILD.Data;
using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// The chat sidebar's server surface over HTTP: starring a chat and searching
/// chats by message content, both scoped to the caller's own chats.
/// </summary>
public class ChatSidebarApiTests
{
    private static async Task SeedChatAsync(ApiFactory factory, Guid id, string owner, params (string Role, string Content)[] messages)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ChatSessions.Add(new ChatSession
        {
            Id = id,
            UserId = owner,
            Name = $"Chat {id:N}",
            AiProviderId = Guid.NewGuid(),
            ProviderType = "claude-code",
            ToolAllowlistCsv = "ild",
            ScratchPath = Path.Combine(Path.GetTempPath(), "ild-chat-sidebar-api", id.ToString("N")),
            UpdatedAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
        });
        var sequence = 0;
        foreach (var (role, content) in messages)
        {
            db.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(),
                ChatSessionId = id,
                Role = role,
                Content = content,
                Sequence = sequence++,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<DateTime?> StoredUpdatedAtAsync(ApiFactory factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.ChatSessions.AsNoTracking().SingleAsync(c => c.Id == id, TestContext.Current.CancellationToken)).UpdatedAt;
    }

    private static async Task<Dictionary<Guid, bool>> FavoritesAsync(HttpClient client)
    {
        var history = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/chat/history", TestContext.Current.CancellationToken);
        return history!.ToDictionary(c => c.GetProperty("id").GetGuid(), c => c.GetProperty("isFavorite").GetBoolean());
    }

    private static async Task<HttpStatusCode> PutFavoriteAsync(HttpClient client, Guid id, string json)
    {
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PutAsync($"/api/v1/chat/{id}/favorite", body, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    [Fact]
    public async Task A_user_stars_and_unstars_their_own_chat_without_touching_its_last_activity_and_never_another_users()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        var bobs = Guid.NewGuid();
        await SeedChatAsync(factory, mine, factory.AdminUsername);
        await SeedChatAsync(factory, other, factory.AdminUsername);
        await SeedChatAsync(factory, bobs, "bob");
        var activity = await StoredUpdatedAtAsync(factory, mine);
        var bobsActivity = await StoredUpdatedAtAsync(factory, bobs);

        Assert.Equal(new Dictionary<Guid, bool> { [mine] = false, [other] = false }, await FavoritesAsync(client));

        Assert.Equal(HttpStatusCode.NoContent, await PutFavoriteAsync(client, mine, """{"favorite":true}"""));
        Assert.Equal(new Dictionary<Guid, bool> { [mine] = true, [other] = false }, await FavoritesAsync(client));
        Assert.Equal(activity, await StoredUpdatedAtAsync(factory, mine));

        Assert.Equal(HttpStatusCode.NoContent, await PutFavoriteAsync(client, mine, """{"favorite":false}"""));
        Assert.Equal(new Dictionary<Guid, bool> { [mine] = false, [other] = false }, await FavoritesAsync(client));
        Assert.Equal(activity, await StoredUpdatedAtAsync(factory, mine));

        Assert.Equal(HttpStatusCode.NotFound, await PutFavoriteAsync(client, bobs, """{"favorite":true}"""));
        Assert.Equal(HttpStatusCode.NotFound, await PutFavoriteAsync(client, Guid.NewGuid(), """{"favorite":true}"""));
        Assert.Equal(HttpStatusCode.BadRequest, await PutFavoriteAsync(client, mine, "{}"));
        Assert.Equal(HttpStatusCode.BadRequest, await PutFavoriteAsync(client, mine, """{"favorite":"yes"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, await PutFavoriteAsync(client, mine, """{"favorite":null}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, await PutFavoriteAsync(factory.CreateClient(), mine, """{"favorite":true}"""));

        Assert.Equal(new Dictionary<Guid, bool> { [mine] = false, [other] = false }, await FavoritesAsync(client));
        Assert.Equal(bobsActivity, await StoredUpdatedAtAsync(factory, bobs));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var bobStarred = await db.ChatSessions.AsNoTracking().Where(c => c.Id == bobs)
                .Select(c => EF.Property<bool>(c, "IsFavorite")).SingleAsync(TestContext.Current.CancellationToken);
            Assert.False(bobStarred);
        }
    }

    [Fact]
    public async Task Search_finds_the_callers_chats_whose_messages_contain_the_query_case_insensitively_and_literally()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var deploy = Guid.NewGuid();
        var percent = Guid.NewGuid();
        var ego = Guid.NewGuid();
        var answered = Guid.NewGuid();
        var bobs = Guid.NewGuid();
        await SeedChatAsync(factory, deploy, factory.AdminUsername, ("user", "Fix the DEPLOY loop please"));
        await SeedChatAsync(factory, percent, factory.AdminUsername, ("user", "hello"), ("assistant", "It is 100% done_ok now"));
        await SeedChatAsync(factory, ego, factory.AdminUsername, ("user", "my ego is fine, 1000 done"));
        await SeedChatAsync(factory, answered, factory.AdminUsername, ("user", "what next?"), ("assistant", "Redeploy it."));
        await SeedChatAsync(factory, bobs, "bob", ("user", "deploy the thing"));
        await SeedChatAsync(factory, Guid.NewGuid(), factory.AdminUsername);

        async Task<HashSet<Guid>> SearchAsync(string q)
        {
            var ids = await client.GetFromJsonAsync<Guid[]>($"/api/v1/chat/search?q={Uri.EscapeDataString(q)}", TestContext.Current.CancellationToken);
            return ids!.ToHashSet();
        }

        Assert.Equal(new HashSet<Guid> { deploy, answered }, await SearchAsync("deploy"));
        Assert.Equal(new HashSet<Guid> { deploy, answered }, await SearchAsync("DePlOy"));
        Assert.Equal(new HashSet<Guid> { percent }, await SearchAsync("%"));
        Assert.Equal(new HashSet<Guid> { percent }, await SearchAsync("e_o"));
        Assert.Equal(new HashSet<Guid> { percent }, await SearchAsync("0% d"));
        Assert.Empty(await SearchAsync("nothing like this"));

        foreach (var blank in new[] { "/api/v1/chat/search", "/api/v1/chat/search?q=", "/api/v1/chat/search?q=%20%20" })
        {
            using var response = await client.GetAsync(blank, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using (var anonymous = await factory.CreateClient().GetAsync("/api/v1/chat/search?q=deploy", TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
