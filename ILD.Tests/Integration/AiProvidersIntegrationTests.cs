using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task SetDefault_endpoint_promotes_provider_and_demotes_previous_default()
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

        var promoteResponse = await client.PostAsync($"/api/v1/aiproviders/{b}/set-default", null);
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

    [Fact]
    public async Task CustomMcpServers_round_trips_through_create_response_and_update()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        // The raw servers JSON the UI sends via the dedicated, non-secret field.
        var servers = "{\"chrome-devtools\":{\"command\":[\"npx\"]}}";

        var createResponse = await client.PostAsJsonAsync("/api/v1/aiproviders", new
        {
            name = "OpenCode w/chrome",
            type = "opencode",
            baseUrl = "https://oc.example.com",
            model = "gpt-4",
            isDefault = false,
            customMcpServersJson = servers,
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // The create response echoes the value (never the whole config blob).
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal(servers, created.GetProperty("customMcpServersJson").GetString());
        Assert.True(created.GetProperty("hasConfig").GetBoolean());
        Assert.False(created.TryGetProperty("config", out _));

        // GET also returns it, so reopening the edit modal round-trips the value.
        var getResponse = await client.GetAsync($"/api/v1/aiproviders/{id}");
        var fetched = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(servers, fetched.GetProperty("customMcpServersJson").GetString());

        // Updating with a new value persists and is echoed back.
        var newServers = "{\"other\":{\"command\":[\"npx\"]}}";
        var updateResponse = await client.PutAsJsonAsync($"/api/v1/aiproviders/{id}", new
        {
            name = "OpenCode w/chrome",
            type = "opencode",
            baseUrl = "https://oc.example.com",
            model = "gpt-4",
            isDefault = false,
            customMcpServersJson = newServers,
        });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(newServers, updated.GetProperty("customMcpServersJson").GetString());
    }

    [Fact]
    public async Task Editing_a_provider_preserves_other_config_keys_the_ui_never_sees()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        // Seed a provider whose config carries a secret (an embedded apiKey the Pi
        // adapter reads) alongside the MCP value — the shape the UI must not clobber.
        var config = "{\"apiKey\":\"sk-secret\",\"customMcpServersJson\":\"{\\\"a\\\":{\\\"command\\\":[\\\"npx\\\"]}}\"}";
        var createResponse = await client.PostAsJsonAsync("/api/v1/aiproviders", new
        {
            name = "Seeded",
            type = "opencode",
            baseUrl = "https://oc.example.com",
            model = "gpt-4",
            isDefault = false,
            config,
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var id = (await createResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        // A normal UI edit sends only the MCP value (no raw config).
        var newServers = "{\"b\":{\"command\":[\"npx\"]}}";
        var updateResponse = await client.PutAsJsonAsync($"/api/v1/aiproviders/{id}", new
        {
            name = "Seeded renamed",
            type = "opencode",
            baseUrl = "https://oc.example.com",
            model = "gpt-4",
            isDefault = false,
            customMcpServersJson = newServers,
        });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        // The MCP value changed…
        Assert.Equal(newServers, updated.GetProperty("customMcpServersJson").GetString());

        // …and the embedded secret survived: it is still readable via the raw
        // config (never returned to the UI, but persisted in the store), while the
        // MCP value reflects the edit. Parse rather than substring-match so the
        // assertion is agnostic to JSON string escaping.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = db.AiProviders.Single(p => p.Id == Guid.Parse(id)).Config!;
        using var storedDoc = JsonDocument.Parse(stored);
        Assert.Equal("sk-secret", storedDoc.RootElement.GetProperty("apiKey").GetString());
        Assert.Equal(newServers, storedDoc.RootElement.GetProperty("customMcpServersJson").GetString());
    }
}
