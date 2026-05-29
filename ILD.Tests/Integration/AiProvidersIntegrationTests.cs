using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ILD.Tests.Integration;

[Collection("AuthEnvironment")]
public class AiProvidersIntegrationTests
{
    [Fact]
    public async Task GetAll_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/aiproviders");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAll_with_token_returns_200_and_empty_array()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/aiproviders");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<object[]>();
        Assert.Empty(items!);
    }

    [Fact]
    public async Task Create_with_unsupported_type_returns_400()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/aiproviders", new
        {
            name = "legacy-openai",
            type = "openai",
            baseUrl = "https://api.example.com",
            model = "gpt-4",
            isDefault = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Promoting_existing_provider_via_put_demotes_previous_default()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var aResponse = await client.PostAsJsonAsync("/api/v1/aiproviders", new
        {
            name = "A",
            type = "opencode",
            baseUrl = "https://a.example.com",
            model = "gpt-4",
            isDefault = true,
        });
        Assert.Equal(HttpStatusCode.Created, aResponse.StatusCode);
        var a = (await aResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var bResponse = await client.PostAsJsonAsync("/api/v1/aiproviders", new
        {
            name = "B",
            type = "opencode",
            baseUrl = "https://b.example.com",
            model = "gpt-4",
            isDefault = false,
        });
        Assert.Equal(HttpStatusCode.Created, bResponse.StatusCode);
        var b = (await bResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var promoteResponse = await client.PutAsJsonAsync($"/api/v1/aiproviders/{b}", new
        {
            name = "B",
            type = "opencode",
            baseUrl = "https://b.example.com",
            model = "gpt-4",
            isDefault = true,
        });
        Assert.Equal(HttpStatusCode.OK, promoteResponse.StatusCode);

        var listResponse = await client.GetAsync("/api/v1/aiproviders");
        var items = await listResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(items);

        var byId = items!.ToDictionary(i => i.GetProperty("id").GetString()!);
        Assert.False(byId[a].GetProperty("isDefault").GetBoolean());
        Assert.True(byId[b].GetProperty("isDefault").GetBoolean());
        Assert.Single(items, i => i.GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public async Task Creating_second_default_demotes_the_first()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var firstResponse = await client.PostAsJsonAsync("/api/v1/aiproviders", new
        {
            name = "first",
            type = "opencode",
            baseUrl = "https://first.example.com",
            model = "gpt-4",
            isDefault = true,
        });
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

        var secondResponse = await client.PostAsJsonAsync("/api/v1/aiproviders", new
        {
            name = "second",
            type = "opencode",
            baseUrl = "https://second.example.com",
            model = "gpt-4",
            isDefault = true,
        });
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);

        var listResponse = await client.GetAsync("/api/v1/aiproviders");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var items = await listResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(items);
        Assert.Equal(2, items!.Length);

        var defaults = items.Where(i => i.GetProperty("isDefault").GetBoolean()).ToList();
        Assert.Single(defaults);
        Assert.Equal("second", defaults[0].GetProperty("name").GetString());
    }
}
