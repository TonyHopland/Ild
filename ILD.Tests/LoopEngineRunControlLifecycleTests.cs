using ILD.Data.Enums;

namespace ILD.Tests;

public class LoopEngineRunControlLifecycleTests
{
    [Fact]
    public async Task Active_run_ids_does_not_include_runs_that_have_completed()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(("s", NodeType.Start), ("a", NodeType.Cmd), ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "a");
        h.AddEdge("e2", "a", "c");
        h.Save();

        await h.Engine.RunAsync(h.RunId);

        var active = await h.Engine.GetActiveRunIdsAsync();
        Assert.DoesNotContain(h.RunId, active);
    }

    [Fact]
    public async Task Active_run_ids_does_not_include_runs_that_have_failed()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(("s", NodeType.Start), ("a", NodeType.Cmd), ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "a");
        h.AddEdge("e2", "a", "c");
        h.Save();

        h.Fakes[NodeType.Cmd].Behavior = _ => ILD.Core.Services.Interfaces.NodeExecutionResult.Fail("boom");

        await h.Engine.RunAsync(h.RunId);

        var active = await h.Engine.GetActiveRunIdsAsync();
        Assert.DoesNotContain(h.RunId, active);
    }

    [Fact]
    public async Task Active_run_ids_does_not_include_runs_that_were_cancelled()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(("s", NodeType.Start), ("a", NodeType.Cmd), ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "a");
        h.AddEdge("e2", "a", "c");
        h.Save();

        // Cmd that cancels the run mid-flight
        h.Fakes[NodeType.Cmd].Behavior = _ =>
        {
            h.Engine.CancelRunAsync(h.RunId).Wait();
            throw new OperationCanceledException();
        };

        await h.Engine.RunAsync(h.RunId);

        var active = await h.Engine.GetActiveRunIdsAsync();
        Assert.DoesNotContain(h.RunId, active);
    }
}
