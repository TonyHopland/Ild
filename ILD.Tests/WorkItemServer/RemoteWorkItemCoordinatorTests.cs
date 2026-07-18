using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Moq;

namespace ILD.Tests.WorkItemServer;

public sealed class RemoteWorkItemCoordinatorTests
{
    private static readonly WorkItemServerOptions Opts = new() { BaseUrl = "http://x", ApiKey = "k" };

    private static RemoteWorkItem Item(string id, RemoteWorkItemStatus status, params string[] tags) => new()
    {
        Id = id, Title = "w", Status = status, Tags = tags,
    };

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

        var tracker = new InMemoryActiveWorkItemTracker();
        var engine = new Mock<ILoopEngine>();
        var resolver = new Mock<ILoopTemplateResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
                .Returns(new LoopTemplateResolution(LoopTemplateResolutionKind.Single, Guid.NewGuid(), Array.Empty<string>()));

        var sut = new RemoteWorkItemCoordinator(client.Object, tracker, resolver.Object, engine.Object);
        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        Assert.Single(result.Claimed);
        Assert.Contains(ready.Id, tracker.Snapshot());
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

        var sut = new RemoteWorkItemCoordinator(client.Object, new InMemoryActiveWorkItemTracker(), resolver.Object, engine.Object);
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

        var sut = new RemoteWorkItemCoordinator(client.Object, new InMemoryActiveWorkItemTracker(), resolver.Object, engine.Object);
        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        Assert.Single(result.EscalatedToHumanFeedback);
        Assert.Contains("Multiple loop templates", captured!.Reason);
    }

    [Fact]
    public async Task Resumes_waiting_for_ild_items_to_running()
    {
        var waiting = Item(Guid.NewGuid().ToString(), RemoteWorkItemStatus.WaitingForIld);
        var tracker = new InMemoryActiveWorkItemTracker();
        tracker.Add(waiting.Id);

        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.PollAsync(Opts, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemotePollResponse { ActiveItems = new[] { waiting } });
        client.Setup(c => c.TransitionAsync(Opts, waiting.Id,
                It.Is<RemoteTransitionRequest>(r => r.TargetStatus == RemoteWorkItemStatus.Running),
                It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemoteTransitionResponse { Success = true, ActualStatus = RemoteWorkItemStatus.Running });

        var engine = new Mock<ILoopEngine>();
        var resolver = new Mock<ILoopTemplateResolver>();
        var sut = new RemoteWorkItemCoordinator(client.Object, tracker, resolver.Object, engine.Object);
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

        var tracker = new InMemoryActiveWorkItemTracker();
        var sut = new RemoteWorkItemCoordinator(client.Object, tracker, resolver.Object, engine.Object);
        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 1);

        Assert.Single(result.Claimed);
        Assert.Equal(1, tracker.Count);
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

        var tracker = new InMemoryActiveWorkItemTracker();
        var sut = new RemoteWorkItemCoordinator(client.Object, tracker, resolver.Object, engine.Object);
        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        Assert.Empty(result.Claimed);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public async Task Drops_done_items_from_tracker()
    {
        var done = Item(Guid.NewGuid().ToString(), RemoteWorkItemStatus.Done);
        var tracker = new InMemoryActiveWorkItemTracker();
        tracker.Add(done.Id);

        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.PollAsync(Opts, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemotePollResponse { ActiveItems = new[] { done } });

        var engine = new Mock<ILoopEngine>();
        var resolver = new Mock<ILoopTemplateResolver>();
        var sut = new RemoteWorkItemCoordinator(client.Object, tracker, resolver.Object, engine.Object);
        await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        Assert.DoesNotContain(done.Id, tracker.Snapshot());
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
        var sut = new RemoteWorkItemCoordinator(client.Object, new InMemoryActiveWorkItemTracker(), resolver.Object, engine.Object);
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
            client.Object, new InMemoryActiveWorkItemTracker(), resolver.Object, engine.Object,
            workItemNotifier: notifier.Object);

        await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        notifier.Verify(n => n.WorkItemStateChangedAsync(
            ready.Id, RemoteWorkItemStatus.Ready, RemoteWorkItemStatus.Running), Times.Once);
    }

    [Fact]
    public async Task Notifies_signalr_when_resuming_waiting_for_ild_item()
    {
        var waiting = Item(Guid.NewGuid().ToString(), RemoteWorkItemStatus.WaitingForIld);
        var tracker = new InMemoryActiveWorkItemTracker();
        tracker.Add(waiting.Id);

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
            client.Object, tracker, resolver.Object, engine.Object,
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

        var tracker = new InMemoryActiveWorkItemTracker();
        var engine = new Mock<ILoopEngine>();
        var resolver = new Mock<ILoopTemplateResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
                .Returns(new LoopTemplateResolution(LoopTemplateResolutionKind.Single, Guid.NewGuid(), Array.Empty<string>()));

        var sut = new RemoteWorkItemCoordinator(client.Object, tracker, resolver.Object, engine.Object);
        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5, claimReadyItems: false);

        Assert.Empty(result.Claimed);
        Assert.Equal(0, tracker.Count);
        // Nothing should have been transitioned (no claim, no escalation) and no
        // run started — Ready items are left untouched for a human to promote.
        client.Verify(c => c.TransitionAsync(Opts, It.IsAny<string>(), It.IsAny<RemoteTransitionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        engine.Verify(e => e.StartRunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task When_paused_still_resumes_waiting_for_ild_and_drops_done()
    {
        // Everything except Ready→Running auto-promotion must keep working while
        // paused: a parked WaitingForIld run still resumes, a finished item still
        // leaves the heartbeat set, and a Ready item is left untouched.
        var waiting = Item(Guid.NewGuid().ToString(), RemoteWorkItemStatus.WaitingForIld);
        var done = Item(Guid.NewGuid().ToString(), RemoteWorkItemStatus.Done);
        var ready = Item(Guid.NewGuid().ToString(), RemoteWorkItemStatus.Ready, "build");

        var tracker = new InMemoryActiveWorkItemTracker();
        tracker.Add(waiting.Id);
        tracker.Add(done.Id);

        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.PollAsync(Opts, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemotePollResponse
              {
                  ActiveItems = new[] { waiting, done },
                  ReadyItems = new[] { ready },
              });
        client.Setup(c => c.TransitionAsync(Opts, waiting.Id,
                It.Is<RemoteTransitionRequest>(r => r.TargetStatus == RemoteWorkItemStatus.Running),
                It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemoteTransitionResponse { Success = true, ActualStatus = RemoteWorkItemStatus.Running });

        var engine = new Mock<ILoopEngine>();
        var resolver = new Mock<ILoopTemplateResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
                .Returns(new LoopTemplateResolution(LoopTemplateResolutionKind.Single, Guid.NewGuid(), Array.Empty<string>()));

        var sut = new RemoteWorkItemCoordinator(client.Object, tracker, resolver.Object, engine.Object);
        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 5, claimReadyItems: false);

        Assert.Single(result.Resumed);
        Assert.DoesNotContain(done.Id, tracker.Snapshot());
        Assert.Empty(result.Claimed);
        Assert.DoesNotContain(ready.Id, tracker.Snapshot());
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
            client.Object, new InMemoryActiveWorkItemTracker(), resolver.Object, engine.Object,
            workItemNotifier: notifier.Object);

        await sut.RunPollCycleAsync(Opts, maxConcurrent: 5);

        notifier.Verify(n => n.WorkItemStateChangedAsync(
            It.IsAny<string>(), It.IsAny<RemoteWorkItemStatus>(), It.IsAny<RemoteWorkItemStatus>()), Times.Never);
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

        var providerStore = new Mock<IProviderStore>();
        foreach (var p in providers)
            providerStore.Setup(s => s.GetAiProviderByIdAsync(p.Id)).ReturnsAsync(p);
        providerStore.Setup(s => s.GetDefaultAiProviderAsync()).ReturnsAsync(defaultProvider);

        var sut = new RemoteWorkItemCoordinator(
            client.Object, new InMemoryActiveWorkItemTracker(),
            new Mock<ILoopTemplateResolver>().Object, new Mock<ILoopEngine>().Object,
            loopRunStore: runStore.Object, providerStore: providerStore.Object, aiTracker: concurrency);

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
