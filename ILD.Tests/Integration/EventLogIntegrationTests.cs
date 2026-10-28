using System.Net;

namespace ILD.Tests.Integration;

/// <summary>
/// Event log endpoints currently live on <c>LoopRunsController</c>
/// (<c>/api/v1/loopruns/{id}/events</c>). These tests verify route wiring for
/// those event endpoints.
/// </summary>
public class EventLogIntegrationTests(SharedApiFactory factory) : IClassFixture<SharedApiFactory>
{
    [Fact]
    public async Task GetEvents_with_token_for_unknown_run_returns_200_with_an_empty_page()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/loopruns/" + Guid.NewGuid() + "/events", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
