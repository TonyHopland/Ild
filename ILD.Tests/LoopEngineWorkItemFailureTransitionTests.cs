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

        Assert.Equal(LoopRunStatus.Failed, status);
        var wi = h.ReloadServerWorkItem();
        Assert.Equal(WorkItemStatus.HumanFeedback, ((WorkItemStatus)(int)wi.Status));
        Assert.False(string.IsNullOrEmpty(h.ReloadRun().HumanFeedbackReason));
    }
}
