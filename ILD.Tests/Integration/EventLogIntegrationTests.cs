using System.Net;
using FluentAssertions;

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
        var response = await client.GetAsync("/api/v1/loopruns/" + Guid.NewGuid() + "/events");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetEvents_with_token_for_unknown_run_returns_404_or_empty()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/loopruns/" + Guid.NewGuid() + "/events");
        // Controller returns either 404 (no run) or 200 with empty page.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }
}
