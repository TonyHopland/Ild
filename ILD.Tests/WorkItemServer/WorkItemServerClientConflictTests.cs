using System.Net;
using ILD.Core.Services.Remote;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// How the typed client translates the WorkItem server's answers on the
/// attachment endpoints.
///
/// <para>
/// A 409 means the server refused the change because other writers kept winning
/// the race for the item's list — nothing stored, nothing lost, worth retrying,
/// and a different thing from a missing work item. Untranslated it reaches the
/// API as an opaque 500 on upload (the client's generic failure path throws and
/// nothing catches it) and as a 404 on delete, which tells the caller an
/// attachment that is still there has gone.
/// </para>
/// </summary>
public sealed class WorkItemServerClientConflictTests
{
    private static readonly WorkItemServerOptions Opts =
        new() { BaseUrl = "http://localhost", ApiKey = "k" };

    private static WorkItemServerClient ClientAnswering(HttpStatusCode status)
        => new(new HttpClient(new StubHandler(status)));

    [Fact]
    public async Task An_upload_the_server_refused_as_conflicting_is_retryable()
        => await Assert.ThrowsAsync<RemoteAttachmentConflictException>(
            () => ClientAnswering(HttpStatusCode.Conflict)
                .AddAttachmentAsync(Opts, "47", "sketch.png", "image/png", new MemoryStream([1])));

    [Fact]
    public async Task A_removal_the_server_refused_as_conflicting_is_retryable()
        => await Assert.ThrowsAsync<RemoteAttachmentConflictException>(
            () => ClientAnswering(HttpStatusCode.Conflict).DeleteAttachmentAsync(Opts, "47", "a1"));

    [Fact]
    public async Task A_missing_work_item_still_reads_as_missing_rather_than_as_a_conflict()
    {
        Assert.Null(await ClientAnswering(HttpStatusCode.NotFound)
            .AddAttachmentAsync(Opts, "47", "sketch.png", "image/png", new MemoryStream([1])));
        Assert.False(await ClientAnswering(HttpStatusCode.NotFound).DeleteAttachmentAsync(Opts, "47", "a1"));
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status));
    }
}
