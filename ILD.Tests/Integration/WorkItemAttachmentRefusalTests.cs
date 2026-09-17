using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Data;
using ILD.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// Uploads the ILD API turns away before anything reaches the WorkItem server:
/// a request that is not an upload at all, and one that carries no files. Both
/// would otherwise cross the network to be refused there, or come back as an
/// empty success that stored nothing.
/// </summary>
[Collection("AttachmentEnvironment")]
public class WorkItemAttachmentRefusalTests
{
    private static async Task<string> CreateWorkItemAsync(ApiFactory factory, HttpClient client)
    {
        Guid repositoryId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var provider = new RemoteProvider
            {
                Id = Guid.NewGuid(),
                Name = "refusal-provider",
                Type = "forgejo",
                Url = "https://example.invalid",
                CreatedAt = DateTime.UtcNow,
            };
            db.RemoteProviders.Add(provider);
            var repo = new Repository
            {
                Id = Guid.NewGuid(),
                Name = "refusal-repo",
                CloneUrl = "https://example.invalid/repo.git",
                RemoteProviderId = provider.Id,
                CreatedAt = DateTime.UtcNow,
            };
            db.Repositories.Add(repo);
            await db.SaveChangesAsync();
            repositoryId = repo.Id;
        }

        var resp = await client.PostAsJsonAsync("/api/v1/workitems", new
        {
            title = "refuses bad uploads",
            description = "",
            repositoryId = repositoryId.ToString(),
        });
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage resp)
        => JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString();

    [Fact]
    public async Task An_upload_carrying_no_files_is_refused_rather_than_answered_with_nothing()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var id = await CreateWorkItemAsync(factory, client);

        // A well-formed form that simply has no file in it — the shape a client
        // sends when it posts the surrounding fields and forgets the files.
        using var fieldsOnly = new MultipartFormDataContent { { new StringContent("a note"), "note" } };
        var resp = await client.PostAsync($"/api/v1/workitems/{id}/attachments", fieldsOnly);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await ErrorAsync(resp)));
    }

    [Fact]
    public async Task A_request_that_is_not_multipart_is_refused_with_the_field_it_wanted()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var id = await CreateWorkItemAsync(factory, client);

        var resp = await client.PostAsJsonAsync($"/api/v1/workitems/{id}/attachments", new { files = "not a file" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("files", (await ErrorAsync(resp))!, StringComparison.Ordinal);
    }
}
