using System.Net;
using System.Net.Http.Json;

namespace ILD.Tests.Integration;

[Collection("AuthEnvironment")]
public class RemoteProvidersIntegrationTests
{
    [Fact]
    public async Task GetAll_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/remoteproviders");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAll_with_token_returns_200_and_empty_array()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/remoteproviders");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<object[]>();
        Assert.Empty(items!);
    }

    [Fact]
    public async Task GetTypes_with_token_returns_only_implemented_provider_types()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/v1/remoteproviders/types");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<RemoteProviderTypeResponse[]>();
        Assert.NotNull(items);
        Assert.Equal(new[] { "Forgejo", "GitHub" }, items!.Select(i => i.Type).OrderBy(t => t).ToArray());
    }

    private sealed class RemoteProviderTypeResponse
    {
        public string Type { get; set; } = string.Empty;
    }
}
