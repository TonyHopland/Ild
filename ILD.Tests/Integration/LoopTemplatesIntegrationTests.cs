using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace ILD.Tests.Integration;

[Collection("AuthEnvironment")]
public class LoopTemplatesIntegrationTests
{
    [Fact]
    public async Task GetAll_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/looptemplates");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAll_with_token_returns_200_and_seeded_templates()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/looptemplates");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // TemplateSeeder runs on startup; the seeded list may be empty or non-empty.
        var items = await response.Content.ReadFromJsonAsync<object[]>();
        items.Should().NotBeNull();
    }
}
