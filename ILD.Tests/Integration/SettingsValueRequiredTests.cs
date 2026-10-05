using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ILD.Tests.Integration;

/// <summary>
/// A settings write must name its value: a body without one is refused, never
/// read as the empty value that clears the title provider tag.
/// </summary>
public class SettingsValueRequiredTests
{
    [Fact]
    public async Task A_write_with_no_value_is_refused_and_changes_nothing()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var ct = TestContext.Current.CancellationToken;
        const string url = "/api/v1/settings/chat.titleProviderTag";

        using (var set = await client.PutAsJsonAsync(url, new { value = "Fast" }, ct))
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        using (var empty = await client.PutAsJsonAsync(url, new { }, ct))
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var stored = await client.GetFromJsonAsync<JsonElement>(url, ct);
        Assert.Equal("Fast", stored.GetProperty("value").GetString());
    }
}
