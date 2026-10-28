using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ILD.Tests.Integration;

/// <summary>The preferences endpoints through the real pipeline: behind a session, and a round trip.</summary>
public class PreferencesEndpointTests
{
    [Fact]
    public async Task A_preference_put_by_the_signed_in_user_is_read_back()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await factory.GetAdminTokenAsync());
        var ct = TestContext.Current.CancellationToken;

        var put = await client.PutAsJsonAsync("/api/v1/preferences/taskboard.folded.done", new { value = "true" }, ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var all = await client.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/preferences", ct);
        Assert.Equal("true", all!["taskboard.folded.done"]);
    }

    [Fact]
    public async Task A_put_with_no_value_is_refused_before_it_reaches_the_store()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await factory.GetAdminTokenAsync());
        var ct = TestContext.Current.CancellationToken;

        var put = await client.PutAsJsonAsync("/api/v1/preferences/chat.enabled", new { }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var all = await client.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/preferences", ct);
        Assert.Empty(all!);
    }

    [Fact]
    public async Task A_preference_deleted_is_gone_and_deleting_it_again_is_not_found()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await factory.GetAdminTokenAsync());
        var ct = TestContext.Current.CancellationToken;
        await client.PutAsJsonAsync("/api/v1/preferences/chat.enabled", new { value = "false" }, ct);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/preferences/chat.enabled", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/v1/preferences/chat.enabled", ct)).StatusCode);
        var all = await client.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/preferences", ct);
        Assert.Empty(all!);
    }
}
