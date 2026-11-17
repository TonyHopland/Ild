using System.Net;
using System.Net.Http.Json;

namespace ILD.Tests.Integration;

/// <summary>ILD no longer waits for the processes an agent leaves running, so its restart and resume endpoints are gone.</summary>
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
}
