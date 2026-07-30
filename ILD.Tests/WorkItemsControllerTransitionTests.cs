using ILD.Api.Controllers;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;

namespace ILD.Tests;

/// <summary>
/// POST /api/v1/workitems/{id}/transition is the single endpoint behind every
/// board-driven status move — drag-and-drop, the keyboard move shortcut and the
/// work-item status dropdown all post to it. Backlog is a legal destination for
/// an item that has not started: CONTEXT.md describes moving back to "Backlog"
/// as the full reset for re-planning, and the work-item server accepts the
/// target without complaint.
///
/// These tests drive the endpoint through a real <see cref="WorkItemManager"/>
/// against the fake work-item server, so they pin the whole ILD-side path
/// rather than a mock's say-so.
///
/// Reproduction as the board sees it — an item in Ready with no run behind it:
/// <code>
///   POST /api/v1/workitems/{id}/transition
///   {"targetStatus":"Backlog"}
///
///   HTTP 400 Bad Request
///   {"error":"Transition not allowed"}
/// </code>
/// The item stays in Ready and the card snaps back to its column.
///
/// Out of scope here: what a Backlog move should do to an item whose run is
/// still going. That is <c>CleanupToBacklogAsync</c> / POST {id}/cleanup-to-backlog,
/// covered by <see cref="WorkItemManagerTests"/>; nothing below asserts against it.
/// </summary>
public class WorkItemsControllerTransitionTests
{
    private static (WorkItemsController controller, WorkItemManager mgr, TestDb db, Guid repoId) Setup()
    {
        var db = new TestDb();
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/repo.git" };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.SaveChanges();

        var eventLog = new Mock<IEventLogService>();
        eventLog.Setup(e => e.AppendAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>()))
            .ReturnsAsync(1L);

        var mgr = new WorkItemManager(
            new Mock<IRepositoryManager>().Object,
            db.Providers,
            eventLog.Object,
            db.LoopRuns,
            db.ServerClient,
            db.ServerOptions,
            engine: new Mock<ILoopEngine>().Object);

        var controller = new WorkItemsController(
            mgr,
            new Mock<ILoopEngine>().Object,
            new Mock<IWorktreePreviewService>().Object,
            new Mock<IRepositoryManager>().Object,
            db.LoopRuns,
            db.Providers,
            NullLogger<WorkItemsController>.Instance);

        return (controller, mgr, db, repo.Id);
    }

    /// <summary>
    /// Stand an item up in <paramref name="from"/> with no run behind it — the
    /// "has not started yet" case from the bug report.
    /// </summary>
    private static async Task<string> SeedIdleItemAsync(WorkItemManager mgr, Guid repoId, RemoteWorkItemStatus from)
    {
        var id = await mgr.CreateWorkItemAsync("move me back", "", repoId);
        await mgr.TransitionAsync(id, from);
        var seeded = await mgr.GetWorkItemAsync(id);
        Assert.Equal(from, seeded!.Status);
        Assert.Null(seeded.CurrentLoopRunId);
        return id;
    }

    private static async Task AssertMovesToBacklogAsync(RemoteWorkItemStatus from)
    {
        var (controller, mgr, db, repoId) = Setup();
        using var _ = db;

        var id = await SeedIdleItemAsync(mgr, repoId, from);

        var result = await controller.Transition(id, new WorkItemTransitionRequest { TargetStatus = "Backlog" });

        // Surface the rejection body verbatim — it is what the board shows the
        // user and what identifies the endpoint as the thing refusing.
        if (result is BadRequestObjectResult bad)
            Assert.Fail($"{from} -> Backlog was rejected: 400 {JsonSerializer.Serialize(bad.Value)}");
        Assert.IsType<OkResult>(result);
        var after = await mgr.GetWorkItemAsync(id);
        Assert.Equal(RemoteWorkItemStatus.Backlog, after!.Status);
    }

    // The reported bug: an item sitting in Ready that never started cannot be
    // dragged back to Backlog — the endpoint answers 400 "Transition not allowed".
    [Fact]
    public async Task Transition_from_Ready_to_Backlog_is_allowed_for_an_item_that_has_not_started()
        => await AssertMovesToBacklogAsync(RemoteWorkItemStatus.Ready);

    // Same missing mapping, same 400: nothing about the endpoint's rejection is
    // specific to Ready, so the other pre-run columns are broken too.
    [Fact]
    public async Task Transition_from_WorkQueue_to_Backlog_is_allowed()
        => await AssertMovesToBacklogAsync(RemoteWorkItemStatus.WorkQueue);

    [Fact]
    public async Task Transition_from_HumanFeedback_to_Backlog_is_allowed()
        => await AssertMovesToBacklogAsync(RemoteWorkItemStatus.HumanFeedback);

    // A second lap round the board: the item ran, its run ended, and it is
    // sitting in Ready again. Nothing is alive underneath it, so it moves back
    // like any unstarted item — the refusal below keys on a live run, not on
    // the item ever having had one.
    [Fact]
    public async Task Transition_to_Backlog_is_allowed_when_the_items_earlier_run_has_finished()
    {
        var (controller, mgr, db, repoId) = Setup();
        using var _ = db;

        var id = await SeedIdleItemAsync(mgr, repoId, RemoteWorkItemStatus.Ready);
        SeedRun(db, id, LoopRunStatus.Completed, completed: true);

        var result = await controller.Transition(id, new WorkItemTransitionRequest { TargetStatus = "Backlog" });

        if (result is BadRequestObjectResult bad)
            Assert.Fail($"Ready -> Backlog with a finished run was rejected: 400 {JsonSerializer.Serialize(bad.Value)}");
        Assert.Equal(RemoteWorkItemStatus.Backlog, (await mgr.GetWorkItemAsync(id))!.Status);
    }

    /// <summary>
    /// Give the item a run the engine still considers alive — the case the
    /// board move deliberately does not cover.
    /// </summary>
    private static void SeedLiveRun(TestDb db, string workItemId)
        => SeedRun(db, workItemId, LoopRunStatus.WaitingHuman, completed: false);

    private static void SeedRun(TestDb db, string workItemId, LoopRunStatus status, bool completed)
    {
        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "test" };
        var ltv = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = lt.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplates.Add(lt);
        db.Context.LoopTemplateVersions.Add(ltv);
        db.Context.LoopRuns.Add(new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            LoopTemplateVersionId = ltv.Id,
            Status = status,
            StartedAt = DateTime.UtcNow,
            CompletedAt = completed ? DateTime.UtcNow : null,
        });
        db.Context.SaveChanges();
    }

    // The board move is for items that have not started. One parked at a human
    // gate with its run still alive keeps the 400 it has always answered:
    // relabelling the card would leave the run heartbeating underneath it,
    // holding a concurrency slot. Stopping the run and resetting the item is
    // CleanupToBacklogAsync / POST {id}/cleanup-to-backlog, from the modal.
    [Fact]
    public async Task Transition_to_Backlog_is_refused_while_the_items_run_is_still_alive()
    {
        var (controller, mgr, db, repoId) = Setup();
        using var _ = db;

        var id = await SeedIdleItemAsync(mgr, repoId, RemoteWorkItemStatus.HumanFeedback);
        SeedLiveRun(db, id);

        var result = await controller.Transition(id, new WorkItemTransitionRequest { TargetStatus = "Backlog" });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(RemoteWorkItemStatus.HumanFeedback, (await mgr.GetWorkItemAsync(id))!.Status);
    }

    // The other half of the refusal, and the one the live-run check cannot
    // stand in for: a finished item has no run left alive, so only the source
    // whitelist keeps it out. Re-opening something already declared Done is a
    // decision the board does not get to make silently — it goes back through
    // WorkQueue like anything else entering the queue.
    [Fact]
    public async Task Transition_to_Backlog_is_refused_from_Done_even_with_no_run_alive()
    {
        var (controller, mgr, db, repoId) = Setup();
        using var _ = db;

        var id = await SeedIdleItemAsync(mgr, repoId, RemoteWorkItemStatus.Done);
        Assert.Null(await db.LoopRuns.GetActiveByWorkItemAsync(id));

        var result = await controller.Transition(id, new WorkItemTransitionRequest { TargetStatus = "Backlog" });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(RemoteWorkItemStatus.Done, (await mgr.GetWorkItemAsync(id))!.Status);
    }

    // Guard on the fix: widening the endpoint must not turn a typo into a
    // silent no-op transition.
    [Fact]
    public async Task Transition_to_an_unparseable_status_still_returns_400()
    {
        var (controller, mgr, db, repoId) = Setup();
        using var _ = db;

        var id = await SeedIdleItemAsync(mgr, repoId, RemoteWorkItemStatus.Ready);

        var result = await controller.Transition(id, new WorkItemTransitionRequest { TargetStatus = "NotAStatus" });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(RemoteWorkItemStatus.Ready, (await mgr.GetWorkItemAsync(id))!.Status);
    }
}
