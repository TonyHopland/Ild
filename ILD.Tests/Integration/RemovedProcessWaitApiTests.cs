using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ILD.Tests.Integration;

/// <summary>ILD no longer waits for the processes an agent leaves running, so nothing of that wait is left on the API.</summary>
public class RemovedProcessWaitApiTests
{
    [Theory]
    [InlineData("restart")]
    [InlineData("resume")]
    public async Task The_endpoints_that_restarted_and_resumed_a_waiting_node_are_gone(string action)
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync($"/api/v1/loopruns/{Guid.NewGuid()}/waiting-ild/{action}", new { note = "x" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("children.waitMinutes")]
    [InlineData("children.maxCycles")]
    public async Task The_two_settings_are_gone(string key)
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var all = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/settings", TestContext.Current.CancellationToken);
        Assert.DoesNotContain(all!, s => s.GetProperty("key").GetString() == key);
        using var put = await client.PutAsJsonAsync($"/api/v1/settings/{key}", new { value = "5" }, TestContext.Current.CancellationToken);
        Assert.NotEqual(HttpStatusCode.OK, put.StatusCode);
    }
}
