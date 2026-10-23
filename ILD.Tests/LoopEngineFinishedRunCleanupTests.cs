using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A run that ends loses its worktree, local branch and session snapshots, but only once
/// no loop drives it any more; its row, event log included, stays.
/// </summary>
public class LoopEngineFinishedRunCleanupTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_run_that_completes_at_its_Cleanup_node_loses_its_local_state_after_the_loop_exits_and_keeps_its_history()
    {
        var reclaimer = ReclaimerRemovingNothing();
        using var h = NewHarness(reclaimer, out var cleaner);
        h.AddNode("c", NodeType.Cleanup);
        h.Registry.Register(new ScriptedExecutor(NodeType.Cleanup,
            new NodeOutcome.NodeStarting("c"),
            new NodeOutcome.Terminal("cleaned")));
        var run = h.SeedRun("c");
        await GiveLocalStateAsync(h, run);
        bool? ownedWhileRemoving = null;
        reclaimer.Setup(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>())).Returns(async (LoopRun r) =>
        {
            ownedWhileRemoving = (await h.Engine.GetActiveRunIdsAsync()).Contains(r.Id);
            return true;
        });

        await h.LaunchAsync();
        await cleaner.Removed(run.Id).WaitAsync(Guard, TestContext.Current.CancellationToken);
        await h.WaitUntilIdleAsync();

        var after = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Completed, after.Status);
        Assert.Null(after.WorktreePath);
        Assert.Null(after.BranchName);
        Assert.Equal(0, FinishedRunCleanerWiring.SnapshotCount(h.Db, run.Id));
        Assert.Contains(h.ReloadEvents(), e => e.EventType == EventType.LoopRunCompleted);
        Assert.NotEmpty(h.ReloadRunNodes());
        Assert.False(ownedWhileRemoving);
        reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.Is<LoopRun>(x => x.Id == run.Id)), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelling_a_parked_run_removes_its_local_state_inline(bool viaCancelButton)
    {
        var reclaimer = ReclaimerRemovingNothing();
        using var h = NewHarness(reclaimer, out _);
        h.AddNode("a", NodeType.AI);
        var run = h.SeedRun("a", LoopRunStatus.WaitingHuman);
        await GiveLocalStateAsync(h, run);

        if (viaCancelButton)
            await h.Engine.CancelRunAsync(run.Id);
        else
            await h.Engine.StopRunAsync(run.Id, "PR closed");

        var after = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Cancelled, after.Status);
        Assert.Null(after.WorktreePath);
        Assert.Null(after.BranchName);
        Assert.Equal(0, FinishedRunCleanerWiring.SnapshotCount(h.Db, run.Id));
        Assert.Contains(h.ReloadEvents(), e => e.EventType == EventType.LoopRunCancelled);
    }

    [Fact]
    public async Task A_run_whose_removal_fails_still_completes_and_its_item_still_goes_to_Done()
    {
        var reclaimer = new Mock<IRunReclaimer>();
        reclaimer.Setup(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>())).ThrowsAsync(new IOException("Directory not empty"));
        using var h = NewHarness(reclaimer, out _);
        h.AddNode("a", NodeType.AI);
        var run = h.SeedRun("a", LoopRunStatus.WaitingHuman);
        await GiveLocalStateAsync(h, run);

        await h.Engine.CancelRunAsync(run.Id);

        var after = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Cancelled, after.Status);
        Assert.Equal("/tmp/wt/engine-run", after.WorktreePath);
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            It.IsAny<ILD.Core.Services.Remote.WorkItemActor>(), h.WorkItemId, ILD.Core.Services.Remote.RemoteWorkItemStatus.HumanFeedback,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Once);
    }

    private static Mock<IRunReclaimer> ReclaimerRemovingNothing()
    {
        var m = new Mock<IRunReclaimer>();
        m.Setup(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>())).ReturnsAsync(true);
        return m;
    }

    private static LoopEngineHarness NewHarness(Mock<IRunReclaimer> reclaimer, out SignallingCleaner cleaner)
    {
        var signalling = new SignallingCleaner();
        cleaner = signalling;
        return new LoopEngineHarness(configure: services =>
        {
            services.AddSingleton(reclaimer.Object);
            services.AddSingleton<IAdapterSessionSnapshotStore>(sp => new AdapterSessionSnapshotStore(sp.GetRequiredService<AppDbContext>()));
            services.AddRealFinishedRunCleaner();
            var real = services.Last(d => d.ServiceType == typeof(IFinishedRunCleaner));
            services.Remove(real);
            services.AddSingleton<IFinishedRunCleaner>(sp =>
            {
                signalling.Inner = (IFinishedRunCleaner)real.ImplementationFactory!(sp);
                return signalling;
            });
        });
    }

    private static async Task GiveLocalStateAsync(LoopEngineHarness h, LoopRun run)
    {
        run.WorktreePath = "/tmp/wt/engine-run";
        run.BranchName = "ild/wi-x-run-engine";
        await h.Db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await FinishedRunCleanerWiring.SeedSnapshotAsync(h.Db, run.Id);
    }

    /// <summary>The real cleaner, reporting when it has removed a run's state.</summary>
    internal sealed class SignallingCleaner : IFinishedRunCleaner
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource> _removed = new();

        public IFinishedRunCleaner Inner { get; set; } = null!;

        public Task Removed(Guid runId) => Signal(runId).Task;

        public async Task<bool> RemoveLocalStateAsync(Guid runId, bool allowFailed = false)
        {
            var removed = await Inner.RemoveLocalStateAsync(runId, allowFailed);
            if (removed) Signal(runId).TrySetResult();
            return removed;
        }

        private TaskCompletionSource Signal(Guid runId)
            => _removed.GetOrAdd(runId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
