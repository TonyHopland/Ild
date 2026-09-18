using System.Net;
using ILD.Core.Services.Remote;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// What the typed client makes of an answer that is neither success nor "no such
/// thing". Every attachment call turns it into the <see cref="HttpRequestException"/>
/// its callers report as an outage — delete included, which would otherwise be
/// the one call that quietly reports a failing server as a file already gone.
/// </summary>
public sealed class WorkItemAttachmentClientOutageTests
{
    private static readonly Guid AttachmentId = Guid.NewGuid();

    private static (WorkItemServerClient Client, WorkItemServerOptions Options) Answering(HttpStatusCode status)
        => (new WorkItemServerClient(new HttpClient(new FixedResponseHandler(status)) { BaseAddress = new Uri("http://workitem-server:8081") }),
            new WorkItemServerOptions { BaseUrl = "http://workitem-server:8081", ApiKey = "k" });

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_delete_the_server_could_not_answer_is_not_reported_as_a_missing_attachment(HttpStatusCode status)
    {
        var (client, options) = Answering(status);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DeleteAttachmentAsync(options, "42", AttachmentId));
    }

    [Fact]
    public async Task A_delete_of_something_that_is_not_there_is_reported_as_not_found()
    {
        var (client, options) = Answering(HttpStatusCode.NotFound);

        Assert.False(await client.DeleteAttachmentAsync(options, "42", AttachmentId));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_listing_or_a_download_the_server_could_not_answer_is_an_outage(HttpStatusCode status)
    {
        var (client, options) = Answering(status);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ListAttachmentsAsync(options, "42"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAttachmentAsync(options, "42", AttachmentId));
    }

    private sealed class FixedResponseHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("") });
    }
}
