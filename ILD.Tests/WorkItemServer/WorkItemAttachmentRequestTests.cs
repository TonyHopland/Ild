using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ILD.WorkItemServer.Attachments;
using ILD.WorkItemServer.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// Requests that are not an upload at all, and file names that are not names.
/// Everything here is a caller mistake the server has to answer with a reason
/// rather than a 500 from the database or an empty 201 that stored nothing.
/// </summary>
[Collection("AttachmentEnvironment")]
public sealed class WorkItemAttachmentRequestTests : IAsyncLifetime
{
    private AttachmentServerFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new AttachmentServerFactory();
        _client = _factory.AuthedClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<string> CreateWorkItemAsync()
    {
        var created = await _client.PostAsJsonAsync("/workitems", new CreateWorkItemRequest { Title = "request shapes" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<WorkItemDto>())!.Id;
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage resp)
        => JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString();

    [Fact]
    public async Task An_upload_carrying_no_files_is_refused_rather_than_answered_with_nothing()
    {
        var id = await CreateWorkItemAsync();

        // A well-formed form that simply has no file in it — the shape a client
        // sends when it posts the surrounding fields and forgets the files.
        using var fieldsOnly = new MultipartFormDataContent { { new StringContent("a note"), "note" } };
        var resp = await _client.PostAsync($"/workitems/{id}/attachments", fieldsOnly);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await ErrorAsync(resp)));
    }

    [Fact]
    public async Task A_request_that_is_not_multipart_is_refused_with_the_field_it_wanted()
    {
        var id = await CreateWorkItemAsync();

        var resp = await _client.PostAsJsonAsync($"/workitems/{id}/attachments", new { files = "not a file" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("files", (await ErrorAsync(resp))!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_with_more_parts_than_the_form_reader_will_take_is_refused_and_stores_nothing()
    {
        var id = await CreateWorkItemAsync();

        // Far past the ten this server accepts, and past the 1024 parts the form
        // reader will parse at all: the refusal then comes from the framework
        // rather than from the limit check, and it still has to be the caller's
        // answer — a 400 saying why, with nothing stored.
        var files = Enumerable.Range(0, 1200)
            .Select(i => ($"f{i}.bin", (string?)"application/octet-stream", new byte[] { (byte)i }))
            .ToArray();

        using var body = AttachmentUpload.Of(files);
        var resp = await _client.PostAsync($"/workitems/{id}/attachments", body);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("form", await resp.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var listed = await _client.GetFromJsonAsync<JsonElement>($"/workitems/{id}/attachments");
        Assert.Empty(listed.EnumerateArray().ToList());
    }

    [Fact]
    public void Only_the_upload_endpoint_raises_its_body_cap_and_it_does_so_before_the_body_is_read()
    {
        // MVC parses a form request for its value providers before the action
        // runs, so a cap raised inside the action arrives after the host default
        // has already decided. It has to be a filter, and only here.
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .ToList();
        var raising = endpoints
            .Where(e => e.Metadata.GetMetadata<RaiseAttachmentBodyLimitAttribute>() != null)
            .Select(e => $"{string.Join('|', e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])} {e.RoutePattern.RawText}")
            .ToList();

        Assert.Equal(new[] { "POST workitems/{id}/attachments" }, raising);
    }

    [Fact]
    public async Task A_file_name_carrying_a_path_is_stored_as_its_last_segment()
    {
        var id = await CreateWorkItemAsync();

        using var body = AttachmentUpload.Of("../../etc/passwd", "text/plain", Encoding.UTF8.GetBytes("root"));
        var resp = await _client.PostAsync($"/workitems/{id}/attachments", body);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement[0];
        Assert.Equal("passwd", created.GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task A_file_name_longer_than_the_column_is_refused_rather_than_truncated()
    {
        var id = await CreateWorkItemAsync();

        using var body = AttachmentUpload.Of(new string('a', 300) + ".png", "image/png", AttachmentUpload.Bytes(16));
        var resp = await _client.PostAsync($"/workitems/{id}/attachments", body);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await ErrorAsync(resp)));

        var listed = await _client.GetFromJsonAsync<JsonElement>($"/workitems/{id}/attachments");
        Assert.Empty(listed.EnumerateArray().ToList());
    }
}
