using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ILD.Tests.Integration;

public class RepositoriesIntegrationTests
{
    [Fact]
    public async Task GetAll_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/repositories", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAll_with_token_returns_200_and_empty_array()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/repositories", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<object[]>(TestContext.Current.CancellationToken);
        Assert.Empty(items!);
    }

    private static object NewRepoPayload(string providerId, string? previewEnv = null) => new
    {
        name = "my-repo",
        cloneUrl = "https://git.example.com/my-repo.git",
        defaultBranch = "main",
        remoteProviderId = providerId,
        defaultIntakeStatus = "Backlog",
        previewEnv,
    };

    // A repository's RemoteProviderId is an enforced FK, so seed a real provider.
    private static async Task<string> SeedProviderAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ILD.Data.Entities.AppDbContext>();
        var provider = new RemoteProvider { Id = Guid.NewGuid(), Name = "prov", Type = "Forgejo", Url = "https://git.example.com" };
        db.RemoteProviders.Add(provider);
        await db.SaveChangesAsync();
        return provider.Id.ToString();
    }

    private static async Task<string?> ReadStoredPreviewEnvAsync(ApiFactory factory, string id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ILD.Data.Entities.AppDbContext>();
        var repo = await db.Repositories.AsNoTracking().FirstAsync(r => r.Id == Guid.Parse(id));
        return repo.PreviewEnv;
    }

    [Fact]
    public async Task Create_accepts_preview_env_but_never_echoes_it_in_plaintext()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var providerId = await SeedProviderAsync(factory);

        const string env = "API_TOKEN=secret-abc\nFOO=bar";
        var createResponse = await client.PostAsJsonAsync("/api/v1/repositories", NewRepoPayload(providerId, env), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var id = created.GetProperty("id").GetString()!;
        // Masked: the plaintext is never returned, only whether one is set.
        Assert.True(created.GetProperty("hasPreviewEnv").GetBoolean());
        Assert.False(created.TryGetProperty("previewEnv", out _));

        // GET is masked the same way, but the value is persisted in the store.
        var getResponse = await client.GetAsync($"/api/v1/repositories/{id}", TestContext.Current.CancellationToken);
        var fetched = await getResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(fetched.GetProperty("hasPreviewEnv").GetBoolean());
        Assert.False(fetched.TryGetProperty("previewEnv", out _));
        Assert.Equal(env, await ReadStoredPreviewEnvAsync(factory, id));
    }

    [Fact]
    public async Task Update_with_blank_preview_env_keeps_the_stored_value()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var providerId = await SeedProviderAsync(factory);

        const string env = "API_TOKEN=keep-me";
        var createResponse = await client.PostAsJsonAsync("/api/v1/repositories", NewRepoPayload(providerId, env), cancellationToken: TestContext.Current.CancellationToken);
        var id = (await createResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetString()!;

        // A normal edit that leaves the .env textarea blank must not wipe the secret
        // (mirrors the provider API-key masking).
        var updateResponse = await client.PutAsJsonAsync($"/api/v1/repositories/{id}", NewRepoPayload(providerId, previewEnv: null), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(updated.GetProperty("hasPreviewEnv").GetBoolean());
        Assert.Equal(env, await ReadStoredPreviewEnvAsync(factory, id));
    }

    // The agent service token authenticates fine — it just is not a user, and every
    // endpoint outside /api/v1/agent demands the user role. These tests pin that.
    private static HttpClient CreateAgentClient(ApiFactory factory)
    {
        var client = factory.CreateClient();
        var token = factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task PreviewEnv_endpoint_returns_the_decrypted_text_to_a_signed_in_user()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var providerId = await SeedProviderAsync(factory);

        const string env = "API_TOKEN=secret-abc\nFOO=bar";
        var createResponse = await client.PostAsJsonAsync("/api/v1/repositories", NewRepoPayload(providerId, env), cancellationToken: TestContext.Current.CancellationToken);
        var id = (await createResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetString()!;

        var response = await client.GetAsync($"/api/v1/repositories/{id}/preview-env", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(env, body.GetProperty("previewEnv").GetString());
    }

    [Fact]
    public async Task PreviewEnv_endpoint_is_forbidden_to_the_agent_token()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var providerId = await SeedProviderAsync(factory);

        var createResponse = await client.PostAsJsonAsync("/api/v1/repositories", NewRepoPayload(providerId, "API_TOKEN=secret-abc"), cancellationToken: TestContext.Current.CancellationToken);
        var id = (await createResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetString()!;

        var agent = CreateAgentClient(factory);
        var read = await agent.GetAsync($"/api/v1/repositories/{id}/preview-env", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.DoesNotContain("secret-abc", await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // The agent must not be able to destroy it either.
        var cleared = await agent.DeleteAsync($"/api/v1/repositories/{id}/preview-env", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, cleared.StatusCode);
        Assert.Equal("API_TOKEN=secret-abc", await ReadStoredPreviewEnvAsync(factory, id));
    }

    [Fact]
    public async Task Delete_preview_env_clears_the_stored_value()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var providerId = await SeedProviderAsync(factory);

        var createResponse = await client.PostAsJsonAsync("/api/v1/repositories", NewRepoPayload(providerId, "GOING=away"), cancellationToken: TestContext.Current.CancellationToken);
        var id = (await createResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetString()!;

        var response = await client.DeleteAsync($"/api/v1/repositories/{id}/preview-env", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("hasPreviewEnv").GetBoolean());
        Assert.Null(await ReadStoredPreviewEnvAsync(factory, id));
    }

    [Fact]
    public async Task Update_with_a_new_preview_env_replaces_the_stored_value()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var providerId = await SeedProviderAsync(factory);

        var createResponse = await client.PostAsJsonAsync("/api/v1/repositories", NewRepoPayload(providerId, "OLD=1"), cancellationToken: TestContext.Current.CancellationToken);
        var id = (await createResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetString()!;

        const string newEnv = "NEW=2\nEXTRA=3";
        var updateResponse = await client.PutAsJsonAsync($"/api/v1/repositories/{id}", NewRepoPayload(providerId, newEnv), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.Equal(newEnv, await ReadStoredPreviewEnvAsync(factory, id));
    }

    private static async Task<(string RepoId, Guid ProviderId)> SeedRepositoryAsync(ApiFactory factory, string cloneUrl, string? providerApiKey = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ILD.Data.Entities.AppDbContext>();
        var provider = new RemoteProvider { Id = Guid.NewGuid(), Name = "prov", Type = "Forgejo", Url = "https://git.example.com", ApiKey = providerApiKey };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "app", CloneUrl = cloneUrl, DefaultBranch = "main", RemoteProviderId = provider.Id, CreatedAt = DateTime.UtcNow };
        db.RemoteProviders.Add(provider);
        db.Repositories.Add(repo);
        await db.SaveChangesAsync();
        return (repo.Id.ToString(), provider.Id);
    }

    [Fact]
    public async Task Test_returns_the_result_as_data_for_a_stored_repository()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        // A blank clone URL answers without running git.
        var (id, _) = await SeedRepositoryAsync(factory, cloneUrl: "");

        var response = await client.PostAsync($"/api/v1/repositories/{id}/test", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal("Misconfigured", body.GetProperty("outcome").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        Assert.True(body.TryGetProperty("detail", out _));
    }

    private sealed class RecordingConnectionTester : IConnectionTester
    {
        public List<(Repository Repo, RemoteProvider? Provider)> RepositoryCalls { get; } = new();

        public Task<ConnectionTestResult> TestRemoteProviderAsync(RemoteProvider provider, CancellationToken ct)
            => throw new InvalidOperationException("not expected");

        public Task<ConnectionTestResult> TestRepositoryAsync(Repository repo, RemoteProvider? provider, CancellationToken ct)
        {
            RepositoryCalls.Add((repo, provider));
            return Task.FromResult(new ConnectionTestResult(ConnectionTestOutcome.Ok, "Reached main.", null));
        }
    }

    [Fact]
    public async Task Test_probes_the_repository_with_its_stored_provider_credentials()
    {
        var tester = new RecordingConnectionTester();
        await using var factory = new ApiFactory(configureServices: services =>
        {
            services.RemoveAll<IConnectionTester>();
            services.AddSingleton<IConnectionTester>(tester);
        });
        var client = await factory.CreateAuthenticatedClientAsync();
        var (id, providerId) = await SeedRepositoryAsync(factory, "https://git.example.com/team/app.git", providerApiKey: "stored-key-1");

        var response = await client.PostAsync($"/api/v1/repositories/{id}/test", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (repo, provider) = Assert.Single(tester.RepositoryCalls);
        Assert.Equal(id, repo.Id.ToString());
        Assert.Equal("https://git.example.com/team/app.git", repo.CloneUrl);
        Assert.NotNull(provider);
        Assert.Equal(providerId, provider!.Id);
        Assert.Equal("stored-key-1", provider.ApiKey);
    }

    [Fact]
    public async Task Test_rejects_a_malformed_id_and_an_unknown_one()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var malformed = await client.PostAsync("/api/v1/repositories/not-a-guid/test", null, TestContext.Current.CancellationToken);
        var unknown = await client.PostAsync($"/api/v1/repositories/{Guid.NewGuid()}/test", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Test_is_for_signed_in_users_only()
    {
        await using var factory = new ApiFactory();
        var (id, _) = await SeedRepositoryAsync(factory, cloneUrl: "");

        var anonymous = await factory.CreateClient().PostAsync($"/api/v1/repositories/{id}/test", null, TestContext.Current.CancellationToken);
        var agent = await CreateAgentClient(factory).PostAsync($"/api/v1/repositories/{id}/test", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, agent.StatusCode);
    }

    /// <summary>
    /// The regression this whole scheme exists to prevent: user-facing endpoints
    /// that say nothing about authorization are refused to the agent token by
    /// default, so forgetting to think about an endpoint is safe. None of these
    /// carries an explicit agent check — the fallback policy is doing all the work.
    /// A 200 would mean the agent got in; a 500 would mean the refusal itself
    /// crashed, which is how the hand-rolled <c>Forbid()</c> calls used to fail.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/repositories")]
    [InlineData("/api/v1/workitems")]
    [InlineData("/api/v1/chat/history")]
    [InlineData("/api/v1/settings")]
    [InlineData("/api/v1/loopruns")]
    [InlineData("/api/v1/auth/me")]
    public async Task A_user_facing_endpoint_with_no_authorization_of_its_own_refuses_the_agent_token(string path)
    {
        await using var factory = new ApiFactory();
        var agent = CreateAgentClient(factory);

        var response = await agent.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
