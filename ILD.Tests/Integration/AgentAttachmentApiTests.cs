using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ILD.Data;
using ILD.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// What an agent can see and do with attachments: the metadata arrives on the
/// work item it already reads, the bytes come from one read-only route, and
/// there is nothing on this surface that writes — an agent may not add or remove
/// a human's files.
/// </summary>
public class AgentAttachmentApiTests
{
    private static async Task<Guid> SeedRepositoryAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var provider = new RemoteProvider
        {
            Id = Guid.NewGuid(),
            Name = "agent-attachments-provider",
            Type = "forgejo",
            Url = "https://example.invalid",
            CreatedAt = DateTime.UtcNow,
        };
        db.RemoteProviders.Add(provider);
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = "agent-attachments-repo",
            CloneUrl = "https://example.invalid/repo.git",
            RemoteProviderId = provider.Id,
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(repo);
        await db.SaveChangesAsync();
        return repo.Id;
    }

    private static async Task<string> CreateWorkItemAsync(ApiFactory factory, HttpClient client)
    {
        var repositoryId = await SeedRepositoryAsync(factory);
        var resp = await client.PostAsJsonAsync("/api/v1/workitems", new
        {
            title = "an agent reads this",
            description = "",
            repositoryId = repositoryId.ToString(),
        });
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<Guid> UploadAsync(HttpClient client, string workItemId, string fileName, string contentType, byte[] bytes)
    {
        using var body = AttachmentUpload.Of(fileName, contentType, bytes);
        var resp = await client.PostAsync($"/api/v1/workitems/{workItemId}/attachments", body);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement[0].GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Get_workitem_carries_the_attachment_metadata_and_never_the_bytes()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var id = await CreateWorkItemAsync(factory, client);
        var bytes = Encoding.UTF8.GetBytes("a sketch of the layout");
        var attachmentId = await UploadAsync(client, id, "sketch.png", "image/png", bytes);

        var resp = await client.GetAsync($"/api/v1/agent/workitems/{id}");
        resp.EnsureSuccessStatusCode();
        var raw = await resp.Content.ReadAsStringAsync();
        var item = JsonDocument.Parse(raw).RootElement;

        var attachment = Assert.Single(item.GetProperty("attachments").EnumerateArray().ToList());
        Assert.Equal(attachmentId, attachment.GetProperty("id").GetGuid());
        Assert.Equal("sketch.png", attachment.GetProperty("fileName").GetString());
        Assert.Equal("image/png", attachment.GetProperty("contentType").GetString());
        Assert.Equal(bytes.Length, attachment.GetProperty("sizeBytes").GetInt64());
        Assert.DoesNotContain(Convert.ToBase64String(bytes), raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_item_with_no_attachments_reports_an_empty_array_rather_than_nothing()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var id = await CreateWorkItemAsync(factory, client);

        var item = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent/workitems/{id}");

        Assert.True(item.TryGetProperty("attachments", out var attachments));
        Assert.Equal(JsonValueKind.Array, attachments.ValueKind);
        Assert.Empty(attachments.EnumerateArray().ToList());
    }

    [Fact]
    public async Task An_agent_can_download_an_attachment_it_saw_on_the_work_item()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var id = await CreateWorkItemAsync(factory, client);
        var bytes = AttachmentUpload.Bytes(512);
        var attachmentId = await UploadAsync(client, id, "diagram.png", "image/png", bytes);

        var download = await client.GetAsync($"/api/v1/agent/workitems/{id}/attachments/{attachmentId}");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task The_agent_download_route_is_404_for_an_unknown_item_or_attachment()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var id = await CreateWorkItemAsync(factory, client);
        var real = await UploadAsync(client, id, "real.png", "image/png", AttachmentUpload.Bytes(8));

        // A route that exists, so a 404 cannot merely mean "no such route".
        var served = await client.GetAsync($"/api/v1/agent/workitems/{id}/attachments/{real}");
        var unknownItem = await client.GetAsync($"/api/v1/agent/workitems/999999/attachments/{Guid.NewGuid()}");
        var unknownAttachment = await client.GetAsync($"/api/v1/agent/workitems/{id}/attachments/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownItem.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownAttachment.StatusCode);
    }

    [Fact]
    public async Task The_agent_surface_reads_attachments_and_writes_none()
    {
        await using var factory = new ApiFactory();
        _ = factory.CreateClient();

        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } route
                        && route.Contains("api/v1/agent", StringComparison.OrdinalIgnoreCase)
                        && route.Contains("attachment", StringComparison.OrdinalIgnoreCase))
            .Select(e => (
                Route: e.RoutePattern.RawText!,
                Methods: e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? Array.Empty<string>()))
            .ToList();

        Assert.NotEmpty(routes);
        Assert.All(routes, r => Assert.All(r.Methods, m => Assert.True(
            m is "GET" or "HEAD", $"{m} {r.Route} writes an attachment from the agent surface")));
    }
}
