using System.Net.Http.Json;
using System.Text;
using ILD.Core.Services.Remote;
using ILD.WorkItemServer.Dtos;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// The typed client against the live server, so the two halves of the attachment
/// contract cannot drift. The refusal cases matter most: every other call on
/// this client turns a non-success status into an <see cref="HttpRequestException"/>
/// its callers report as "WorkItemServer unreachable", which would bury the one
/// answer the user needs to see — which limit they broke.
/// </summary>
public sealed class WorkItemAttachmentClientTests
{
    private static async Task<string> CreateWorkItemAsync(HttpClient http)
    {
        var created = await http.PostAsJsonAsync("/workitems", new CreateWorkItemRequest { Title = "client attachments" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<WorkItemDto>())!.Id;
    }

    private static (WorkItemServerClient Client, WorkItemServerOptions Options, HttpClient Http) Connect(AttachmentServerFactory factory)
    {
        var http = factory.AuthedClient();
        return (new WorkItemServerClient(http), new WorkItemServerOptions
        {
            BaseUrl = "http://localhost",
            ApiKey = AttachmentServerFactory.ApiKey,
        }, http);
    }

    [Fact]
    public async Task Upload_list_download_and_delete_round_trip_through_the_client()
    {
        await using var factory = new AttachmentServerFactory();
        var (client, opts, http) = Connect(factory);
        using var _ = http;
        var id = await CreateWorkItemAsync(http);
        var bytes = Encoding.UTF8.GetBytes("a sketch, pretend");

        var upload = await client.UploadAttachmentsAsync(opts, id,
            new[] { new RemoteAttachmentUpload("sketch.png", "image/png", bytes) }, TestContext.Current.CancellationToken);

        Assert.Equal("Created", upload.Outcome.ToString());
        var created = Assert.Single(upload.Created);
        Assert.Equal("sketch.png", created.FileName);
        Assert.Equal("image/png", created.ContentType);
        Assert.Equal(bytes.Length, created.SizeBytes);

        var listed = await client.ListAttachmentsAsync(opts, id, TestContext.Current.CancellationToken);
        Assert.Equal(created.Id, Assert.Single(listed!).Id);

        var content = await client.GetAttachmentAsync(opts, id, created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(content);
        Assert.Equal(bytes, content!.Value.Content);
        Assert.Equal("image/png", content.Value.ContentType);
        Assert.Equal("sketch.png", content.Value.FileName);

        Assert.True(await client.DeleteAttachmentAsync(opts, id, created.Id, TestContext.Current.CancellationToken));
        Assert.Empty((await client.ListAttachmentsAsync(opts, id, TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task A_refused_upload_comes_back_as_an_outcome_carrying_the_servers_message()
    {
        await using var factory = new AttachmentServerFactory(maxAttachmentMb: 1);
        var (client, opts, http) = Connect(factory);
        using var _ = http;
        var id = await CreateWorkItemAsync(http);

        var upload = await client.UploadAttachmentsAsync(opts, id,
            new[] { new RemoteAttachmentUpload("big.bin", "application/octet-stream", AttachmentUpload.Bytes(2 * 1024 * 1024)) }, TestContext.Current.CancellationToken);

        Assert.NotEqual("Created", upload.Outcome.ToString());
        Assert.False(string.IsNullOrWhiteSpace(upload.Error));
        Assert.Empty(upload.Created);
    }

    [Fact]
    public async Task An_unknown_work_item_is_reported_as_not_found_rather_than_as_an_unreachable_server()
    {
        await using var factory = new AttachmentServerFactory();
        var (client, opts, http) = Connect(factory);
        using var _ = http;

        var upload = await client.UploadAttachmentsAsync(opts, "999999",
            new[] { new RemoteAttachmentUpload("x.txt", "text/plain", Encoding.UTF8.GetBytes("x")) }, TestContext.Current.CancellationToken);

        Assert.Equal("NotFound", upload.Outcome.ToString());
        Assert.Null(await client.ListAttachmentsAsync(opts, "999999", TestContext.Current.CancellationToken));
        Assert.Null(await client.GetAttachmentAsync(opts, "999999", Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.False(await client.DeleteAttachmentAsync(opts, "999999", Guid.NewGuid(), TestContext.Current.CancellationToken));
    }
}
