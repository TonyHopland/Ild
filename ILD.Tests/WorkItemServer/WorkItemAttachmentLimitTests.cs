using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.WorkItemServer.Attachments;
using ILD.WorkItemServer.Dtos;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// What the configured limits actually do to a request reaching the WorkItem
/// server directly — the boundary an ILD instance cannot speak for, because
/// anything holding an API key can call it without going through one.
///
/// Each case boots its own server: the limits are read once at startup, so
/// "the same file under a different setting" can only be two hosts.
/// </summary>
[Collection("AttachmentEnvironment")]
public sealed class WorkItemAttachmentLimitTests
{
    private const int Megabyte = 1024 * 1024;

    private static async Task<string> CreateWorkItemAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/workitems", new CreateWorkItemRequest { Title = "limits" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<WorkItemDto>())!.Id;
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string workItemId, params (string FileName, string? ContentType, byte[] Bytes)[] files)
    {
        using var body = AttachmentUpload.Of(files);
        return await client.PostAsync($"/workitems/{workItemId}/attachments", body);
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage resp)
    {
        var raw = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(raw).RootElement.GetProperty("error").GetString();
    }

    [Fact]
    public async Task A_file_over_the_configured_maximum_is_refused_with_400_and_nothing_is_stored()
    {
        await using var factory = new AttachmentServerFactory(maxAttachmentMb: 1);
        using var client = factory.AuthedClient();
        var id = await CreateWorkItemAsync(client);

        var resp = await UploadAsync(client, id, ("big.bin", "application/octet-stream", AttachmentUpload.Bytes(2 * Megabyte)));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await ErrorAsync(resp)));

        var listed = await client.GetFromJsonAsync<JsonElement>($"/workitems/{id}/attachments");
        Assert.Empty(listed.EnumerateArray().ToList());
    }

    [Fact]
    public async Task The_same_file_is_accepted_once_the_configured_maximum_is_raised()
    {
        var payload = AttachmentUpload.Bytes(2 * Megabyte);

        await using (var tight = new AttachmentServerFactory(maxAttachmentMb: 1))
        {
            using var client = tight.AuthedClient();
            var id = await CreateWorkItemAsync(client);
            var refused = await UploadAsync(client, id, ("same.bin", "application/octet-stream", payload));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        await using var roomy = new AttachmentServerFactory(maxAttachmentMb: 4);
        using var roomyClient = roomy.AuthedClient();
        var itemId = await CreateWorkItemAsync(roomyClient);

        var accepted = await UploadAsync(roomyClient, itemId, ("same.bin", "application/octet-stream", payload));

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    [Fact]
    public async Task An_upload_that_would_push_the_item_over_its_total_is_refused_with_400()
    {
        await using var factory = new AttachmentServerFactory(maxAttachmentMb: 1, maxTotalMb: 1);
        using var client = factory.AuthedClient();
        var id = await CreateWorkItemAsync(client);

        var first = await UploadAsync(client, id, ("first.bin", "application/octet-stream", AttachmentUpload.Bytes(600 * 1024)));
        var second = await UploadAsync(client, id, ("second.bin", "application/octet-stream", AttachmentUpload.Bytes(600 * 1024)));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        var error = await ErrorAsync(second);
        Assert.Contains("total", error!, StringComparison.OrdinalIgnoreCase);

        var listed = await client.GetFromJsonAsync<JsonElement>($"/workitems/{id}/attachments");
        Assert.Equal("first.bin", Assert.Single(listed.EnumerateArray().ToList()).GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task A_request_is_all_or_nothing_when_one_of_its_files_breaks_a_limit()
    {
        await using var factory = new AttachmentServerFactory(maxAttachmentMb: 1);
        using var client = factory.AuthedClient();
        var id = await CreateWorkItemAsync(client);

        var resp = await UploadAsync(client, id,
            ("fine.bin", "application/octet-stream", AttachmentUpload.Bytes(1024)),
            ("huge.bin", "application/octet-stream", AttachmentUpload.Bytes(2 * Megabyte)));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var listed = await client.GetFromJsonAsync<JsonElement>($"/workitems/{id}/attachments");
        Assert.Empty(listed.EnumerateArray().ToList());
    }

    [Fact]
    public void The_multipart_limits_are_derived_from_the_configured_per_file_maximum()
    {
        // Above the 30 MB a hosted server would otherwise cap a request at, so a
        // derived ceiling is distinguishable from a framework default.
        using var factory = new AttachmentServerFactory(maxAttachmentMb: 40);
        var expected = AttachmentLimits.FromEnvironment(name => name == "ILD_MAX_ATTACHMENT_MB" ? "40" : null);

        var form = factory.Services.GetRequiredService<IOptions<FormOptions>>().Value;

        Assert.True(expected.MaxRequestBytes > RecordingMaxRequestBodySizeFeature.HostDefault);
        Assert.Equal(expected.MaxRequestBytes, form.MultipartBodyLengthLimit);
        Assert.Equal(expected.MaxBytesPerFile, (long)form.MemoryBufferThreshold);
    }

    [Fact]
    public async Task Only_the_attachment_endpoint_raises_the_request_body_limit()
    {
        await using var factory = new AttachmentServerFactory(maxAttachmentMb: 40);
        using var client = factory.AuthedClient();
        var id = await CreateWorkItemAsync(client);
        var expected = AttachmentLimits.FromEnvironment(name => name == "ILD_MAX_ATTACHMENT_MB" ? "40" : null);

        using var upload = AttachmentUpload.Of("modest.png", "image/png", AttachmentUpload.Bytes(2048));
        var (uploadContext, uploadLimit) = await factory.Server.ObserveBodySizeLimitAsync(
            "POST", $"/workitems/{id}/attachments", AttachmentServerFactory.ApiKey, upload);
        var (listContext, listLimit) = await factory.Server.ObserveBodySizeLimitAsync(
            "GET", "/workitems", AttachmentServerFactory.ApiKey);

        Assert.Equal((int)HttpStatusCode.Created, uploadContext.Response.StatusCode);
        Assert.Equal(expected.MaxRequestBytes, uploadLimit.MaxRequestBodySize!.Value);
        Assert.Equal((int)HttpStatusCode.OK, listContext.Response.StatusCode);
        Assert.Equal(RecordingMaxRequestBodySizeFeature.HostDefault, listLimit.MaxRequestBodySize!.Value);
    }
}
