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

    private static Task<IActionResult> AnswerAsync(WorkItemsController controller, string id, Answer answer, Guid? runId, string? text)
        => answer switch
        {
            Answer.Input => controller.HumanFeedbackInput(id, new HumanFeedbackInputRequest { RunId = runId, Input = text }),
            Answer.Respond => controller.HumanFeedbackRespond(id, new HumanFeedbackInputRequest { RunId = runId, Input = text }),
            Answer.Edge => controller.HumanFeedbackEdge(id, new HumanFeedbackEdgeRequest { RunId = runId, Name = "Rework", Input = text }),
            _ => controller.HumanFeedbackReject(id, new HumanFeedbackRejectRequest { RunId = runId, Input = text }),
        };

    /// <summary>
    /// A Human node driven by the real executor, with every way of answering it
    /// wired to a node of its own, so where the run goes next shows how it was
    /// answered.
    /// </summary>
    private static LoopNode RoutedHumanNode(LoopEngineHarness h)
    {
        var human = h.AddNode("h", NodeType.Human, "Plan check");
        h.AddNode("approved", NodeType.Cmd);
        h.AddNode("rejected", NodeType.Cmd);
        h.AddNode("reworked", NodeType.Cmd);
        h.AddNode("responded", NodeType.Cmd);
        h.AddEdge("h", "approved", EdgeType.OnSuccess);
        h.AddEdge("h", "rejected", EdgeType.OnFailure);
        h.AddEdge("h", "reworked", EdgeType.Custom, "Rework");
        h.AddEdge("h", "responded", EdgeType.Custom, "Respond");
        h.Registry.Register(new ILD.Core.Services.Implementations.Executors.HumanNodeExecutor());
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting("next"),
            new NodeOutcome.Terminal("done")));
        return human;
    }

    private static (LoopRun Run, LoopRunNode Waiting) Parked(
        TestDb db, string workItemId, Guid versionId, LoopNode human,
        LoopRunStatus status = LoopRunStatus.WaitingHuman, DateTime? startedAt = null)
    {
        var run = RunTimeline.SeedRun(db, workItemId, versionId, status, currentNodeId: human.Id,
            humanFeedbackReason: HumanFeedbackReasons.HumanInputNeeded, startedAt: startedAt);
        return (run, RunTimeline.SeedRunNode(db, run.Id, human, LoopRunNodeStatus.WaitingHuman));
    }

    /// <summary>A work item the board shows waiting on a person.</summary>
    private static async Task<string> WaitingItemAsync(Rig rig)
    {
        var id = await rig.WorkItemAsync();
        Assert.True(await rig.Manager.TransitionAsync(id, RemoteWorkItemStatus.HumanFeedback));
        return id;
    }

    /// <summary>
    /// Wait out every drive an answer launched. The runs come from the engine, not
    /// the database: the harness shares one SQLite connection, and opening a
    /// context on it while a drive is running a statement fails.
    /// </summary>
    private static async Task DrainAsync(LoopEngineHarness h)
    {
        foreach (var runId in await h.Engine.GetActiveRunIdsAsync())
            await LoopEngineHarness.WaitUntilIdleAsync((LoopEngine)h.Engine, runId);
    }

    /// <summary>Everything an answer could change on the item's runs and their executions.</summary>
    private static string RunState(TestDb db, string workItemId)
    {
        var ctx = db.Fresh();
        var runs = ctx.LoopRuns.AsNoTracking().Where(r => r.WorkItemId == workItemId).AsEnumerable()
            .OrderBy(r => r.Id)
            .Select(r => $"{r.Id} {r.Status} {r.HumanFeedbackReason} {r.ExternalActionResult} {r.ExternalActionResultType} "
                + $"{r.ExternalActionEdgeName} {r.CurrentNodeId} {r.CompletedAt:O} {r.UpdatedAt:O}");
        var ids = ctx.LoopRuns.AsNoTracking().Where(r => r.WorkItemId == workItemId).Select(r => r.Id).ToList();
        var nodes = ctx.LoopRunNodes.AsNoTracking().Where(n => ids.Contains(n.LoopRunId)).AsEnumerable()
            .OrderBy(n => n.Id)
            .Select(n => $"{n.Id} {n.Status} {n.Output}");
        return string.Join("\n", runs.Concat(nodes));
    }

    private static void AssertNoAnswerRecorded(TestDb db)
        => Assert.Empty(db.Fresh().EventLogs.AsNoTracking().Where(e => e.EventType == EventType.HumanFeedbackReceived).ToList());

    private static void AssertErrorFor(IActionResult result, int status)
    {
        var refused = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, refused.StatusCode);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(refused.Value));
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("error").GetString()));
    }

    [Theory]
    [InlineData(Answer.Input, "Ship it as planned", "Ship it as planned", null, "approved")]
    [InlineData(Answer.Respond, "Which of the two caches do you mean?", "Which of the two caches do you mean?", "Respond", "responded")]
    [InlineData(Answer.Edge, "Split the PR into the schema change and the rest", "Split the PR into the schema change and the rest", "Rework", "reworked")]
    [InlineData(Answer.Reject, "the approach ignores the retention rules", "rejected by user: the approach ignores the retention rules", null, "rejected")]
    [InlineData(Answer.Reject, null, "rejected by user", null, "rejected")]
    public async Task An_answer_to_the_active_waiting_run_is_recorded_on_it_and_resumes_it_along_that_edge(
        Answer answer, string? text, string expected, string? edge, string next)
    {
        using var h = new LoopEngineHarness();
        using var rig = new Rig(h.Db, h.Services.GetRequiredService<IEventLogService>(), h.Engine);
        var human = RoutedHumanNode(h);
        var id = await WaitingItemAsync(rig);
        var (run, waiting) = Parked(rig.Db, id, h.TemplateVersionId, human);

        var result = await AnswerAsync(Controller(rig), id, answer, run.Id, text);
        await DrainAsync(h);

        Assert.IsType<OkResult>(result);
        var received = Assert.Single(RunTimeline.Events(rig.Db, run.Id, EventType.HumanFeedbackReceived));
        Assert.Equal(expected, received.Data);
        Assert.Equal(waiting.Id, received.RunNodeId);
        Assert.Equal(human.Id, received.NodeId);
        Assert.Equal(edge, received.EdgeName);
        Assert.NotEqual(LoopRunStatus.WaitingHuman, rig.Db.Fresh().LoopRuns.Single(r => r.Id == run.Id).Status);
        var executions = rig.Db.Fresh().LoopRunNodes.AsNoTracking().Where(n => n.LoopRunId == run.Id).ToList();
        Assert.DoesNotContain(executions, n => n.Status == LoopRunNodeStatus.WaitingHuman);
        Assert.Contains(executions, n => n.LoopNodeId == h.NodesById[next].Id);
        // The answer moves nothing on the work item server itself: the one move
        // to Running is the engine's, for the run it resumed.
        Assert.Equal(RemoteWorkItemStatus.HumanFeedback, (await rig.Manager.GetWorkItemAsync(id))!.Status);
        h.WorkItemsMock.Verify(m => m.TransitionAsync(id, RemoteWorkItemStatus.Running,
            It.IsAny<string?>(), It.IsAny<string?>(), run.Id, It.IsAny<string?>()), Times.Once);
        h.WorkItemsMock.Verify(m => m.TransitionAsync(It.IsAny<string>(), RemoteWorkItemStatus.Running,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Once);
    }

    public enum Refusal
    {
        NoRun,
        LatestCompleted,
        LatestFailed,
        LatestCancelled,
        RunningNotWaiting,
        WaitingWithNoWaitingNode,
        StaleRunId,
        ConversationEnded,
        NoEngine,
        EarlierRunWhileTheNextIsRunning,
        EarlierRunWhileTheNextIsWaiting,
    }

    public static TheoryData<Refusal, Answer> Refusals()
    {
        var data = new TheoryData<Refusal, Answer>();
        foreach (var refusal in Enum.GetValues<Refusal>())
            foreach (var answer in Enum.GetValues<Answer>())
                data.Add(refusal, answer);
        return data;
    }

    /// <summary>Stands the item up as <paramref name="refusal"/> describes and returns the run id the UI would send.</summary>
    private static async Task<Guid> SeedRefusalAsync(Rig rig, string id, Guid versionId, LoopNode human, Refusal refusal)
    {
        switch (refusal)
        {
            case Refusal.NoRun:
                return Guid.NewGuid();
            case Refusal.LatestCompleted:
                return Parked(rig.Db, id, versionId, human, LoopRunStatus.Completed).Run.Id;
            case Refusal.LatestFailed:
                return Parked(rig.Db, id, versionId, human, LoopRunStatus.Failed).Run.Id;
            case Refusal.LatestCancelled:
                return Parked(rig.Db, id, versionId, human, LoopRunStatus.Cancelled).Run.Id;
            case Refusal.RunningNotWaiting:
                return Parked(rig.Db, id, versionId, human, LoopRunStatus.Running).Run.Id;
            case Refusal.WaitingWithNoWaitingNode:
            {
                var run = RunTimeline.SeedRun(rig.Db, id, versionId, LoopRunStatus.WaitingHuman, currentNodeId: human.Id,
                    humanFeedbackReason: HumanFeedbackReasons.HumanInputNeeded);
                RunTimeline.SeedRunNode(rig.Db, run.Id, human, LoopRunNodeStatus.Succeeded);
                return run.Id;
            }
            case Refusal.StaleRunId:
                Parked(rig.Db, id, versionId, human);
                return Guid.NewGuid();
            case Refusal.ConversationEnded:
            {
                var (run, _) = Parked(rig.Db, id, versionId, human);
                await rig.Events.AppendAsync(run.Id, EventType.LoopRunFailed, "The run failed while the question was open");
                return run.Id;
            }
            case Refusal.NoEngine:
                return Parked(rig.Db, id, versionId, human).Run.Id;
            case Refusal.EarlierRunWhileTheNextIsRunning:
            {
                var earlier = Parked(rig.Db, id, versionId, human, LoopRunStatus.Cancelled, DateTime.UtcNow.AddHours(-1)).Run;
                Parked(rig.Db, id, versionId, human, LoopRunStatus.Running);
                return earlier.Id;
            }
            case Refusal.EarlierRunWhileTheNextIsWaiting:
            {
                var earlier = Parked(rig.Db, id, versionId, human, LoopRunStatus.Cancelled, DateTime.UtcNow.AddHours(-1)).Run;
                Parked(rig.Db, id, versionId, human);
                return earlier.Id;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(refusal));
        }
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task An_answer_for_anything_but_the_active_waiting_run_is_refused_and_changes_nothing(Refusal refusal, Answer answer)
    {
        using var h = refusal == Refusal.NoEngine ? null : new LoopEngineHarness();
        using var rig = h is null
            ? new Rig(noEngine: true)
            : new Rig(h.Db, h.Services.GetRequiredService<IEventLogService>(), h.Engine);
        var versionId = h?.TemplateVersionId ?? rig.VersionId;
        var human = h is null ? RunTimeline.SeedNode(rig.Db, versionId, NodeType.Human, "Plan check") : RoutedHumanNode(h);
        var id = await WaitingItemAsync(rig);
        var runId = await SeedRefusalAsync(rig, id, versionId, human, refusal);
        var before = RunState(rig.Db, id);

        var result = await AnswerAsync(Controller(rig), id, answer, runId, "late");
        if (h is not null) await DrainAsync(h);

        AssertErrorFor(result, 409);
        AssertNoAnswerRecorded(rig.Db);
        Assert.Equal(before, RunState(rig.Db, id));
        Assert.Equal(RemoteWorkItemStatus.HumanFeedback, (await rig.Manager.GetWorkItemAsync(id))!.Status);
        h?.WorkItemsMock.Verify(m => m.TransitionAsync(It.IsAny<string>(), It.IsAny<RemoteWorkItemStatus>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
    }

    [Theory]
    [InlineData(Answer.Input, false)]
    [InlineData(Answer.Input, true)]
    [InlineData(Answer.Respond, false)]
    [InlineData(Answer.Respond, true)]
    [InlineData(Answer.Edge, false)]
    [InlineData(Answer.Edge, true)]
    [InlineData(Answer.Reject, false)]
    [InlineData(Answer.Reject, true)]
    public async Task An_answer_that_does_not_name_its_run_is_a_bad_request(Answer answer, bool emptyId)
    {
        using var h = new LoopEngineHarness();
        using var rig = new Rig(h.Db, h.Services.GetRequiredService<IEventLogService>(), h.Engine);
        var human = RoutedHumanNode(h);
        var id = await WaitingItemAsync(rig);
        Parked(rig.Db, id, h.TemplateVersionId, human);
        var before = RunState(rig.Db, id);

        var result = await AnswerAsync(Controller(rig), id, answer, emptyId ? Guid.Empty : null, "Ship it");
        await DrainAsync(h);

        AssertErrorFor(result, 400);
        AssertNoAnswerRecorded(rig.Db);
        Assert.Equal(before, RunState(rig.Db, id));
        Assert.Equal(RemoteWorkItemStatus.HumanFeedback, (await rig.Manager.GetWorkItemAsync(id))!.Status);
    }

    [Fact]
    public async Task A_delivered_answer_stays_delivered_and_drives_the_run_when_the_item_cannot_be_moved_to_Running()
    {
        using var h = new LoopEngineHarness();
        using var rig = new Rig(h.Db, h.Services.GetRequiredService<IEventLogService>(), h.Engine);
        var human = RoutedHumanNode(h);
        h.WorkItemsMock.Setup(m => m.TransitionAsync(It.IsAny<string>(), RemoteWorkItemStatus.Running,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()))
            .ThrowsAsync(new HttpRequestException("The work item server is unreachable"));
        var id = await WaitingItemAsync(rig);
        var (run, waiting) = Parked(rig.Db, id, h.TemplateVersionId, human);

        var result = await AnswerAsync(Controller(rig), id, Answer.Input, run.Id, "Ship it as planned");
        await DrainAsync(h);

        Assert.IsType<OkResult>(result);
        Assert.Equal(waiting.Id, Assert.Single(RunTimeline.Events(rig.Db, run.Id, EventType.HumanFeedbackReceived)).RunNodeId);
        Assert.Contains(rig.Db.Fresh().LoopRunNodes.AsNoTracking().Where(n => n.LoopRunId == run.Id).ToList(),
            n => n.LoopNodeId == h.NodesById["approved"].Id);
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

    [Theory]
    [InlineData(LoopRunStatus.Running, null, false, true)]
    [InlineData(LoopRunStatus.Failed, LoopRunStatus.Running, true, false)]
    [InlineData(LoopRunStatus.Cancelled, LoopRunStatus.WaitingHuman, false, false)]
    [InlineData(LoopRunStatus.Failed, null, true, false)]
    public async Task A_PR_comment_is_recorded_only_while_the_PRs_run_is_the_items_active_run(
        LoopRunStatus prRun, LoopRunStatus? next, bool merged, bool recorded)
    {
        using var rig = new Rig();
        var id = await rig.WorkItemAsync();
        var run = RunTimeline.SeedRun(rig.Db, id, rig.VersionId, prRun, startedAt: DateTime.UtcNow.AddHours(-1));
        var tracked = rig.Db.Context.LoopRuns.Single(r => r.Id == run.Id);
        tracked.PrUrl = PrUrl;
        rig.Db.Context.SaveChanges();
        var later = next is { } status ? RunTimeline.SeedRun(rig.Db, id, rig.VersionId, status) : null;

        var thrown = await Record.ExceptionAsync(() => PrSync(rig).HandleWebhookAsync(merged
            ? new WebhookPayload("pull_request.closed", "repo-1", "7", PrUrl, "Please also update the docs", "merged")
            : new WebhookPayload("issue_comment.created", "repo-1", "7", PrUrl, "Please also update the docs", null)));

        Assert.Null(thrown);
        Assert.Equal(recorded, RunTimeline.Events(rig.Db, run.Id).Any(e => e.Data == "Please also update the docs"));
        Assert.Equal(merged, rig.Db.Fresh().LoopRuns.Single(r => r.Id == run.Id).IsPrMerged);
        if (later is not null)
        {
            Assert.Empty(RunTimeline.Events(rig.Db, later.Id));
            Assert.Equal(later.Status, rig.Db.Fresh().LoopRuns.Single(r => r.Id == later.Id).Status);
            Assert.NotEqual(RemoteWorkItemStatus.Done, (await rig.Manager.GetWorkItemAsync(id))!.Status);
        }
        else if (merged)
        {
            // The merged PR belongs to the item's latest run, which has nothing
            // left to drive it there, so the merge finishes the item.
            Assert.Equal(RemoteWorkItemStatus.Done, (await rig.Manager.GetWorkItemAsync(id))!.Status);
        }
    }
}
