using FluentAssertions;
using ILD.Data.Enums;

namespace ILD.Tests;

public class LoopEngineWorkItemFailureTransitionTests
{
    [Fact]
    public async Task RunAsync_with_no_Start_node_transitions_WorkItem_to_HumanFeedback()
    {
        using var h = new EngineHarness();
        // Build a graph that has only a Cmd node (no Start).
        h.BuildSimpleGraph(("c", NodeType.Cmd));

        var status = await h.Engine.RunAsync(h.RunId);

        status.Should().Be(LoopRunStatus.Failed);
        var wi = h.ReloadServerWorkItem();
        ((WorkItemStatus)(int)wi.Status).Should().Be(WorkItemStatus.HumanFeedback);
        h.ReloadRun().HumanFeedbackReason.Should().NotBeNullOrEmpty();
    }
}
