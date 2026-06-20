using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

public class PrStatusPollServiceTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";

    private static RemotePrSnapshot Snapshot(
        string state = "open",
        bool merged = false,
        RemotePrCiStatus ci = RemotePrCiStatus.None,
        bool approved = false,
        bool changesRequested = false)
        => new("t", "b", state, merged, null, null, ci, approved, changesRequested,
            Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow);

    private static LoopNodeEdge CustomEdge(Guid sourceNodeId, string name) => new()
    {
        Id = Guid.NewGuid(),
        SourceNodeId = sourceNodeId,
        TargetNodeId = Guid.NewGuid(),
        EdgeType = EdgeType.Custom,
        Name = name,
    };

    private sealed class Harness
    {
        public LoopRun Run { get; }
        public LoopRunNode RunNode { get; }
        public Mock<ILoopRunStore> Runs { get; } = new();
        public Mock<IRemoteProvider> Remote { get; } = new();
        public Mock<ILoopEngine> Engine { get; } = new();
        public Mock<IRunNotifier> Notifier { get; } = new();

        public Harness(RemotePrSnapshot snapshot, string? baseline, params LoopNodeEdge[] edges)
        {
            var loopNodeId = Guid.NewGuid();
            Run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = "wi-1",
                PrUrl = PrUrl,
                Status = LoopRunStatus.WaitingHuman,
                CurrentNodeId = loopNodeId,
                PrPolledEdgeStates = baseline,
            };
            RunNode = new LoopRunNode
            {
                Id = Guid.NewGuid(),
                LoopRunId = Run.Id,
                LoopNodeId = loopNodeId,
                Status = LoopRunNodeStatus.WaitingHuman,
            };

            Runs.Setup(s => s.GetPrAwaitingMergeRunsAsync()).ReturnsAsync(new[] { Run });
            Runs.Setup(s => s.GetRunNodeAsync(Run.Id, loopNodeId)).ReturnsAsync(RunNode);
            Runs.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(edges);
            Remote.Setup(r => r.GetPullRequestSnapshotAsync("https://github.com/team/repo", "7"))
                .ReturnsAsync(snapshot);
        }

        public PrStatusPollService Build() => new(
            Runs.Object, Remote.Object, Engine.Object, Notifier.Object,
            NullLogger<PrStatusPollService>.Instance);
    }

    [Fact]
    public async Task Persists_snapshot_and_pushes_gui_update_every_tick()
    {
        var h = new Harness(Snapshot(ci: RemotePrCiStatus.Pending), baseline: null);
        await h.Build().PollOnceAsync();

        h.Runs.Verify(s => s.UpdateRunAsync(It.Is<LoopRun>(r => r.PrSnapshot != null)), Times.Once);
        h.Notifier.Verify(n => n.PrSnapshotChangedAsync(h.Run.Id), Times.Once);
        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }

    [Fact]
    public async Task Fires_connected_edge_on_newly_true_state()
    {
        var h = new Harness(Snapshot(ci: RemotePrCiStatus.Failed), baseline: null);
        h.Runs.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>()))
            .ReturnsAsync(new[] { CustomEdge(h.RunNode.LoopNodeId, PrNodeEdges.OnCiFailed) });

        await h.Build().PollOnceAsync();

        h.Engine.Verify(e => e.SignalNodeResultAsync(h.Run.Id, h.RunNode.Id,
            It.Is<NodeSignal>(sig => sig.EdgeName == PrNodeEdges.OnCiFailed)), Times.Once);
    }

    [Fact]
    public async Task Does_not_fire_when_edge_unconnected_but_still_persists()
    {
        var h = new Harness(Snapshot(ci: RemotePrCiStatus.Failed), baseline: null);
        await h.Build().PollOnceAsync();

        h.Runs.Verify(s => s.UpdateRunAsync(It.Is<LoopRun>(r => r.PrPolledEdgeStates!.Contains(PrNodeEdges.OnCiFailed))), Times.Once);
        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }

    [Fact]
    public async Task Fires_highest_priority_among_connected_newly_true()
    {
        // Both changes-requested and CI-failed newly true and connected; the
        // higher-priority on_rejected wins.
        var h = new Harness(Snapshot(ci: RemotePrCiStatus.Failed, changesRequested: true), baseline: null);
        h.Runs.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>()))
            .ReturnsAsync(new[]
            {
                CustomEdge(h.RunNode.LoopNodeId, PrNodeEdges.OnCiFailed),
                CustomEdge(h.RunNode.LoopNodeId, PrNodeEdges.OnRejected),
            });

        await h.Build().PollOnceAsync();

        h.Engine.Verify(e => e.SignalNodeResultAsync(h.Run.Id, h.RunNode.Id,
            It.Is<NodeSignal>(sig => sig.EdgeName == PrNodeEdges.OnRejected)), Times.Once);
    }

    [Fact]
    public async Task Does_not_refire_a_state_already_in_the_baseline()
    {
        // CI was already failing last tick (in the baseline), so it is not a
        // transition this tick and must not fire again.
        var h = new Harness(Snapshot(ci: RemotePrCiStatus.Failed), baseline: PrNodeEdges.OnCiFailed);
        h.Runs.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>()))
            .ReturnsAsync(new[] { CustomEdge(h.RunNode.LoopNodeId, PrNodeEdges.OnCiFailed) });

        await h.Build().PollOnceAsync();

        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }
}
