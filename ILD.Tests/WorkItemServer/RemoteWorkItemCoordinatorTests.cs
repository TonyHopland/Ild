using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Logging;
using Moq;

namespace ILD.Tests.WorkItemServer;

public sealed class RemoteWorkItemCoordinatorTests
{
    private static readonly WorkItemServerOptions Opts = new() { BaseUrl = "http://x", ApiKey = "k" };

    private static RemoteWorkItem Item(string id, RemoteWorkItemStatus status, params string[] tags) => new()
    {
        Id = id, Title = "w", Status = status, Tags = tags,
    };

    /// <summary>
    /// A run store reporting no live local runs — the default for cases that
    /// aren't about the concurrency gate. <c>GetActiveWorkItemIdsAsync</c>
    /// always has to be stubbed: unstubbed, Moq hands back null rather than an
    /// empty list.
    /// </summary>
    private static ILoopRunStore NoLiveRuns()
    {
        var store = new Mock<ILoopRunStore>();
        store.Setup(s => s.GetActiveWorkItemIdsAsync()).ReturnsAsync(Array.Empty<string>());
        return store.Object;
    }

    [Fact]
    public async Task Claims_ready_items_when_template_resolves_uniquely()
    {
        var ready = Item(Guid.NewGuid().ToString(), RemoteWorkItemStatus.Ready, "build");

        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.PollAsync(Opts, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemotePollResponse { ReadyItems = new[] { ready } });
        client.Setup(c => c.TransitionAsync(Opts, ready.Id,
                It.Is<RemoteTransitionRequest>(r => r.TargetStatus == RemoteWorkItemStatus.Running),
                It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemoteTransitionResponse { Success = true, ActualStatus = RemoteWorkItemStatus.Running });

        var engine = new Mock<ILoopEngine>();
        var resolver = new Mock<ILoopTemplateResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
                .Returns(new LoopTemplateResolution(LoopTemplateResolutionKind.Single, Guid.NewGuid(), Array.Empty<string>()));

        var sut = new RemoteWorkItemCoordinator(client.Object, resolver.Object, engine.Object, NoLiveRuns());
        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        Assert.Single(result.Claimed);
        // The claim's whole point is the local run behind it — that run is what
        // holds the slot and keeps the item heartbeated from here on.
        engine.Verify(e => e.StartRunAsync(ready.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Escalates_to_humanfeedback_when_no_template_matches()
    {
        var ready = Item(Guid.NewGuid().ToString(), RemoteWorkItemStatus.Ready, "unknown");
        RemoteTransitionRequest? captured = null;

        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.PollAsync(Opts, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemotePollResponse { ReadyItems = new[] { ready } });
        client.Setup(c => c.TransitionAsync(Opts, ready.Id, It.IsAny<RemoteTransitionRequest>(), It.IsAny<CancellationToken>()))
              .Callback<WorkItemServerOptions, string, RemoteTransitionRequest, CancellationToken>((_, _, r, _) => captured = r)
              .ReturnsAsync(new RemoteTransitionResponse { Success = true, ActualStatus = RemoteWorkItemStatus.HumanFeedback });

        var engine = new Mock<ILoopEngine>();
        var resolver = new Mock<ILoopTemplateResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
                .Returns(new LoopTemplateResolution(LoopTemplateResolutionKind.None, null, Array.Empty<string>()));

        var sut = new RemoteWorkItemCoordinator(client.Object, resolver.Object, engine.Object, NoLiveRuns());
        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        Assert.Single(result.EscalatedToHumanFeedback);
        Assert.Equal(RemoteWorkItemStatus.HumanFeedback, captured!.TargetStatus);
        Assert.Contains("No loop", captured.Reason);
    }

    [Fact]
    public async Task Escalates_when_multiple_templates_match()
    {
        var ready = Item(Guid.NewGuid().ToString(), RemoteWorkItemStatus.Ready, "build", "deploy");
        RemoteTransitionRequest? captured = null;

        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.PollAsync(Opts, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemotePollResponse { ReadyItems = new[] { ready } });
        client.Setup(c => c.TransitionAsync(Opts, ready.Id, It.IsAny<RemoteTransitionRequest>(), It.IsAny<CancellationToken>()))
              .Callback<WorkItemServerOptions, string, RemoteTransitionRequest, CancellationToken>((_, _, r, _) => captured = r)
              .ReturnsAsync(new RemoteTransitionResponse { Success = true, ActualStatus = RemoteWorkItemStatus.HumanFeedback });

        var engine = new Mock<ILoopEngine>();
        var resolver = new Mock<ILoopTemplateResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
                .Returns(new LoopTemplateResolution(LoopTemplateResolutionKind.Ambiguous, null, new[] { "build", "deploy" }));

        var sut = new RemoteWorkItemCoordinator(client.Object, resolver.Object, engine.Object, NoLiveRuns());
        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        Assert.Single(result.EscalatedToHumanFeedback);
        Assert.Contains("Multiple loop templates", captured!.Reason);
    }

    [Fact]
    public async Task Resumes_waiting_for_ild_items_to_running()
    {
        var waiting = Item(Guid.NewGuid().ToString(), RemoteWorkItemStatus.WaitingForIld);

        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.PollAsync(Opts, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemotePollResponse { ActiveItems = new[] { waiting } });
        client.Setup(c => c.TransitionAsync(Opts, waiting.Id,
                It.Is<RemoteTransitionRequest>(r => r.TargetStatus == RemoteWorkItemStatus.Running),
                It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemoteTransitionResponse { Success = true, ActualStatus = RemoteWorkItemStatus.Running });

        var engine = new Mock<ILoopEngine>();
        var resolver = new Mock<ILoopTemplateResolver>();
        var sut = new RemoteWorkItemCoordinator(client.Object, resolver.Object, engine.Object, NoLiveRuns());
        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        Assert.Single(result.Resumed);
    }

    [Fact]
    public async Task Respects_max_concurrent_cap()
    {
        var ready1 = Item(Guid.NewGuid().ToString(), RemoteWorkItemStatus.Ready, "build");
        var ready2 = Item(Guid.NewGuid().ToString(), RemoteWorkItemStatus.Ready, "build");

        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.PollAsync(Opts, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemotePollResponse { ReadyItems = new[] { ready1, ready2 } });
        client.Setup(c => c.TransitionAsync(Opts, It.IsAny<string>(), It.IsAny<RemoteTransitionRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemoteTransitionResponse { Success = true, ActualStatus = RemoteWorkItemStatus.Running });

        var engine = new Mock<ILoopEngine>();
        var resolver = new Mock<ILoopTemplateResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
                .Returns(new LoopTemplateResolution(LoopTemplateResolutionKind.Single, Guid.NewGuid(), Array.Empty<string>()));

        var sut = new RemoteWorkItemCoordinator(client.Object, resolver.Object, engine.Object, NoLiveRuns());
        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 1);

        Assert.Single(result.Claimed);
        engine.Verify(e => e.StartRunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Skips_claim_when_server_reports_failure()
    {
        var ready = Item(Guid.NewGuid().ToString(), RemoteWorkItemStatus.Ready, "build");

        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.PollAsync(Opts, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemotePollResponse { ReadyItems = new[] { ready } });
        client.Setup(c => c.TransitionAsync(Opts, ready.Id, It.IsAny<RemoteTransitionRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemoteTransitionResponse { Success = false, ActualStatus = RemoteWorkItemStatus.Ready });

        var engine = new Mock<ILoopEngine>();
        var resolver = new Mock<ILoopTemplateResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
                .Returns(new LoopTemplateResolution(LoopTemplateResolutionKind.Single, Guid.NewGuid(), Array.Empty<string>()));

        var sut = new RemoteWorkItemCoordinator(client.Object, resolver.Object, engine.Object, NoLiveRuns());
        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        Assert.Empty(result.Claimed);
        engine.Verify(e => e.StartRunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Stops_heartbeating_an_item_once_its_run_is_over()
    {
        // A finished item leaves the heartbeat because its run is gone, not
        // because the server called it Done — the same release path as every
        // other terminal status.
        var done = Item(NewId(), RemoteWorkItemStatus.Done);

        var (client, heartbeats) = ScriptedClient(
            new RemotePollResponse { ActiveItems = new[] { done } });

        var activeRuns = NoActiveRuns();
        activeRuns.Add(done.Id);
        var sut = Coordinator(client, RunStoreWithActive(activeRuns));

        await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);
        activeRuns.Clear(); // the run completed
        await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        Assert.Equal(new[] { done.Id }, heartbeats[0]);
        Assert.Empty(heartbeats[1]);
    }

    [Fact]
    public async Task Reports_active_humanfeedback_for_grace_polling()
    {
        var hf = Item(Guid.NewGuid().ToString(), RemoteWorkItemStatus.HumanFeedback);

        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.PollAsync(Opts, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemotePollResponse { ActiveItems = new[] { hf } });

        var engine = new Mock<ILoopEngine>();
        var resolver = new Mock<ILoopTemplateResolver>();
        var sut = new RemoteWorkItemCoordinator(client.Object, resolver.Object, engine.Object, NoLiveRuns());
        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        Assert.True(result.HasActiveHumanFeedback);
    }

    [Fact]
    public async Task Notifies_signalr_when_claiming_ready_item()
    {
        var ready = Item(Guid.NewGuid().ToString(), RemoteWorkItemStatus.Ready, "build");

        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.PollAsync(Opts, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemotePollResponse { ReadyItems = new[] { ready } });
        client.Setup(c => c.TransitionAsync(Opts, ready.Id, It.IsAny<RemoteTransitionRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemoteTransitionResponse { Success = true, ActualStatus = RemoteWorkItemStatus.Running });

        var engine = new Mock<ILoopEngine>();
        var resolver = new Mock<ILoopTemplateResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
                .Returns(new LoopTemplateResolution(LoopTemplateResolutionKind.Single, Guid.NewGuid(), Array.Empty<string>()));

        var notifier = new Mock<IWorkItemNotifier>();
        var sut = new RemoteWorkItemCoordinator(
            client.Object, resolver.Object, engine.Object, NoLiveRuns(),
            workItemNotifier: notifier.Object);

        await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        notifier.Verify(n => n.WorkItemStateChangedAsync(
            ready.Id, RemoteWorkItemStatus.Ready, RemoteWorkItemStatus.Running), Times.Once);
    }

    [Fact]
    public async Task Notifies_signalr_when_resuming_waiting_for_ild_item()
    {
        var waiting = Item(Guid.NewGuid().ToString(), RemoteWorkItemStatus.WaitingForIld);

        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.PollAsync(Opts, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemotePollResponse { ActiveItems = new[] { waiting } });
        client.Setup(c => c.TransitionAsync(Opts, waiting.Id,
                It.Is<RemoteTransitionRequest>(r => r.TargetStatus == RemoteWorkItemStatus.Running),
                It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemoteTransitionResponse { Success = true, ActualStatus = RemoteWorkItemStatus.Running });

        var engine = new Mock<ILoopEngine>();
        var resolver = new Mock<ILoopTemplateResolver>();

        var notifier = new Mock<IWorkItemNotifier>();
        var sut = new RemoteWorkItemCoordinator(
            client.Object, resolver.Object, engine.Object, NoLiveRuns(),
            workItemNotifier: notifier.Object);

        await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        notifier.Verify(n => n.WorkItemStateChangedAsync(
            waiting.Id, RemoteWorkItemStatus.WaitingForIld, RemoteWorkItemStatus.Running), Times.Once);
    }

    [Fact]
    public async Task Does_not_claim_ready_items_when_paused()
    {
        var ready = Item(Guid.NewGuid().ToString(), RemoteWorkItemStatus.Ready, "build");

        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.PollAsync(Opts, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemotePollResponse { ReadyItems = new[] { ready } });

        var engine = new Mock<ILoopEngine>();
        var resolver = new Mock<ILoopTemplateResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
                .Returns(new LoopTemplateResolution(LoopTemplateResolutionKind.Single, Guid.NewGuid(), Array.Empty<string>()));

        var sut = new RemoteWorkItemCoordinator(client.Object, resolver.Object, engine.Object, NoLiveRuns());
        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5, claimReadyItems: false);

        Assert.Empty(result.Claimed);
        // Nothing should have been transitioned (no claim, no escalation) and no
        // run started — Ready items are left untouched for a human to promote.
        client.Verify(c => c.TransitionAsync(Opts, It.IsAny<string>(), It.IsAny<RemoteTransitionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        engine.Verify(e => e.StartRunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task When_paused_still_resumes_waiting_for_ild_and_closes_finished_runs()
    {
        // Everything except Ready→Running auto-promotion must keep working while
        // paused: a parked WaitingForIld run still resumes, a run behind an item
        // the server has finished is still closed, and a Ready item is left
        // untouched. Pause suppresses auto-promotion, not housekeeping — a
        // paused board that stopped releasing slots would come back from the
        // pause already stuck.
        var waiting = Item(NewId(), RemoteWorkItemStatus.WaitingForIld);
        var done = Item(NewId(), RemoteWorkItemStatus.Done);
        var ready = Item(NewId(), RemoteWorkItemStatus.Ready, "build");

        var (client, heartbeats) = ScriptedClient(new RemotePollResponse
        {
            ActiveItems = new[] { waiting, done },
            ReadyItems = new[] { ready },
        });

        // Both runs are alive going in; the Done item's is the one this pass
        // has to close.
        var activeRuns = NoActiveRuns();
        activeRuns.Add(waiting.Id);
        activeRuns.Add(done.Id);
        var doneRun = new LoopRun { Id = Guid.NewGuid(), WorkItemId = done.Id, Status = LoopRunStatus.WaitingHuman };
        var runStore = RunStoreWithActive(activeRuns);
        runStore.Setup(s => s.GetActiveByWorkItemAsync(done.Id)).ReturnsAsync(doneRun);
        var engine = new Mock<ILoopEngine>();

        var result = await Coordinator(client, runStore, engine)
            .RunPollCycleAsync(Opts, maxConcurrent: 5, claimReadyItems: false);

        Assert.Single(result.Resumed);
        Assert.Empty(result.Claimed);
        engine.Verify(e => e.StopRunAsync(doneRun.Id, It.IsAny<string>()), Times.Once);
        Assert.DoesNotContain(done.Id, result.SlotHolders);
        // The heartbeat is the derived set and nothing else — the two live runs
        // going in, never the Ready item the pause left alone.
        Assert.Equal(
            new[] { waiting.Id, done.Id }.OrderBy(x => x, StringComparer.Ordinal),
            heartbeats[0].OrderBy(x => x, StringComparer.Ordinal));
        // The Ready item was never claimed, but the WaitingForIld resume still fired.
        client.Verify(c => c.TransitionAsync(Opts, ready.Id, It.IsAny<RemoteTransitionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Does_not_notify_when_claim_fails()
    {
        var ready = Item(Guid.NewGuid().ToString(), RemoteWorkItemStatus.Ready, "build");

        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.PollAsync(Opts, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemotePollResponse { ReadyItems = new[] { ready } });
        client.Setup(c => c.TransitionAsync(Opts, ready.Id, It.IsAny<RemoteTransitionRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemoteTransitionResponse { Success = false, ActualStatus = RemoteWorkItemStatus.Ready });

        var engine = new Mock<ILoopEngine>();
        var resolver = new Mock<ILoopTemplateResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
                .Returns(new LoopTemplateResolution(LoopTemplateResolutionKind.Single, Guid.NewGuid(), Array.Empty<string>()));

        var notifier = new Mock<IWorkItemNotifier>();
        var sut = new RemoteWorkItemCoordinator(
            client.Object, resolver.Object, engine.Object, NoLiveRuns(),
            workItemNotifier: notifier.Object);

        await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        notifier.Verify(n => n.WorkItemStateChangedAsync(
            It.IsAny<string>(), It.IsAny<RemoteWorkItemStatus>(), It.IsAny<RemoteWorkItemStatus>()), Times.Never);
    }

    // ---- WI-165: a slot must be released when the run behind it ends --------
    //
    // The active set is both the concurrency gate and the heartbeat list. The
    // only in-pass release path filters on Status == Done, but the server
    // echoes every heartbeated id back with its *real* status — so a run that
    // ends with its item in Backlog / Ready / WorkQueue keeps its slot for the
    // lifetime of the process. Five of those take effective concurrency to
    // zero, silently, with a restart as the only recovery.
    //
    // These tests drive whole poll passes (claim, then observe what the server
    // reports next pass) instead of poking the tracker, so they describe
    // observable behaviour and survive Add/Remove being retired in favour of
    // an active set derived from live LoopRuns.

    private static string NewId() => Guid.NewGuid().ToString();

    /// <summary>
    /// A client that serves <paramref name="passes"/> to successive PollAsync
    /// calls (the last one repeats), records the heartbeat list it was handed
    /// on each call, and accepts every transition.
    /// </summary>
    private static (Mock<IWorkItemServerClient> Client, List<IReadOnlyList<string>> Heartbeats) ScriptedClient(
        params RemotePollResponse[] passes)
    {
        var heartbeats = new List<IReadOnlyList<string>>();
        var queue = new Queue<RemotePollResponse>(passes);

        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.PollAsync(Opts, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .Callback<WorkItemServerOptions, IReadOnlyList<string>, CancellationToken>(
                  (_, ids, _) => heartbeats.Add(ids.ToList()))
              .ReturnsAsync(() => queue.Count > 1 ? queue.Dequeue() : queue.Peek());
        client.Setup(c => c.TransitionAsync(Opts, It.IsAny<string>(), It.IsAny<RemoteTransitionRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((WorkItemServerOptions _, string _, RemoteTransitionRequest r, CancellationToken _) =>
                  new RemoteTransitionResponse { Success = true, ActualStatus = r.TargetStatus });

        return (client, heartbeats);
    }

    private static Mock<ILoopTemplateResolver> SingleTemplateResolver()
    {
        var resolver = new Mock<ILoopTemplateResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
                .Returns(new LoopTemplateResolution(LoopTemplateResolutionKind.Single, Guid.NewGuid(), Array.Empty<string>()));
        return resolver;
    }

    /// <summary>
    /// A run store whose Active Work Item Set is read live from
    /// <paramref name="active"/> on every call, so a test can move runs in and
    /// out between passes exactly as the engine would. Listed means "this item
    /// has a run the engine still considers alive"; anything not listed has
    /// terminated locally and must not hold a slot. Which run statuses count as
    /// alive is the store's business, and is pinned in
    /// <see cref="ILD.Tests.LoopRunStoreActiveWorkItemIdsTests"/>.
    /// </summary>
    private static Mock<ILoopRunStore> RunStoreWithActive(List<string> active)
    {
        var store = new Mock<ILoopRunStore>();
        store.Setup(s => s.GetActiveWorkItemIdsAsync()).ReturnsAsync(() => active.ToList());
        return store;
    }

    private static List<string> NoActiveRuns() => new();

    private static RemoteWorkItemCoordinator Coordinator(
        Mock<IWorkItemServerClient> client, Mock<ILoopRunStore> runStore, Mock<ILoopEngine>? engine = null) =>
        new(client.Object, SingleTemplateResolver().Object, (engine ?? new Mock<ILoopEngine>()).Object,
            runStore.Object);

    [Fact]
    public async Task Claims_a_ready_item_after_earlier_claims_terminated_outside_done()
    {
        // Five items are claimed, filling maxConcurrent. Their runs then end
        // without the items reaching Done — the server reports them as
        // Backlog / Ready / WorkQueue. No local run survives, so all five
        // slots are free and the next Ready item must be claimed.
        var leakedIds = Enumerable.Range(0, 5).Select(_ => NewId()).ToArray();
        var leakedStatuses = new[]
        {
            RemoteWorkItemStatus.Backlog,
            RemoteWorkItemStatus.Ready,
            RemoteWorkItemStatus.WorkQueue,
            RemoteWorkItemStatus.Backlog,
            RemoteWorkItemStatus.WorkQueue,
        };
        var fresh = Item(NewId(), RemoteWorkItemStatus.Ready, "build");

        var (client, _) = ScriptedClient(
            new RemotePollResponse
            {
                ReadyItems = leakedIds.Select(id => Item(id, RemoteWorkItemStatus.Ready, "build")).ToArray(),
            },
            new RemotePollResponse
            {
                // WorkItemService.PollAsync echoes back every heartbeated id
                // with whatever status it actually holds.
                ActiveItems = leakedIds.Select((id, i) => Item(id, leakedStatuses[i])).ToArray(),
                ReadyItems = new[] { fresh },
            });

        // All five runs are created during the first pass and are terminal
        // again before the second, so the store never reports one as active.
        var sut = Coordinator(client, RunStoreWithActive(NoActiveRuns()));

        var first = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);
        var second = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        // The five claims land in one pass, so the cap has to count claims
        // made during the pass, not just the set it started with.
        Assert.Equal(5, first.Claimed.Count);
        Assert.Contains(fresh.Id, second.Claimed.Select(c => c.Id));
    }

    [Fact]
    public async Task Reclaims_an_item_whose_own_previous_run_ended_back_in_ready()
    {
        // The #159 self-block. The item is claimed, its run finishes without
        // the item reaching Done, and the server puts it back in Ready — so it
        // returns in ActiveItems (the heartbeat echo) *and* in ReadyItems. The
        // only thing between it and a fresh run is the slot its own dead run
        // still occupies.
        var id = NewId();

        var (client, _) = ScriptedClient(
            new RemotePollResponse { ReadyItems = new[] { Item(id, RemoteWorkItemStatus.Ready, "build") } },
            new RemotePollResponse
            {
                ActiveItems = new[] { Item(id, RemoteWorkItemStatus.Ready) },
                ReadyItems = new[] { Item(id, RemoteWorkItemStatus.Ready, "build") },
            });

        // The run created by the first pass is terminal before the second.
        var sut = Coordinator(client, RunStoreWithActive(NoActiveRuns()));

        var first = await sut.RunPollCycleAsync(Opts, maxConcurrent: 1);
        var second = await sut.RunPollCycleAsync(Opts, maxConcurrent: 1);

        Assert.Single(first.Claimed);
        Assert.Single(second.Claimed);
        Assert.Equal(id, second.Claimed[0].Id);
    }

    [Fact]
    public async Task Human_parked_items_keep_their_slots_and_keep_being_heartbeated()
    {
        // The other side of the fix: HumanFeedback / WaitingForIld items are
        // genuinely active — their runs are parked at a gate, not dead. They
        // must keep occupying a slot AND keep appearing in the heartbeat: the
        // heartbeat is what the server's ReclaimStaleAsync keys off, so
        // dropping them hands the item to a second concurrent run. Asserting
        // only the slot count would miss that half.
        var parked = NewId();
        var waiting = NewId();
        var fresh = Item(NewId(), RemoteWorkItemStatus.Ready, "build");

        var (client, heartbeats) = ScriptedClient(
            new RemotePollResponse
            {
                ReadyItems = new[]
                {
                    Item(parked, RemoteWorkItemStatus.Ready, "build"),
                    Item(waiting, RemoteWorkItemStatus.Ready, "build"),
                },
            },
            new RemotePollResponse
            {
                ActiveItems = new[]
                {
                    Item(parked, RemoteWorkItemStatus.HumanFeedback),
                    Item(waiting, RemoteWorkItemStatus.WaitingForIld),
                },
                ReadyItems = new[] { fresh },
            });

        var activeRuns = NoActiveRuns();
        var sut = Coordinator(client, RunStoreWithActive(activeRuns));

        var first = await sut.RunPollCycleAsync(Opts, maxConcurrent: 2);

        // Both claims produced a run, and both parked at their human gate.
        activeRuns.Add(parked);
        activeRuns.Add(waiting);

        var second = await sut.RunPollCycleAsync(Opts, maxConcurrent: 2);

        Assert.Equal(2, first.Claimed.Count);
        Assert.Empty(second.Claimed);
        Assert.Equal(
            new[] { parked, waiting }.OrderBy(x => x, StringComparer.Ordinal),
            heartbeats[1].OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Logs_the_ids_holding_the_slots_when_a_pass_is_blocked_by_the_cap()
    {
        // A pass that sees Ready work but has no room must report that, and name
        // the ids holding the slots. Without it a leaked slot looks exactly like
        // an idle board — the reason this bug took an hour to find. The pass
        // reports; CapStallReporter decides how often to say it out loud (see
        // CapStallReporterTests), so this asserts the reported facts rather
        // than a log string.
        var busy = NewId();
        var fresh = Item(NewId(), RemoteWorkItemStatus.Ready, "build");

        var (client, _) = ScriptedClient(
            new RemotePollResponse { ReadyItems = new[] { Item(busy, RemoteWorkItemStatus.Ready, "build") } },
            new RemotePollResponse
            {
                ActiveItems = new[] { Item(busy, RemoteWorkItemStatus.Running) },
                ReadyItems = new[] { fresh },
            });

        var activeRuns = NoActiveRuns();
        var sut = Coordinator(client, RunStoreWithActive(activeRuns));

        var first = await sut.RunPollCycleAsync(Opts, maxConcurrent: 1);
        // The claim started a run, and it is still going.
        activeRuns.Add(busy);

        var second = await sut.RunPollCycleAsync(Opts, maxConcurrent: 1);

        // The first pass had room, so it was not blocked by anything.
        Assert.False(first.BlockedByCap);
        // Legitimately at the cap — the run really is alive.
        Assert.Empty(second.Claimed);
        Assert.True(second.BlockedByCap);
        Assert.Contains(busy, second.SlotHolders);
    }

    [Fact]
    public async Task Frees_the_slot_of_an_item_the_server_reports_done_and_closes_its_run()
    {
        // A run parked at a human gate does not end when someone marks its item
        // Done — nothing local is watching for that. Left alive it would sit in
        // the derived set forever, heartbeated and holding a slot, which is the
        // leak this work item exists to remove, arriving through another door.
        var finished = NewId();
        var fresh = Item(NewId(), RemoteWorkItemStatus.Ready, "build");

        var (client, heartbeats) = ScriptedClient(new RemotePollResponse
        {
            ActiveItems = new[] { Item(finished, RemoteWorkItemStatus.Done) },
            ReadyItems = new[] { fresh },
        });

        var parkedRun = new LoopRun
        {
            Id = Guid.NewGuid(), WorkItemId = finished, Status = LoopRunStatus.WaitingHuman,
        };
        var runStore = RunStoreWithActive(new List<string> { finished });
        runStore.Setup(s => s.GetActiveByWorkItemAsync(finished)).ReturnsAsync(parkedRun);

        var engine = new Mock<ILoopEngine>();

        var result = await Coordinator(client, runStore, engine).RunPollCycleAsync(Opts, maxConcurrent: 1);

        // Its slot came back inside the same pass, so the Ready item got in.
        Assert.False(result.BlockedByCap);
        Assert.DoesNotContain(finished, result.SlotHolders);
        Assert.Contains(fresh.Id, result.Claimed.Select(c => c.Id));
        // And the run behind it is closed, so it is gone from the next pass's
        // set too rather than coming back as an immortal WaitingHuman row.
        engine.Verify(e => e.StopRunAsync(parkedRun.Id, It.IsAny<string>()), Times.Once);
        // It was still heartbeated on the way in — the pass reacts to what the
        // poll told it, it cannot know in advance.
        Assert.Equal(new[] { finished }, heartbeats[0]);
    }

    [Fact]
    public async Task Keeps_the_slot_when_closing_a_finished_item_s_run_fails()
    {
        // The run is only over once the write says so. Releasing the slot on
        // the strength of an attempt would let a failed write hand this pass a
        // slot whose run is still alive, and the pass would claim past the cap
        // — the same over-subscription from the other direction.
        var finished = NewId();
        var fresh = Item(NewId(), RemoteWorkItemStatus.Ready, "build");

        var (client, _) = ScriptedClient(new RemotePollResponse
        {
            ActiveItems = new[] { Item(finished, RemoteWorkItemStatus.Done) },
            ReadyItems = new[] { fresh },
        });

        var liveRun = new LoopRun { Id = Guid.NewGuid(), WorkItemId = finished, Status = LoopRunStatus.Running };
        var runStore = RunStoreWithActive(new List<string> { finished });
        runStore.Setup(s => s.GetActiveByWorkItemAsync(finished)).ReturnsAsync(liveRun);
        var engine = new Mock<ILoopEngine>();
        engine.Setup(e => e.StopRunAsync(liveRun.Id, It.IsAny<string>()))
              .ThrowsAsync(new InvalidOperationException("database unavailable"));

        var result = await Coordinator(client, runStore, engine).RunPollCycleAsync(Opts, maxConcurrent: 1);

        Assert.Contains(finished, result.SlotHolders);
        Assert.Empty(result.Claimed);
        Assert.True(result.BlockedByCap);
    }

    [Fact]
    public async Task Frees_the_slot_within_the_pass_when_a_claimed_item_fails_to_start()
    {
        // The unwind is the one path that hands a slot back mid-pass, and with
        // the ledger now local to the pass that release is what lets the next
        // Ready item in. maxConcurrent 1 makes the second claim impossible
        // unless the first item really did give its slot up.
        var doomed = Item(NewId(), RemoteWorkItemStatus.Ready, "build");
        var next = Item(NewId(), RemoteWorkItemStatus.Ready, "build");

        var (client, _) = ScriptedClient(new RemotePollResponse { ReadyItems = new[] { doomed, next } });

        var engine = new Mock<ILoopEngine>();
        engine.Setup(e => e.StartRunAsync(doomed.Id, It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("no start node"));

        var sut = Coordinator(client, RunStoreWithActive(NoActiveRuns()), engine: engine);
        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 1);

        Assert.Equal(doomed.Id, Assert.Single(result.EscalatedToHumanFeedback).Id);
        Assert.Contains(next.Id, result.Claimed.Select(c => c.Id));
        // Handed back for review rather than left Running with no driver.
        client.Verify(c => c.TransitionAsync(Opts, doomed.Id,
            It.Is<RemoteTransitionRequest>(r => r.TargetStatus == RemoteWorkItemStatus.HumanFeedback
                && r.Reason!.Contains("Failed to start run")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Full_slots_are_not_reported_as_blocked_when_there_is_no_ready_work()
    {
        // Slots can be full with nothing waiting on them. That is not a stall,
        // and reporting it as one would have the scheduler announce a healthy
        // board every pass — 5s apart in grace mode.
        var busy = NewId();

        var (client, _) = ScriptedClient(new RemotePollResponse
        {
            ActiveItems = new[] { Item(busy, RemoteWorkItemStatus.Running) },
        });

        var activeRuns = NoActiveRuns();
        activeRuns.Add(busy);

        var result = await Coordinator(client, RunStoreWithActive(activeRuns))
            .RunPollCycleAsync(Opts, maxConcurrent: 1);

        Assert.False(result.BlockedByCap);
        // The slot is still held, though — the ledger is reported either way.
        Assert.Equal(new[] { busy }, result.SlotHolders);
    }

    // ---- Resume gating vs. the work item's AI provider override -------------
    //
    // The resume gate must peek the capacity of the provider the AI node will
    // ACTUALLY run against — i.e. after the work item's override is applied,
    // using the same OverrideAll / OverrideDefault semantics AINodeExecutor
    // uses. Gating on the node's pinned provider instead both strands items
    // (their real provider is idle) and pointlessly resumes them (their real
    // provider is full, so the executor immediately parks them again).

    private static AiProvider Provider(string name, int parallelism) => new()
    {
        Id = Guid.NewGuid(), Name = name, Type = "claude", Model = "m", Parallelism = parallelism,
    };

    /// <summary>Fills every slot of <paramref name="p"/> so it reports no capacity.</summary>
    private static void Saturate(IAiProviderConcurrencyTracker tracker, AiProvider p)
    {
        for (var i = 0; i < p.Parallelism; i++)
            Assert.True(tracker.TryEnter(p.Id, p.Parallelism));
    }

    /// <summary>
    /// A coordinator whose single WaitingForIld item has a run parked on an AI
    /// node. <paramref name="nodeConfig"/> is the node's raw config JSON (pinning
    /// a provider, or not); the item carries the override the server reported.
    /// </summary>
    private static (RemoteWorkItemCoordinator Sut, RemoteWorkItem Waiting) BuildResumeGate(
        string? nodeConfig,
        RemoteAiProviderOverrideMode overrideMode,
        Guid? overrideId,
        IAiProviderConcurrencyTracker concurrency,
        AiProvider? defaultProvider,
        params AiProvider[] providers)
    {
        var waiting = new RemoteWorkItem
        {
            Id = Guid.NewGuid().ToString(),
            Title = "w",
            Status = RemoteWorkItemStatus.WaitingForIld,
            AiProviderOverride = overrideMode,
            AiProviderOverrideId = overrideId,
        };

        var versionId = Guid.NewGuid();
        var node = new LoopNode
        {
            Id = Guid.NewGuid(), LoopTemplateVersionId = versionId,
            NodeType = NodeType.AI, Label = "ai", Config = nodeConfig,
        };
        var run = new LoopRun
        {
            Id = Guid.NewGuid(), WorkItemId = waiting.Id,
            LoopTemplateVersionId = versionId, CurrentNodeId = node.Id,
        };

        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.PollAsync(Opts, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemotePollResponse { ActiveItems = new[] { waiting } });
        client.Setup(c => c.TransitionAsync(Opts, waiting.Id,
                It.Is<RemoteTransitionRequest>(r => r.TargetStatus == RemoteWorkItemStatus.Running),
                It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemoteTransitionResponse { Success = true, ActualStatus = RemoteWorkItemStatus.Running });

        var runStore = new Mock<ILoopRunStore>();
        runStore.Setup(s => s.GetCurrentByWorkItemAsync(waiting.Id)).ReturnsAsync(run);
        runStore.Setup(s => s.GetNodesForVersionAsync(versionId))
                .ReturnsAsync(new[] { node });
        // Unstubbed, Moq hands back null here rather than an empty list, which
        // blows up any caller that reads the active set. These cases are about
        // the resume gate, not concurrency, so keep the set empty.
        runStore.Setup(s => s.GetActiveWorkItemIdsAsync()).ReturnsAsync(Array.Empty<string>());

        var providerStore = new Mock<IProviderStore>();
        foreach (var p in providers)
            providerStore.Setup(s => s.GetAiProviderByIdAsync(p.Id)).ReturnsAsync(p);
        providerStore.Setup(s => s.GetDefaultAiProviderAsync()).ReturnsAsync(defaultProvider);

        var sut = new RemoteWorkItemCoordinator(
            client.Object, new Mock<ILoopTemplateResolver>().Object, new Mock<ILoopEngine>().Object,
            runStore.Object, providerStore: providerStore.Object, aiTracker: concurrency);

        return (sut, waiting);
    }

    [Fact]
    public async Task Does_not_resume_when_the_override_target_is_at_capacity()
    {
        // Node pins A (idle); the item overrides every AI node to B, which is
        // full. The run would execute against B, so it must stay parked.
        var pinnedA = Provider("A", parallelism: 2);
        var overrideB = Provider("B", parallelism: 1);
        var tracker = new AiProviderConcurrencyTracker();
        Saturate(tracker, overrideB);

        var (sut, _) = BuildResumeGate(
            $@"{{""aiProviderId"":""{pinnedA.Id}""}}",
            RemoteAiProviderOverrideMode.OverrideAll, overrideB.Id,
            tracker, defaultProvider: null, pinnedA, overrideB);

        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        Assert.Empty(result.Resumed);
    }

    [Fact]
    public async Task Resumes_when_the_override_target_is_idle_though_the_pinned_provider_is_full()
    {
        // The inverse: A (pinned) is saturated but irrelevant — the override
        // sends this run to B, which is idle. Gating on A strands the item.
        var pinnedA = Provider("A", parallelism: 1);
        var overrideB = Provider("B", parallelism: 4);
        var tracker = new AiProviderConcurrencyTracker();
        Saturate(tracker, pinnedA);

        var (sut, _) = BuildResumeGate(
            $@"{{""aiProviderId"":""{pinnedA.Id}""}}",
            RemoteAiProviderOverrideMode.OverrideAll, overrideB.Id,
            tracker, defaultProvider: null, pinnedA, overrideB);

        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        Assert.Single(result.Resumed);
    }

    [Fact]
    public async Task Resumes_when_the_override_target_has_unlimited_parallelism()
    {
        // Parallelism 0 means unlimited: B never blocks, however many runs are
        // already on it. A fix must not treat 0 as "no slots".
        var pinnedA = Provider("A", parallelism: 1);
        var overrideB = Provider("B", parallelism: 0);
        var tracker = new AiProviderConcurrencyTracker();
        Saturate(tracker, pinnedA);
        for (var i = 0; i < 5; i++) tracker.TryEnter(overrideB.Id, overrideB.Parallelism);

        var (sut, _) = BuildResumeGate(
            $@"{{""aiProviderId"":""{pinnedA.Id}""}}",
            RemoteAiProviderOverrideMode.OverrideAll, overrideB.Id,
            tracker, defaultProvider: null, pinnedA, overrideB);

        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        Assert.Single(result.Resumed);
    }

    [Fact]
    public async Task OverrideDefault_gates_on_the_override_target_when_the_node_is_not_pinned()
    {
        // Unpinned node + OverrideDefault → the override applies, so the run
        // lands on B. B is full, so no resume — even though the default is idle.
        var defaultD = Provider("D", parallelism: 4);
        var overrideB = Provider("B", parallelism: 1);
        var tracker = new AiProviderConcurrencyTracker();
        Saturate(tracker, overrideB);

        var (sut, _) = BuildResumeGate(
            "{}", RemoteAiProviderOverrideMode.OverrideDefault, overrideB.Id,
            tracker, defaultProvider: defaultD, defaultD, overrideB);

        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        Assert.Empty(result.Resumed);
    }

    [Fact]
    public async Task OverrideDefault_gates_on_the_pinned_provider_when_the_node_is_pinned()
    {
        // OverrideDefault must leave a deliberately pinned node alone, so the
        // saturated A still blocks the resume even though B is idle.
        var pinnedA = Provider("A", parallelism: 1);
        var overrideB = Provider("B", parallelism: 4);
        var tracker = new AiProviderConcurrencyTracker();
        Saturate(tracker, pinnedA);

        var (sut, _) = BuildResumeGate(
            $@"{{""aiProviderId"":""{pinnedA.Id}""}}",
            RemoteAiProviderOverrideMode.OverrideDefault, overrideB.Id,
            tracker, defaultProvider: null, pinnedA, overrideB);

        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        Assert.Empty(result.Resumed);
    }

    [Fact]
    public async Task Override_without_a_target_provider_gates_on_the_pinned_provider()
    {
        // Mode set but no target → no override, so A (full) governs.
        var pinnedA = Provider("A", parallelism: 1);
        var tracker = new AiProviderConcurrencyTracker();
        Saturate(tracker, pinnedA);

        var (sut, _) = BuildResumeGate(
            $@"{{""aiProviderId"":""{pinnedA.Id}""}}",
            RemoteAiProviderOverrideMode.OverrideAll, overrideId: null,
            tracker, defaultProvider: null, pinnedA);

        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        Assert.Empty(result.Resumed);
    }

    [Fact]
    public async Task Does_not_resume_an_unpinned_node_when_the_default_provider_is_at_capacity()
    {
        // No pin, no override → the node falls back to the default provider,
        // which is full. Reporting capacity here resumes into an immediate park.
        var defaultD = Provider("D", parallelism: 1);
        var tracker = new AiProviderConcurrencyTracker();
        Saturate(tracker, defaultD);

        var (sut, _) = BuildResumeGate(
            "{}", RemoteAiProviderOverrideMode.None, overrideId: null,
            tracker, defaultProvider: defaultD, defaultD);

        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        Assert.Empty(result.Resumed);
    }
}
