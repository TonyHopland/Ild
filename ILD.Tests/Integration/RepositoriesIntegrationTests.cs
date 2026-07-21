using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

[Collection("AuthEnvironment")]
public class RepositoriesIntegrationTests
{
    [Fact]
    public async Task GetAll_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/repositories");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAll_with_token_returns_200_and_empty_array()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/repositories");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<object[]>();
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
        var createResponse = await client.PostAsJsonAsync("/api/v1/repositories", NewRepoPayload(providerId, env));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString()!;
        // Masked: the plaintext is never returned, only whether one is set.
        Assert.True(created.GetProperty("hasPreviewEnv").GetBoolean());
        Assert.False(created.TryGetProperty("previewEnv", out _));

        // GET is masked the same way, but the value is persisted in the store.
        var getResponse = await client.GetAsync($"/api/v1/repositories/{id}");
        var fetched = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
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
        var createResponse = await client.PostAsJsonAsync("/api/v1/repositories", NewRepoPayload(providerId, env));
        var id = (await createResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        // A normal edit that leaves the .env textarea blank must not wipe the secret
        // (mirrors the provider API-key masking).
        var updateResponse = await client.PutAsJsonAsync($"/api/v1/repositories/{id}", NewRepoPayload(providerId, previewEnv: null));
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(updated.GetProperty("hasPreviewEnv").GetBoolean());
        Assert.Equal(env, await ReadStoredPreviewEnvAsync(factory, id));
    }

    [Fact]
    public async Task Update_with_a_new_preview_env_replaces_the_stored_value()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var providerId = await SeedProviderAsync(factory);

        var createResponse = await client.PostAsJsonAsync("/api/v1/repositories", NewRepoPayload(providerId, "OLD=1"));
        var id = (await createResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        const string newEnv = "NEW=2\nEXTRA=3";
        var updateResponse = await client.PutAsJsonAsync($"/api/v1/repositories/{id}", NewRepoPayload(providerId, newEnv));
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.Equal(newEnv, await ReadStoredPreviewEnvAsync(factory, id));
    }
}
