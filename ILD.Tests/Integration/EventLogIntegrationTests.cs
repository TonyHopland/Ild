using System.Net;

namespace ILD.Tests.Integration;

/// <summary>
/// Event log endpoints currently live on <c>LoopRunsController</c>
/// (<c>/api/v1/loopruns/{id}/events</c>). These tests verify route wiring and
/// auth behaviour for those event endpoints.
/// </summary>
public class EventLogIntegrationTests
{
    [Fact]
    public async Task GetEvents_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/loopruns/" + Guid.NewGuid() + "/events", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetEvents_with_token_for_unknown_run_returns_200_with_an_empty_page()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/loopruns/" + Guid.NewGuid() + "/events", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
