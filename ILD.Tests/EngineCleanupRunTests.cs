using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Enums;
using Moq;

namespace ILD.Tests;

public class EngineCleanupRunTests
{
    [Fact]
    public async Task CleanupRunAsync_transitions_run_to_Completed_and_work_item_to_Done()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        h.AddNode("c", NodeType.Cleanup);

        var cleanup = new ScriptedExecutor(NodeType.Cleanup,
            new NodeOutcome.NodeStarting("c"),
            new NodeOutcome.Terminal("cleaned"));
        h.Registry.Register(cleanup);

        // Run is stuck in WaitingHuman — cleanup should complete the run.
        h.SeedRun("a", LoopRunStatus.WaitingHuman);

        await h.Engine.CleanupRunAsync(h.RunId);

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Completed, run.Status);

        // Work item should be transitioned to Done.
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            h.WorkItemId, RemoteWorkItemStatus.Done,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task CleanupRunAsync_transitions_Failed_run_to_Completed()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        h.AddNode("c", NodeType.Cleanup);

        var cleanup = new ScriptedExecutor(NodeType.Cleanup,
            new NodeOutcome.NodeStarting("c"),
            new NodeOutcome.Terminal("cleaned"));
        h.Registry.Register(cleanup);

        h.SeedRun("a", LoopRunStatus.Failed);

        await h.Engine.CleanupRunAsync(h.RunId);

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Completed, run.Status);
    }
}
