using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Data;
using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// The chat title surface over HTTP: the two app settings as a fresh install
/// reports them, clearing the provider tag, and renaming a chat.
/// </summary>
public class ChatTitlesApiTests
{
    private static async Task<string> SettingAsync(HttpClient client, string key)
    {
        var body = await client.GetFromJsonAsync<JsonElement>($"/api/v1/settings/{key}", TestContext.Current.CancellationToken);
        return body.GetProperty("value").GetString()!;
    }

    [Fact]
    public async Task A_fresh_install_reports_smart_titles_off_with_no_tag_and_the_tag_can_be_cleared_again()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var all = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/settings", TestContext.Current.CancellationToken);
        var values = all!.ToDictionary(s => s.GetProperty("key").GetString()!, s => s.GetProperty("value").GetString());
        Assert.Equal("false", values["chat.smartTitles"]);
        Assert.Equal("", values["chat.titleProviderTag"]);
        Assert.Equal("false", await SettingAsync(client, "chat.smartTitles"));
        Assert.Equal("", await SettingAsync(client, "chat.titleProviderTag"));

        using (var set = await client.PutAsJsonAsync("/api/v1/settings/chat.titleProviderTag", new { value = "Fast" }, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("Fast", await SettingAsync(client, "chat.titleProviderTag"));

        // Empty clears the tag, so titles go back to the default provider.
        using (var clear = await client.PutAsJsonAsync("/api/v1/settings/chat.titleProviderTag", new { value = "" }, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Equal("", await SettingAsync(client, "chat.titleProviderTag"));
    }

    [Fact]
    public async Task A_user_renames_their_own_chat_and_never_another_users()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var mine = Guid.NewGuid();
        var bobs = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var (id, owner) in new[] { (mine, factory.AdminUsername), (bobs, "bob") })
            {
                db.ChatSessions.Add(new ChatSession
                {
                    Id = id,
                    UserId = owner,
                    Name = "Could this be solved better by",
                    AiProviderId = Guid.NewGuid(),
                    ProviderType = "claude-code",
                    ToolAllowlistCsv = "ild",
                    ScratchPath = Path.Combine(Path.GetTempPath(), "ild-chat-title-api", id.ToString("N")),
                });
            }
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var renamed = await client.PutAsJsonAsync($"/api/v1/chat/{mine}/name", new { name = "Deploy loop wiring" }, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        using (var refused = await client.PutAsJsonAsync($"/api/v1/chat/{bobs}/name", new { name = "Hijacked" }, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        using (var unknown = await client.PutAsJsonAsync($"/api/v1/chat/{Guid.NewGuid()}/name", new { name = "Ghost" }, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var history = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/chat/history", TestContext.Current.CancellationToken);
        Assert.Equal("Deploy loop wiring", Assert.Single(history!).GetProperty("name").GetString());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var bob = await db.ChatSessions.AsNoTracking().SingleAsync(c => c.Id == bobs, TestContext.Current.CancellationToken);
            Assert.Equal("Could this be solved better by", bob.Name);
        }
    }
}
