using ILD.Core.Services.Remote;
using ILD.Data.Enums;
using Moq;

namespace ILD.Tests;

/// <summary>
/// <c>StopRunAsync</c> is the single definition of "a run has ended" — the
/// counterpart to <c>LoopRunStore.IsAlive</c>'s definition of alive, and what a
/// work item's concurrency slot is released against. It ends the run and
/// nothing else; deciding what the work item should say next belongs to
/// whoever asked, because they all want something different — Done for a human
/// finishing it, HumanFeedback for the cancel button, and nothing at all for a
/// poll pass reacting to a status the server already has.
/// </summary>
public class LoopEngineStopRunTests
{
    [Fact]
    public async Task Stopping_a_run_ends_it_and_leaves_the_work_item_alone()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        var run = h.SeedRun("a");

        await h.Engine.StopRunAsync(run.Id, "Work item marked Done");

        var after = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Cancelled, after.Status);
        Assert.NotNull(after.CompletedAt);
        Assert.Equal("Work item marked Done", after.HumanFeedbackReason);

        // The whole point: no disposition of its own. A caller on its way to
        // Done would otherwise have to overwrite one, and the overwritten state
        // still leaves a conversation entry and a notification behind.
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            It.IsAny<string>(), It.IsAny<RemoteWorkItemStatus>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Cancelling_a_run_ends_it_and_parks_the_work_item_for_a_human()
    {
        // The cancel button's meaning, and the only caller that wants the park.
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        var run = h.SeedRun("a");

        await h.Engine.CancelRunAsync(run.Id);

        Assert.Equal(LoopRunStatus.Cancelled, h.ReloadRun().Status);
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            h.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Stopping_an_unknown_run_is_a_no_op()
    {
        // Two passes can both see the same finished item before either writes.
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        h.SeedRun("a");

        await h.Engine.StopRunAsync(Guid.NewGuid(), "gone");

        Assert.Equal(LoopRunStatus.Running, h.ReloadRun().Status);
    }
}
