using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ILD.Tests.Integration;

/// <summary>
/// chat.autoDeleteInactive (off on a fresh install, stored as the literal true or
/// false) and chat.autoDeleteInactiveDays (14 on a fresh install, an integer from 1
/// to 3650); anything else is refused and leaves what was stored.
/// </summary>
public class ChatAutoDeleteSettingsTests
{
    private const string Switch = "chat.autoDeleteInactive";
    private const string Days = "chat.autoDeleteInactiveDays";

    private static async Task<string> StoredAsync(HttpClient client, string key)
        => (await client.GetFromJsonAsync<JsonElement>($"/api/v1/settings/{key}", TestContext.Current.CancellationToken))
            .GetProperty("value").GetString()!;

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string key, string value)
        => client.PutAsJsonAsync($"/api/v1/settings/{key}", new { value }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_fresh_install_reports_the_switch_off_and_14_days()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var all = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/settings", TestContext.Current.CancellationToken);
        var values = all!.ToDictionary(s => s.GetProperty("key").GetString()!, s => s.GetProperty("value").GetString());
        Assert.Equal("false", values[Switch]);
        Assert.Equal("14", values[Days]);
        Assert.Equal("false", await StoredAsync(client, Switch));
        Assert.Equal("14", await StoredAsync(client, Days));
    }

    [Fact]
    public async Task The_switch_stores_any_bool_spelling_as_the_literal_and_refuses_the_rest()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        foreach (var (sent, stored) in new[] { (" TRUE ", "true"), ("False", "false"), ("True", "true") })
        {
            using var response = await PutAsync(client, Switch, sent);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(stored, await StoredAsync(client, Switch));
        }

        foreach (var invalid in new[] { "on", "yes", "1", "" })
        {
            using var refused = await PutAsync(client, Switch, invalid);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("true", await StoredAsync(client, Switch));
        }
    }

    [Fact]
    public async Task The_days_take_1_to_3650_and_anything_else_is_refused_naming_the_key_and_range()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        foreach (var valid in new[] { "1", "3650", "30" })
        {
            using var response = await PutAsync(client, Days, valid);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(valid, await StoredAsync(client, Days));
        }

        foreach (var invalid in new[] { "0", "-1", "3651", "7.5", "two weeks", "" })
        {
            using var refused = await PutAsync(client, Days, invalid);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var error = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains(Days, error);
            Assert.Contains("3650", error);
            Assert.Equal("30", await StoredAsync(client, Days));
        }
    }
}
