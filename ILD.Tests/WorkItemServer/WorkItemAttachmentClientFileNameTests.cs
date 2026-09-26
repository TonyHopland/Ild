using System.Net.Http.Json;
using System.Text;
using ILD.Core.Services.Remote;
using ILD.WorkItemServer.Dtos;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// File names a person's machine produces and a multipart header cannot carry
/// verbatim. <c>MultipartFormDataContent</c> throws on a blank name and on one
/// holding a quote or a newline, which would leave the upload as a 500 from the
/// ILD API — so the client makes the name safe and the file still lands.
/// </summary>
public sealed class WorkItemAttachmentClientFileNameTests
{
    [Theory]
    [InlineData("my \"photo\".png", "my _photo_.png")]
    [InlineData("two\nlines.png", "two_lines.png")]
    [InlineData("", "attachment")]
    [InlineData("   ", "attachment")]
    public async Task A_name_a_header_cannot_carry_is_made_safe_rather_than_refused(string sent, string stored)
    {
        await using var factory = new AttachmentServerFactory();
        using var http = factory.AuthedClient();
        var client = new WorkItemServerClient(http);
        var options = new WorkItemServerOptions { BaseUrl = "http://localhost", ApiKey = AttachmentServerFactory.ApiKey };

        var created = await http.PostAsJsonAsync("/workitems", new CreateWorkItemRequest { Title = "awkward names" }, cancellationToken: TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<WorkItemDto>(TestContext.Current.CancellationToken))!.Id;
        var bytes = Encoding.UTF8.GetBytes("the file itself is fine");

        var upload = await client.UploadAttachmentsAsync(
            options, id, new[] { new RemoteAttachmentUpload(sent, "image/png", bytes) }, TestContext.Current.CancellationToken);

        Assert.Equal("Created", upload.Outcome.ToString());
        Assert.Equal(stored, Assert.Single(upload.Created).FileName);

        // The name is the only thing that changed: the file comes back whole.
        var content = await client.GetAttachmentAsync(options, id, upload.Created[0].Id, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, content!.Value.Content);
    }
}
