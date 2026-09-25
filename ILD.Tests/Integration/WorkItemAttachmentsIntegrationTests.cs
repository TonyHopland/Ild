using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ILD.Data;
using ILD.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// The human-facing attachment surface of the ILD API, proxied to the WorkItem
/// server. Everything here is exercised over HTTP with no UI present — the UI
/// lands separately and builds against exactly these routes.
/// </summary>
public class WorkItemAttachmentsIntegrationTests
{
    private const int Megabyte = 1024 * 1024;

    private static async Task<Guid> SeedRepositoryAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var provider = new RemoteProvider
        {
            Id = Guid.NewGuid(),
            Name = "attachments-provider",
            Type = "forgejo",
            Url = "https://example.invalid",
            CreatedAt = DateTime.UtcNow,
        };
        db.RemoteProviders.Add(provider);
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = "attachments-repo",
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
            title = "carries attachments",
            description = "",
            repositoryId = repositoryId.ToString(),
        });
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string workItemId, params (string FileName, string? ContentType, byte[] Bytes)[] files)
    {
        using var body = AttachmentUpload.Of(files);
        return await client.PostAsync($"/api/v1/workitems/{workItemId}/attachments", body);
    }

    [Fact]
    public async Task Upload_list_download_and_delete_round_trip_over_the_api()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var id = await CreateWorkItemAsync(factory, client);
        var bytes = Encoding.UTF8.GetBytes("pretend this is a screenshot");

        var upload = await UploadAsync(client, id, ("screenshot.png", "image/png", bytes));
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var created = Assert.Single(
            JsonDocument.Parse(await upload.Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToList());
        var attachmentId = created.GetProperty("id").GetGuid();
        Assert.Equal("screenshot.png", created.GetProperty("fileName").GetString());

        var list = await client.GetAsync($"/api/v1/workitems/{id}/attachments");
        list.EnsureSuccessStatusCode();
        var listedRaw = await list.Content.ReadAsStringAsync();
        var listed = Assert.Single(JsonDocument.Parse(listedRaw).RootElement.EnumerateArray().ToList());
        Assert.Equal(attachmentId, listed.GetProperty("id").GetGuid());
        Assert.Equal(bytes.Length, listed.GetProperty("sizeBytes").GetInt64());
        Assert.DoesNotContain(Convert.ToBase64String(bytes), listedRaw, StringComparison.Ordinal);

        var download = await client.GetAsync($"/api/v1/workitems/{id}/attachments/{attachmentId}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", download.Content.Headers.ContentType?.MediaType);

        var deleted = await client.DeleteAsync($"/api/v1/workitems/{id}/attachments/{attachmentId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var empty = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workitems/{id}/attachments");
        Assert.Empty(empty.EnumerateArray().ToList());
    }

    [Fact]
    public async Task A_download_is_served_as_an_attachment_that_cannot_be_sniffed()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var id = await CreateWorkItemAsync(factory, client);
        var page = Encoding.UTF8.GetBytes("<html><script>alert(document.cookie)</script></html>");

        var upload = await UploadAsync(client, id, ("evil.html", "text/html", page));
        var attachmentId = JsonDocument.Parse(await upload.Content.ReadAsStringAsync())
            .RootElement[0].GetProperty("id").GetGuid();

        var download = await client.GetAsync($"/api/v1/workitems/{id}/attachments/{attachmentId}");

        // Uploaded markup must not be able to run on the ILD origin.
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("evil.html", download.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task Every_attachment_route_refuses_a_caller_with_no_session()
    {
        await using var factory = new ApiFactory();
        var signedIn = await factory.CreateAuthenticatedClientAsync();
        var anonymous = factory.CreateClient();
        // A route that exists, so a 401 cannot merely mean "no such route".
        var id = await CreateWorkItemAsync(factory, signedIn);
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync($"/api/v1/workitems/{id}/attachments")).StatusCode);

        using var body = AttachmentUpload.Of("x.txt", "text/plain", Encoding.UTF8.GetBytes("x"));
        var upload = await anonymous.PostAsync($"/api/v1/workitems/{id}/attachments", body);
        var list = await anonymous.GetAsync($"/api/v1/workitems/{id}/attachments");
        var download = await anonymous.GetAsync($"/api/v1/workitems/{id}/attachments/{Guid.NewGuid()}");
        var delete = await anonymous.DeleteAsync($"/api/v1/workitems/{id}/attachments/{Guid.NewGuid()}");
        var settings = await anonymous.GetAsync("/api/v1/settings/attachments");

        Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, download.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, settings.StatusCode);
    }

    [Fact]
    public async Task An_unknown_work_item_is_404_on_every_attachment_route()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var unknown = "999999";
        // A route that exists, so a 404 cannot merely mean "no such route".
        var known = await CreateWorkItemAsync(factory, client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/workitems/{known}/attachments")).StatusCode);

        var upload = await UploadAsync(client, unknown, ("x.txt", "text/plain", Encoding.UTF8.GetBytes("x")));
        var list = await client.GetAsync($"/api/v1/workitems/{unknown}/attachments");
        var download = await client.GetAsync($"/api/v1/workitems/{unknown}/attachments/{Guid.NewGuid()}");
        var delete = await client.DeleteAsync($"/api/v1/workitems/{unknown}/attachments/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, upload.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task An_unknown_attachment_id_is_404()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var id = await CreateWorkItemAsync(factory, client);
        var upload = await UploadAsync(client, id, ("real.bin", "application/octet-stream", AttachmentUpload.Bytes(8)));
        var real = JsonDocument.Parse(await upload.Content.ReadAsStringAsync()).RootElement[0].GetProperty("id").GetGuid();

        var served = await client.GetAsync($"/api/v1/workitems/{id}/attachments/{real}");
        var download = await client.GetAsync($"/api/v1/workitems/{id}/attachments/{Guid.NewGuid()}");
        var delete = await client.DeleteAsync($"/api/v1/workitems/{id}/attachments/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task The_limits_endpoint_reports_the_defaults_a_client_has_to_enforce_before_uploading()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var limits = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/attachments");

        Assert.Equal(25L * Megabyte, limits.GetProperty("maxBytesPerFile").GetInt64());
        Assert.Equal(10, limits.GetProperty("maxFilesPerRequest").GetInt32());
        Assert.Equal(250L * Megabyte, limits.GetProperty("maxTotalBytesPerWorkItem").GetInt64());
    }

    [Fact]
    public async Task The_limits_endpoint_follows_the_configured_values()
    {
        await using var factory = new ApiFactory(environment: new Dictionary<string, string?>
        {
            ["ILD_MAX_ATTACHMENT_MB"] = "3",
            ["ILD_MAX_ATTACHMENTS_TOTAL_MB"] = "9",
        });
        var client = await factory.CreateAuthenticatedClientAsync();

        var limits = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/attachments");

        Assert.Equal(3L * Megabyte, limits.GetProperty("maxBytesPerFile").GetInt64());
        Assert.Equal(9L * Megabyte, limits.GetProperty("maxTotalBytesPerWorkItem").GetInt64());
    }

    [Fact]
    public async Task A_file_over_the_configured_maximum_is_refused_with_400_by_the_api_itself()
    {
        await using var factory = new ApiFactory(environment: new Dictionary<string, string?>
        {
            ["ILD_MAX_ATTACHMENT_MB"] = "1",
        });
        var client = await factory.CreateAuthenticatedClientAsync();
        var id = await CreateWorkItemAsync(factory, client);

        var resp = await UploadAsync(client, id, ("big.bin", "application/octet-stream", AttachmentUpload.Bytes(2 * Megabyte)));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var error = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString();
        Assert.False(string.IsNullOrWhiteSpace(error));

        var listed = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workitems/{id}/attachments");
        Assert.Empty(listed.EnumerateArray().ToList());
    }

    [Fact]
    public async Task More_than_ten_files_in_one_request_is_refused_with_400_by_the_api_itself()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var id = await CreateWorkItemAsync(factory, client);
        var files = Enumerable.Range(0, 11)
            .Select(i => ($"f{i}.bin", (string?)"application/octet-stream", AttachmentUpload.Bytes(16, (byte)i)))
            .ToArray();

        var resp = await UploadAsync(client, id, files);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var error = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString();
        Assert.Contains("10", error!, StringComparison.Ordinal);

        var listed = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workitems/{id}/attachments");
        Assert.Empty(listed.EnumerateArray().ToList());
    }

    [Fact]
    public async Task The_multipart_limits_are_derived_from_the_configured_per_file_maximum()
    {
        // 40 MB per file puts the derived ceiling above the 30 MB a hosted server
        // caps a request at by default, which is the whole point of deriving it.
        await using var factory = new ApiFactory(environment: new Dictionary<string, string?>
        {
            ["ILD_MAX_ATTACHMENT_MB"] = "40",
        });
        var expected = ILD.Core.Services.Attachments.AttachmentLimits.FromEnvironment(
            name => name == "ILD_MAX_ATTACHMENT_MB" ? "40" : null);

        var form = factory.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Features.FormOptions>>().Value;

        Assert.True(expected.MaxRequestBytes > RecordingMaxRequestBodySizeFeature.HostDefault);
        Assert.Equal(expected.MaxRequestBytes, form.MultipartBodyLengthLimit);
        Assert.Equal(expected.MaxBytesPerFile, (long)form.MemoryBufferThreshold);
    }

    [Fact]
    public async Task Only_the_attachment_endpoint_raises_the_request_body_limit()
    {
        await using var factory = new ApiFactory(environment: new Dictionary<string, string?>
        {
            ["ILD_MAX_ATTACHMENT_MB"] = "40",
        });
        var client = await factory.CreateAuthenticatedClientAsync();
        var token = await factory.GetAdminTokenAsync();
        var id = await CreateWorkItemAsync(factory, client);
        var expected = ILD.Core.Services.Attachments.AttachmentLimits.FromEnvironment(
            name => name == "ILD_MAX_ATTACHMENT_MB" ? "40" : null);

        using var body = AttachmentUpload.Of("modest.png", "image/png", AttachmentUpload.Bytes(2048));
        var (uploadContext, uploadLimit) = await factory.Server.ObserveBodySizeLimitAsync(
            "POST", $"/api/v1/workitems/{id}/attachments", token, body);
        var (listContext, listLimit) = await factory.Server.ObserveBodySizeLimitAsync(
            "GET", $"/api/v1/workitems/{id}/attachments", token);

        Assert.Equal((int)HttpStatusCode.Created, uploadContext.Response.StatusCode);
        Assert.Equal(expected.MaxRequestBytes, uploadLimit.MaxRequestBodySize!.Value);
        Assert.Equal((int)HttpStatusCode.OK, listContext.Response.StatusCode);
        Assert.Equal(RecordingMaxRequestBodySizeFeature.HostDefault, listLimit.MaxRequestBodySize!.Value);
    }

    [Fact]
    public async Task A_per_item_total_the_work_item_server_refuses_is_reported_as_400_and_not_as_an_outage()
    {
        // The API cannot know an item's stored total without asking, so this 400
        // arrives from the WorkItem server. Every other failure of that call is
        // mapped to 503 "WorkItemServer unreachable", which would tell the user
        // their instance is down when in fact their upload was simply too big.
        await using var factory = new ApiFactory(environment: new Dictionary<string, string?>
        {
            ["ILD_MAX_ATTACHMENT_MB"] = "1",
            ["ILD_MAX_ATTACHMENTS_TOTAL_MB"] = "1",
        });
        var client = await factory.CreateAuthenticatedClientAsync();
        var id = await CreateWorkItemAsync(factory, client);

        var first = await UploadAsync(client, id, ("first.bin", "application/octet-stream", AttachmentUpload.Bytes(600 * 1024)));
        var second = await UploadAsync(client, id, ("second.bin", "application/octet-stream", AttachmentUpload.Bytes(600 * 1024)));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        var error = JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString();
        Assert.Contains("total", error!, StringComparison.OrdinalIgnoreCase);

        var listed = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workitems/{id}/attachments");
        Assert.Equal("first.bin", Assert.Single(listed.EnumerateArray().ToList()).GetProperty("fileName").GetString());
    }
}
