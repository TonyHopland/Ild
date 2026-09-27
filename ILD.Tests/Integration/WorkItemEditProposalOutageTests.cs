using System.Net;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests.Integration;

/// <summary>
/// The human edit-proposal routes when the WorkItem server cannot be reached:
/// an outage is a 503 naming it, and an approve the server already applied is
/// never reported as failed because a read that follows it could not be made.
/// A run whose pending proposals cannot be withdrawn is not deleted.
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

        var resp = await human.GetAsync($"/api/v1/workitems/{WorkItemId}/edit-proposals", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("WorkItemServer unreachable", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_applied_approve_is_reported_applied_with_its_item_even_when_no_read_after_it_can_be_made()
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
            .ThrowsAsync(Outage);
        client.Setup(c => c.ListEditProposalsAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Outage);
        await using var factory = ServerWith(client);
        var human = await factory.CreateAuthenticatedClientAsync();

        var resp = await human.PostAsync($"/api/v1/workitems/{WorkItemId}/edit-proposals/{proposal.Id}/approve", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("Approved", body.GetProperty("proposal").GetProperty("status").GetString());
        Assert.Equal("Applied", body.GetProperty("workItem").GetProperty("title").GetString());
    }

    public enum RunDeletion { AUserDeletesTheRun, ItsWorkItemIsDeleted }

    [Theory]
    [InlineData(RunDeletion.AUserDeletesTheRun)]
    [InlineData(RunDeletion.ItsWorkItemIsDeleted)]
    public async Task A_run_whose_pending_proposals_cannot_be_withdrawn_is_kept_for_a_later_retry(RunDeletion deletion)
    {
        var requester = new RemoteWorkItem { Id = WorkItemId, Title = "Requester", Status = RemoteWorkItemStatus.Done };
        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.GetAsync(It.IsAny<WorkItemServerOptions>(), WorkItemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(requester);
        client.Setup(c => c.ListAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<RemoteWorkItemStatus?>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { requester });
        client.Setup(c => c.QueryEditProposalsAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<RemoteEditProposalQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Outage);
        client.Setup(c => c.ListEditProposalsAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Outage);
        client.Setup(c => c.RejectEditProposalAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Outage);
        await using var factory = new ApiFactory(configureServices: services =>
        {
            services.ReplaceSingleton(client.Object);
            services.ReplaceSingleton(ReclaimerThatSucceeds());
        });
        var human = await factory.CreateAuthenticatedClientAsync();
        var runId = await SeedFinishedRunAsync(factory, WorkItemId);

        var resp = deletion == RunDeletion.AUserDeletesTheRun
            ? await human.DeleteAsync($"/api/v1/loopruns/{runId}", TestContext.Current.CancellationToken)
            : await human.DeleteAsync($"/api/v1/workitems/{WorkItemId}", TestContext.Current.CancellationToken);

        if (deletion == RunDeletion.AUserDeletesTheRun)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
        }
        Assert.True(await RunExistsAsync(factory, runId));
    }

    [Fact]
    public async Task With_no_WorkItem_server_configured_a_run_is_deleted_as_before()
    {
        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.QueryEditProposalsAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<RemoteEditProposalQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Outage);
        var unconfigured = new Mock<IWorkItemServerOptionsResolver>();
        unconfigured.Setup(r => r.ResolveForRepositoryAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No WorkItem server is configured."));
        unconfigured.Setup(r => r.ResolveForWorkItemAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No WorkItem server is configured."));
        await using var factory = new ApiFactory(configureServices: services =>
        {
            services.ReplaceSingleton(client.Object);
            services.ReplaceSingleton(unconfigured.Object);
            services.ReplaceSingleton(ReclaimerThatSucceeds());
        });
        var human = await factory.CreateAuthenticatedClientAsync();
        var runId = await SeedFinishedRunAsync(factory, WorkItemId);

        var resp = await human.DeleteAsync($"/api/v1/loopruns/{runId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.False(await RunExistsAsync(factory, runId));
    }

    private static IRunReclaimer ReclaimerThatSucceeds()
    {
        var reclaimer = new Mock<IRunReclaimer>();
        reclaimer.Setup(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>())).ReturnsAsync(true);
        return reclaimer.Object;
    }

    private static async Task<Guid> SeedFinishedRunAsync(ApiFactory factory, string workItemId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"withdrawal-{Guid.NewGuid():N}" };
        db.LoopTemplates.Add(template);
        var version = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = template.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.LoopTemplateVersions.Add(version);
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Completed,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            StartedAt = DateTime.UtcNow.AddHours(-1),
            CompletedAt = DateTime.UtcNow,
        };
        db.LoopRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private static async Task<bool> RunExistsAsync(ApiFactory factory, Guid runId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().LoopRuns.AnyAsync(r => r.Id == runId);
    }
}
