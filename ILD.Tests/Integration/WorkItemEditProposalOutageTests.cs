using System.Net;
using System.Text.Json;
using ILD.Core.Services.Remote;
using Moq;

namespace ILD.Tests.Integration;

/// <summary>
/// The human edit-proposal routes when the WorkItem server cannot be reached:
/// an outage is a 503 naming it, and an approve the server already applied is
/// never reported as failed because a read that follows it could not be made.
/// </summary>
public class WorkItemEditProposalOutageTests
{
    private const string WorkItemId = "42";

    private static readonly HttpRequestException Outage = new("Connection refused (workitem-server:8081)");

    private static ApiFactory ServerWith(Mock<IWorkItemServerClient> client)
        => new(configureServices: services => services.ReplaceSingleton(client.Object));

    [Fact]
    public async Task Listing_proposals_while_the_server_is_unreachable_is_reported_as_the_outage()
    {
        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.ListEditProposalsAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Outage);
        await using var factory = ServerWith(client);
        var human = await factory.CreateAuthenticatedClientAsync();

        var resp = await human.GetAsync($"/api/v1/workitems/{WorkItemId}/edit-proposals");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("WorkItemServer unreachable", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_applied_approve_is_reported_applied_even_when_the_chats_to_hint_cannot_be_read()
    {
        var proposal = new RemoteWorkItemEditProposal
        {
            Id = Guid.NewGuid(),
            WorkItemId = WorkItemId,
            Status = RemoteEditProposalStatus.Approved,
            CreatedByChatSessionId = Guid.NewGuid(),
        };
        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.ApproveEditProposalAsync(It.IsAny<WorkItemServerOptions>(), WorkItemId, proposal.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EditProposalDecisionResult(
                EditProposalDecisionOutcome.Applied, proposal, new RemoteWorkItem { Id = WorkItemId, Title = "Applied" }));
        client.Setup(c => c.GetAsync(It.IsAny<WorkItemServerOptions>(), WorkItemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteWorkItem { Id = WorkItemId, Title = "Applied" });
        client.Setup(c => c.ListEditProposalsAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Outage);
        await using var factory = ServerWith(client);
        var human = await factory.CreateAuthenticatedClientAsync();

        var resp = await human.PostAsync($"/api/v1/workitems/{WorkItemId}/edit-proposals/{proposal.Id}/approve", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Approved", body.GetProperty("proposal").GetProperty("status").GetString());
    }
}
