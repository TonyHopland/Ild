using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ILD.Api.Filters;
using ILD.Data;
using ILD.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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

    /// <summary>
    /// A multipart body written by hand, because <see cref="MultipartFormDataContent"/>
    /// refuses to produce the file names a real client legitimately sends — a blank
    /// one, or one holding the quote that names it <c>my "photo".png</c>.
    /// </summary>
    private static HttpContent RawUpload(params (string FileName, byte[] Bytes)[] files)
    {
        const string boundary = "ild-attachment-test-boundary";
        var body = new MemoryStream();
        foreach (var (fileName, bytes) in files)
        {
            var header = $"--{boundary}\r\nContent-Disposition: form-data; name=\"files\"; "
                + $"filename=\"{fileName.Replace("\"", "\\\"", StringComparison.Ordinal)}\"\r\n"
                + "Content-Type: application/octet-stream\r\n\r\n";
            body.Write(Encoding.UTF8.GetBytes(header));
            body.Write(bytes);
            body.Write("\r\n"u8);
        }
        body.Write(Encoding.UTF8.GetBytes($"--{boundary}--\r\n"));

        var content = new ByteArrayContent(body.ToArray());
        content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
        return content;
    }

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
    public async Task A_request_with_more_parts_than_the_form_reader_will_take_is_refused_and_stores_nothing()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var id = await CreateWorkItemAsync(factory, client);

        // Far past the ten this endpoint accepts, and past the 1024 parts the form
        // reader will parse at all: the refusal then comes from the framework
        // rather than from the limit check, and it still has to be the caller's
        // answer — a 400 saying why, with nothing stored.
        var files = Enumerable.Range(0, 1200).Select(i => ($"f{i}.bin", new byte[] { (byte)i })).ToArray();
        using var body = RawUpload(files);
        var resp = await client.PostAsync($"/api/v1/workitems/{id}/attachments", body);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("form", await resp.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var listed = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workitems/{id}/attachments");
        Assert.Empty(listed.EnumerateArray().ToList());
    }

    [Fact]
    public async Task Only_the_upload_endpoint_raises_its_body_cap_and_it_does_so_before_the_body_is_read()
    {
        // MVC parses a form request for its value providers before the action
        // runs, so a cap raised inside the action arrives after the host default
        // has already decided. It has to be a filter, and only here.
        await using var factory = new ApiFactory();
        _ = factory.CreateClient();

        var raising = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<RaiseAttachmentBodyLimitAttribute>() != null)
            .Select(e => $"{string.Join('|', e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])} {e.RoutePattern.RawText}")
            .ToList();

        Assert.Equal(new[] { "POST api/v1/WorkItems/{id}/attachments" }, raising);
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
