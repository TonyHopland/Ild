using FluentAssertions;
using ILD.Core.Services.Interfaces;
using ILD.Data.Enums;

namespace ILD.Tests;

public class LoopEnginePauseSafetyNetTests
{
    [Fact]
    public async Task Cancellation_during_pause_is_responsive()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(("s", NodeType.Start), ("c", NodeType.Cmd));
        h.AddEdge("e1", "s", "c");
        h.Save();

        var startEntered = new TaskCompletionSource();
        var startTcs = new TaskCompletionSource();
        h.Fakes[NodeType.Start].AsyncBehavior = async _ =>
        {
            startEntered.TrySetResult();
            await startTcs.Task;
            return NodeExecutionResult.Ok("ok");
        };

        var task = Task.Run(() => h.Engine.RunAsync(h.RunId));
        await startEntered.Task;
        await h.Engine.PauseRunAsync(h.RunId);
        startTcs.SetResult();

        // Give the run a moment to settle into the pause loop.
        await Task.Delay(100);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await h.Engine.CancelRunAsync(h.RunId);
        var status = await task;
        sw.Stop();

        status.Should().Be(LoopRunStatus.Cancelled);
        sw.ElapsedMilliseconds.Should().BeLessThan(500, "cancellation should not wait for full Task.Delay tick");
    }
}
