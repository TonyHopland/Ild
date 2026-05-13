using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace ILD.Tests.Integration;

[Collection("AuthEnvironment")]
public class LoopRunsIntegrationTests
{
    [Fact]
    public async Task GetAll_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/looprins");
        // Path is /api/v1/[controller] -> /api/v1/loopruns
        // Use the correct route below
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Unauthorized);

        var actual = await client.GetAsync("/api/v1/loopruns");
        actual.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAll_with_token_returns_200_and_empty_array()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/loopruns");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await response.Content.ReadFromJsonAsync<object[]>();
        items.Should().NotBeNull();
        items!.Length.Should().Be(0);
    }

    [Fact]
    public async Task GetById_for_unknown_id_returns_404()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/loopruns/" + Guid.NewGuid());
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
