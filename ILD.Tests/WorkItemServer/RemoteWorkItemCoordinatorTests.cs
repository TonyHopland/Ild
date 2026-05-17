using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
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

        Assert.Equal(1, result.Claimed.Count());
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

        Assert.Equal(1, result.EscalatedToHumanFeedback.Count());
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

        Assert.Equal(1, result.EscalatedToHumanFeedback.Count());
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

        Assert.Equal(1, result.Resumed.Count());
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

        Assert.Equal(1, result.Claimed.Count());
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
}
