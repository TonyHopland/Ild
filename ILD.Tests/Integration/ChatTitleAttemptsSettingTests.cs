using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ILD.Tests.Integration;

/// <summary>chat.titleMaxAttempts: 3 on a fresh install, an integer from 1 to 10, anything else refused.</summary>
public class ChatTitleAttemptsSettingTests
{
    private const string Url = "/api/v1/settings/chat.titleMaxAttempts";

    private static async Task<string> StoredAsync(HttpClient client)
        => (await client.GetFromJsonAsync<JsonElement>(Url, TestContext.Current.CancellationToken))
            .GetProperty("value").GetString()!;

    [Fact]
    public async Task Title_attempts_default_to_three_take_one_to_ten_and_refuse_anything_else()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var ct = TestContext.Current.CancellationToken;

        var all = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/settings", ct);
        Assert.Equal("3", all!.Single(s => s.GetProperty("key").GetString() == "chat.titleMaxAttempts").GetProperty("value").GetString());
        Assert.Equal("3", await StoredAsync(client));

        foreach (var valid in new[] { "1", "10", "5" })
        {
            using var stored = await client.PutAsJsonAsync(Url, new { value = valid }, ct);
            Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
            Assert.Equal(valid, await StoredAsync(client));
        }

        foreach (var invalid in new[] { "0", "11", "-1", "three", "", "2.5" })
        {
            using var refused = await client.PutAsJsonAsync(Url, new { value = invalid }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("5", await StoredAsync(client));
        }
    }
}
