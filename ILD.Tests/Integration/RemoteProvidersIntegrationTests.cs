using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

public class RemoteProvidersIntegrationTests
{
    [Fact]
    public async Task GetAll_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/remoteproviders", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAll_with_token_returns_200_and_empty_array()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/remoteproviders", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<object[]>(TestContext.Current.CancellationToken);
        Assert.Empty(items!);
    }

    [Fact]
    public async Task GetTypes_with_token_returns_only_implemented_provider_types()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/v1/remoteproviders/types", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<RemoteProviderTypeResponse[]>(TestContext.Current.CancellationToken);
        Assert.NotNull(items);
        Assert.Equal(new[] { "AzureDevOps", "Forgejo", "GitHub" }, items!.Select(i => i.Type).OrderBy(t => t).ToArray());
    }

    private static HttpClient CreateAgentClient(ApiFactory factory)
    {
        var client = factory.CreateClient();
        var token = factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<string> SeedProviderAsync(ApiFactory factory, string type, string url)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var provider = new RemoteProvider { Id = Guid.NewGuid(), Name = "prov", Type = type, Url = url, ApiKey = "k-123" };
        db.RemoteProviders.Add(provider);
        await db.SaveChangesAsync();
        return provider.Id.ToString();
    }

    [Fact]
    public async Task Test_returns_the_result_as_data_for_a_stored_provider()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        // Not an absolute URL, so the test answers without leaving the process.
        var id = await SeedProviderAsync(factory, "Forgejo", "not a url");

        var response = await client.PostAsync($"/api/v1/remoteproviders/{id}/test", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal("Misconfigured", body.GetProperty("outcome").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        Assert.True(body.TryGetProperty("detail", out _));
    }

    [Fact]
    public async Task Test_rejects_a_malformed_id_and_an_unknown_one()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var malformed = await client.PostAsync("/api/v1/remoteproviders/not-a-guid/test", null, TestContext.Current.CancellationToken);
        var unknown = await client.PostAsync($"/api/v1/remoteproviders/{Guid.NewGuid()}/test", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Test_is_for_signed_in_users_only()
    {
        await using var factory = new ApiFactory();
        var id = await SeedProviderAsync(factory, "Forgejo", "not a url");

        var anonymous = await factory.CreateClient().PostAsync($"/api/v1/remoteproviders/{id}/test", null, TestContext.Current.CancellationToken);
        var agent = await CreateAgentClient(factory).PostAsync($"/api/v1/remoteproviders/{id}/test", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, agent.StatusCode);
    }

    private sealed class RemoteProviderTypeResponse
    {
        public string Type { get; set; } = string.Empty;
    }
}
