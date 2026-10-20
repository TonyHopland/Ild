using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ILD.Tests.Integration;

/// <summary>The preferences endpoints through the real pipeline: behind a session, and a round trip.</summary>
public class PreferencesEndpointTests
{
    [Fact]
    public async Task Preferences_need_a_session()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/preferences", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_preference_put_by_the_signed_in_user_is_read_back()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await factory.GetAdminTokenAsync());
        var ct = TestContext.Current.CancellationToken;

        var put = await client.PutAsJsonAsync("/api/v1/preferences/taskboard.folded", new { value = """{"Done":true}""" }, ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var all = await client.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/preferences", ct);
        Assert.Equal("""{"Done":true}""", all!["taskboard.folded"]);
    }
}
