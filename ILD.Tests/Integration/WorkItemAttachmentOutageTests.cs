using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ILD.Core.Services.Remote;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests.Integration;

/// <summary>
/// What every attachment route answers when the WorkItem server — which holds
/// the bytes — cannot be reached. It is a 503 naming the outage, never a 500 and
/// never a 404: a caller told "no such attachment" goes looking for a file that
/// is sitting safely in a database nobody can currently reach, and a delete
/// answered 204 or 404 would have them believe it is gone.
/// </summary>
public class WorkItemAttachmentOutageTests
{
    private const string WorkItemId = "42";

    /// <summary>An ILD API whose WorkItem server refuses every attachment call the way a dead one does.</summary>
    private static ApiFactory UnreachableWorkItemServer()
    {
        var outage = new HttpRequestException("Connection refused (workitem-server:8081)");
        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.ListAttachmentsAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(outage);
        client.Setup(c => c.UploadAttachmentsAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<RemoteAttachmentUpload>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(outage);
        client.Setup(c => c.GetAttachmentAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(outage);
        client.Setup(c => c.DeleteAttachmentAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(outage);

        return new ApiFactory(configureServices: services => services.ReplaceSingleton(client.Object));
    }

    private static async Task AssertReportsTheOutageAsync(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("WorkItemServer unreachable", body.GetProperty("error").GetString());
        Assert.Contains("Connection refused", body.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_attachments_while_the_server_is_unreachable_is_reported_as_the_outage()
    {
        await using var factory = UnreachableWorkItemServer();
        var client = await factory.CreateAuthenticatedClientAsync();

        await AssertReportsTheOutageAsync(await client.GetAsync($"/api/v1/workitems/{WorkItemId}/attachments"));
    }

    [Fact]
    public async Task Uploading_while_the_server_is_unreachable_is_reported_as_the_outage_and_not_as_a_refusal()
    {
        await using var factory = UnreachableWorkItemServer();
        var client = await factory.CreateAuthenticatedClientAsync();

        using var body = AttachmentUpload.Of("sketch.png", "image/png", Encoding.UTF8.GetBytes("a sketch"));
        await AssertReportsTheOutageAsync(await client.PostAsync($"/api/v1/workitems/{WorkItemId}/attachments", body));
    }

    [Fact]
    public async Task Downloading_while_the_server_is_unreachable_is_reported_as_the_outage()
    {
        await using var factory = UnreachableWorkItemServer();
        var client = await factory.CreateAuthenticatedClientAsync();

        await AssertReportsTheOutageAsync(
            await client.GetAsync($"/api/v1/workitems/{WorkItemId}/attachments/{Guid.NewGuid()}"));
    }

    [Fact]
    public async Task Deleting_while_the_server_is_unreachable_is_the_outage_and_never_a_404()
    {
        await using var factory = UnreachableWorkItemServer();
        var client = await factory.CreateAuthenticatedClientAsync();

        var resp = await client.DeleteAsync($"/api/v1/workitems/{WorkItemId}/attachments/{Guid.NewGuid()}");

        Assert.NotEqual(HttpStatusCode.NotFound, resp.StatusCode);
        await AssertReportsTheOutageAsync(resp);
    }

    [Fact]
    public async Task An_agent_downloading_while_the_server_is_unreachable_is_told_so_too()
    {
        await using var factory = UnreachableWorkItemServer();
        var client = await factory.CreateAuthenticatedClientAsync();

        await AssertReportsTheOutageAsync(
            await client.GetAsync($"/api/v1/agent/workitems/{WorkItemId}/attachments/{Guid.NewGuid()}"));
    }
}
