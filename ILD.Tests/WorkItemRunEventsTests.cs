using System.Text.Json;
using ILD.Api.Controllers;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The events written around a run from outside the engine — a human answering
/// or moving the item, recovery, the stuck-run watchdog, the PR webhook, the
/// poll cycle — each on the run it is about, and nothing written onto a run
/// that is not the one in hand.
/// </summary>
public class WorkItemRunEventsTests
{
    private sealed class Rig : IDisposable
    {
        public TestDb Db { get; }
        public IEventLogService Events { get; }
        public Mock<ILoopEngine> Engine { get; } = new();
        public WorkItemManager Manager { get; }
        public Guid RepoId { get; }
        public Guid VersionId { get; }

        public Rig(TestDb? db = null, IEventLogService? events = null, ILoopEngine? engine = null, bool noEngine = false)
        {
            Db = db ?? new TestDb();
            Events = events ?? RunTimeline.EventLog(Db);
            var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example.test" };
            var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example.test/repo.git" };
            Db.Context.RemoteProviders.Add(remote);
            Db.Context.Repositories.Add(repo);
            Db.Context.SaveChanges();
            RepoId = repo.Id;
            VersionId = RunTimeline.SeedVersion(Db);
            Manager = RunTimeline.Manager(Db, noEngine ? null : engine ?? Engine.Object, Events);
        }

        public Task<string> WorkItemAsync() => Manager.CreateWorkItemAsync("item", "body", RepoId);

        public void Dispose() => Db.Dispose();
    }

    /// <summary>A run parked at a Human node, with the execution that is waiting.</summary>
    private static (LoopRun Run, LoopRunNode Waiting) ParkedAtHuman(Rig rig, string workItemId)
    {
        var human = RunTimeline.SeedNode(rig.Db, rig.VersionId, NodeType.Human, "Plan check");
        var run = RunTimeline.SeedRun(rig.Db, workItemId, rig.VersionId, LoopRunStatus.WaitingHuman,
            currentNodeId: human.Id, humanFeedbackReason: HumanFeedbackReasons.HumanInputNeeded);
        var waiting = RunTimeline.SeedRunNode(rig.Db, run.Id, human, LoopRunNodeStatus.WaitingHuman);
        return (run, waiting);
    }

    public enum Answer { Input, Respond, Edge, Reject }

    [Theory]
    [InlineData(Answer.Input, "Ship it as planned", null)]
    [InlineData(Answer.Respond, "Which of the two caches do you mean?", null)]
    [InlineData(Answer.Edge, "Split the PR into the schema change and the rest", "Rework")]
    [InlineData(Answer.Reject, "rejected by user: the approach ignores the retention rules", null)]
    public async Task A_human_answer_is_recorded_on_the_run_and_the_execution_it_answers(Answer answer, string expected, string? edge)
    {
        using var rig = new Rig();
        var id = await rig.WorkItemAsync();
        var (run, waiting) = ParkedAtHuman(rig, id);

        switch (answer)
        {
            case Answer.Input: await rig.Manager.SubmitHumanFeedbackInputAsync(id, "Ship it as planned"); break;
            case Answer.Respond: await rig.Manager.SubmitHumanFeedbackRespondAsync(id, "Which of the two caches do you mean?"); break;
            case Answer.Edge: await rig.Manager.SubmitHumanFeedbackEdgeAsync(id, "Rework", "Split the PR into the schema change and the rest"); break;
            case Answer.Reject: await rig.Manager.RejectHumanFeedbackAsync(id, "the approach ignores the retention rules"); break;
        }

        var received = Assert.Single(RunTimeline.Events(rig.Db, run.Id, EventType.HumanFeedbackReceived));
        Assert.Equal(expected, received.Data);
        Assert.Equal(waiting.Id, received.RunNodeId);
        if (edge is not null)
            Assert.Equal(edge, received.EdgeName);
    }

    private static WorkItemsController Controller(Rig rig) => new(
        rig.Manager,
        rig.Engine.Object,
        new Mock<IWorktreePreviewService>().Object,
        new Mock<IRepositoryManager>().Object,
        rig.Db.LoopRuns,
        rig.Db.Providers,
        NoPackageFeeds.Resolver,
        new Mock<IBranchNameOverrideService>().Object,
        ILD.Core.Services.Attachments.AttachmentLimits.FromEnvironment(),
        NullLogger<WorkItemsController>.Instance);

    [Theory]
    [InlineData(Answer.Input)]
    [InlineData(Answer.Respond)]
    [InlineData(Answer.Edge)]
    [InlineData(Answer.Reject)]
    public async Task Answering_a_run_that_has_ended_is_a_conflict_and_records_nothing(Answer answer)
    {
        using var rig = new Rig();
        var id = await rig.WorkItemAsync();
        var (run, _) = ParkedAtHuman(rig, id);
        await rig.Events.AppendAsync(run.Id, EventType.LoopRunFailed, "The run failed while the question was open");
        var controller = Controller(rig);

        IActionResult result = answer switch
        {
            Answer.Input => await controller.HumanFeedbackInput(id, new HumanFeedbackInputRequest { Input = "late" }),
            Answer.Respond => await controller.HumanFeedbackRespond(id, new HumanFeedbackInputRequest { Input = "late" }),
            Answer.Edge => await controller.HumanFeedbackEdge(id, new HumanFeedbackEdgeRequest { Name = "Rework", Input = "late" }),
            _ => await controller.HumanFeedbackReject(id, new HumanFeedbackRejectRequest { Input = "late" }),
        };

        var conflict = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(conflict.Value));
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("error").GetString()));
        Assert.Empty(RunTimeline.Events(rig.Db, run.Id, EventType.HumanFeedbackReceived));
        rig.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }

    [Fact]
    public async Task Marking_an_item_Done_ends_its_live_run_with_one_cancellation_on_that_run()
    {
        using var h = new LoopEngineHarness();
        using var rig = new Rig(h.Db, h.Services.GetRequiredService<IEventLogService>(), h.Engine);
        var id = await rig.WorkItemAsync();
        var (run, _) = ParkedAtHuman(rig, id);

        await rig.Manager.TransitionToDoneAsync(id);

        var ended = Assert.Single(RunTimeline.EndingEvents(rig.Db, run.Id));
        Assert.Equal(EventType.LoopRunCancelled, ended.EventType);
        Assert.False(string.IsNullOrWhiteSpace(ended.Data));
    }

    [Fact]
    public async Task Without_an_engine_marking_an_item_Done_still_records_the_run_it_ends_as_cancelled()
    {
        using var rig = new Rig(noEngine: true);
        var id = await rig.WorkItemAsync();
        var (run, _) = ParkedAtHuman(rig, id);

        await rig.Manager.TransitionToDoneAsync(id);

        Assert.Equal(LoopRunStatus.Cancelled, rig.Db.Fresh().LoopRuns.Single(r => r.Id == run.Id).Status);
        Assert.Equal(EventType.LoopRunCancelled, Assert.Single(RunTimeline.EndingEvents(rig.Db, run.Id)).EventType);
    }

    [Fact]
    public async Task Moving_an_item_with_a_live_run_to_HumanFeedback_by_hand_parks_that_run_with_the_reason()
    {
        using var rig = new Rig();
        var id = await rig.WorkItemAsync();
        var older = RunTimeline.SeedRun(rig.Db, id, rig.VersionId, LoopRunStatus.Failed,
            humanFeedbackReason: "earlier", startedAt: DateTime.UtcNow.AddHours(-2));
        var live = RunTimeline.SeedRun(rig.Db, id, rig.VersionId, LoopRunStatus.Running);

        await rig.Manager.TransitionToHumanFeedbackAsync(id, "Needs a product decision before going further");

        Assert.Contains("Needs a product decision before going further",
            Assert.Single(RunTimeline.Events(rig.Db, live.Id, EventType.RunParked)).Data);
        Assert.Empty(RunTimeline.Events(rig.Db, older.Id));
    }

    [Fact]
    public async Task Moving_an_item_with_no_live_run_to_HumanFeedback_by_hand_gives_the_item_the_reason_and_leaves_its_runs_alone()
    {
        using var rig = new Rig();
        var id = await rig.WorkItemAsync();
        var previous = RunTimeline.SeedRun(rig.Db, id, rig.VersionId, LoopRunStatus.Cancelled, humanFeedbackReason: "Run Cancelled");

        await rig.Manager.TransitionToHumanFeedbackAsync(id, "Needs a product decision before going further");

        var view = await rig.Manager.GetWorkItemAsync(id);
        Assert.Equal(RemoteWorkItemStatus.HumanFeedback, view!.Status);
        Assert.Contains("Needs a product decision before going further", view.StatusReason);
        Assert.NotNull(view.StatusReasonAt);
        Assert.Equal("Run Cancelled", rig.Db.Fresh().LoopRuns.Single(r => r.Id == previous.Id).HumanFeedbackReason);
        Assert.Empty(RunTimeline.Events(rig.Db, previous.Id));
    }

    [Fact]
    public async Task A_transition_puts_a_reason_on_a_run_only_when_told_which_run()
    {
        using var rig = new Rig();
        var id = await rig.WorkItemAsync();
        var previous = RunTimeline.SeedRun(rig.Db, id, rig.VersionId, LoopRunStatus.Failed, humanFeedbackReason: "Node Failed");

        await rig.Manager.TransitionAsync(id, RemoteWorkItemStatus.HumanFeedback, "Not about this run",
            humanFeedbackReason: "Not about this run");

        Assert.Contains(rig.Db.Fresh().LoopRuns.Single(r => r.Id == previous.Id).HumanFeedbackReason,
            new[] { "Node Failed", null });

        await rig.Manager.TransitionAsync(id, RemoteWorkItemStatus.HumanFeedback, "About this run",
            humanFeedbackReason: "About this run", currentLoopRunId: previous.Id);

        Assert.Equal("About this run", rig.Db.Fresh().LoopRuns.Single(r => r.Id == previous.Id).HumanFeedbackReason);
    }

    private static readonly WorkItemServerOptions Opts = new() { BaseUrl = "http://workitems.example.test", ApiKey = "k" };

    [Theory]
    [InlineData(false, "Failed to start run: Matched template t has a broken graph")]
    [InlineData(true, "No loop found for existing tags")]
    public async Task The_poll_cycle_gives_an_item_it_could_not_start_a_reason_of_its_own(bool noTemplate, string reason)
    {
        using var rig = new Rig();
        var id = await rig.WorkItemAsync();
        var previous = RunTimeline.SeedRun(rig.Db, id, rig.VersionId, LoopRunStatus.Failed, humanFeedbackReason: "Node Failed");

        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.PollAsync(Opts, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemotePollResponse
            {
                ReadyItems = new[] { new RemoteWorkItem { Id = id, Title = "item", Status = RemoteWorkItemStatus.Ready, Tags = new[] { "build" } } },
            });
        client.Setup(c => c.TransitionAsync(Opts, id, It.IsAny<RemoteTransitionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkItemServerOptions _, string _, RemoteTransitionRequest r, CancellationToken _) =>
                new RemoteTransitionResponse { Success = true, ActualStatus = r.TargetStatus });
        var resolver = new Mock<ILoopTemplateResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
            .Returns(noTemplate
                ? new LoopTemplateResolution(LoopTemplateResolutionKind.None, null, Array.Empty<string>())
                : new LoopTemplateResolution(LoopTemplateResolutionKind.Single, Guid.NewGuid(), new[] { "t" }));
        rig.Engine.Setup(e => e.StartRunAsync(id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Matched template t has a broken graph"));

        await new RemoteWorkItemCoordinator(client.Object, resolver.Object, rig.Engine.Object, rig.Db.LoopRuns)
            .RunPollCycleAsync(Opts, maxConcurrent: 5, ct: TestContext.Current.CancellationToken);

        var view = await rig.Manager.GetWorkItemAsync(id);
        Assert.Contains(reason, view!.StatusReason);
        Assert.NotNull(view.StatusReasonAt);
        Assert.Equal("Node Failed", rig.Db.Fresh().LoopRuns.Single(r => r.Id == previous.Id).HumanFeedbackReason);
    }

    [Theory]
    [InlineData(RecoveryPolicy.NeedsReview, null, "Recovery requires review")]
    [InlineData(RecoveryPolicy.AutoResume, "/srv/ild/worktrees/wi-9-run-2", "worktree is missing or unhealthy at '/srv/ild/worktrees/wi-9-run-2'")]
    public async Task Recovery_that_needs_a_person_records_why_on_the_run(RecoveryPolicy policy, string? worktree, string expected)
    {
        using var rig = new Rig();
        var id = await rig.WorkItemAsync();
        var run = RunTimeline.SeedRun(rig.Db, id, rig.VersionId, LoopRunStatus.Running,
            worktreePath: worktree, recoveryPolicy: policy);
        var repo = new Mock<IRepositoryManager>();
        repo.Setup(r => r.ValidateWorktreeHealthAsync(It.IsAny<string>())).ReturnsAsync(false);
        using var services = RunTimeline.Services(rig.Db, s =>
        {
            s.AddSingleton<IWorkItemManager>(rig.Manager);
            s.AddSingleton(repo.Object);
            s.AddSingleton(rig.Engine.Object);
            s.AddSingleton(rig.Events);
        });
        var recovery = ActivatorUtilities.CreateInstance<RecoveryManager>(services);

        await recovery.RecoverRunAsync(run.Id);

        Assert.Contains(expected, Assert.Single(RunTimeline.Events(rig.Db, run.Id, EventType.RecoveryTriggered)).Data);
    }

    [Fact]
    public async Task The_watchdog_ending_a_run_left_running_after_it_finished_records_the_failure()
    {
        using var rig = new Rig();
        var run = RunTimeline.SeedRun(rig.Db, "WI-" + Guid.NewGuid().ToString("N"), rig.VersionId, LoopRunStatus.Running,
            startedAt: DateTime.UtcNow.AddMinutes(-10));
        var stale = DateTime.UtcNow.AddMinutes(-10);
        await rig.Db.Fresh().LoopRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.UpdatedAt, stale)
            .SetProperty(r => r.CompletedAt, stale.AddMinutes(1)), TestContext.Current.CancellationToken);

        var engine = new Mock<ILoopEngine>();
        engine.Setup(e => e.GetActiveRunIdsAsync()).ReturnsAsync(Array.Empty<Guid>());
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(w => w.GetWorkItemAsync(It.IsAny<string>()))
            .ReturnsAsync((string wid) => new WorkItemView { Id = wid, Status = RemoteWorkItemStatus.Running });
        var services = new ServiceCollection();
        services.AddSingleton<ILoopRunStore>(new ILD.Data.Stores.LoopRunStore(rig.Db.Fresh()));
        services.AddSingleton(new Mock<IRecoveryManager>().Object);
        services.AddSingleton(workItems.Object);
        services.AddSingleton(rig.Events);
        using var provider = services.BuildServiceProvider();
        var watchdog = new StuckRunWatchdog(provider.GetRequiredService<IServiceScopeFactory>(), engine.Object,
            NullLogger<StuckRunWatchdog>.Instance);

        await (Task)typeof(StuckRunWatchdog)
            .GetMethod("SweepOnceAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(watchdog, new object?[] { CancellationToken.None })!;

        Assert.Equal(LoopRunStatus.Failed, rig.Db.Fresh().LoopRuns.Single(r => r.Id == run.Id).Status);
        var failed = Assert.Single(RunTimeline.EndingEvents(rig.Db, run.Id));
        Assert.Equal(EventType.LoopRunFailed, failed.EventType);
        Assert.False(string.IsNullOrWhiteSpace(failed.Data));
    }

    private const string PrUrl = "https://forge.example.test/team/repo/pulls/7";

    private static PrSyncService PrSync(Rig rig)
    {
        var services = RunTimeline.Services(rig.Db, s =>
        {
            s.AddSingleton<IWorkItemManager>(rig.Manager);
            s.AddSingleton(rig.Engine.Object);
            s.AddSingleton(new Mock<IPrStatusPoller>().Object);
            s.AddSingleton(rig.Events);
        });
        return ActivatorUtilities.CreateInstance<PrSyncService>(services);
    }

    [Fact]
    public async Task A_PR_comment_from_the_webhook_shows_in_the_runs_events_page()
    {
        using var rig = new Rig();
        var run = RunTimeline.SeedRun(rig.Db, await rig.WorkItemAsync(), rig.VersionId, LoopRunStatus.WaitingHuman);
        var tracked = rig.Db.Context.LoopRuns.Single(r => r.Id == run.Id);
        tracked.PrUrl = PrUrl;
        rig.Db.Context.SaveChanges();
        await rig.Events.AppendAsync(run.Id, EventType.LoopRunStarted, "started");

        await PrSync(rig).HandleWebhookAsync(new WebhookPayload("issue_comment.created", "repo-1", "7", PrUrl, "Please also update the docs", null));

        var page = await rig.Events.GetByRunIdAfterCursorAsync(run.Id, 0, 100);
        Assert.Equal(new[] { "started", "Please also update the docs" }, page.Entries.Select(e => e.Data));
    }

    [Fact]
    public async Task A_merge_webhook_with_a_comment_for_an_ended_run_still_records_the_merge_and_not_the_comment()
    {
        using var rig = new Rig();
        var run = RunTimeline.SeedRun(rig.Db, await rig.WorkItemAsync(), rig.VersionId, LoopRunStatus.Completed);
        var tracked = rig.Db.Context.LoopRuns.Single(r => r.Id == run.Id);
        tracked.PrUrl = PrUrl;
        rig.Db.Context.SaveChanges();
        await rig.Events.AppendAsync(run.Id, EventType.LoopRunCompleted, "done");

        var thrown = await Record.ExceptionAsync(() => PrSync(rig).HandleWebhookAsync(
            new WebhookPayload("pull_request.closed", "repo-1", "7", PrUrl, "Merged, thanks!", "merged")));

        Assert.Null(thrown);
        Assert.True(rig.Db.Fresh().LoopRuns.Single(r => r.Id == run.Id).IsPrMerged);
        Assert.DoesNotContain(RunTimeline.Events(rig.Db, run.Id), e => e.Data == "Merged, thanks!");
    }
}
