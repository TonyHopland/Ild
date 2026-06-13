using ILD.Data.Enums;

namespace ILD.Tests;

/// <summary>
/// State-machine coverage for the halt-and-steer control plane:
/// <see cref="ILD.Core.Services.Interfaces.ILoopEngine.HaltRunAsync"/> and
/// <see cref="ILD.Core.Services.Interfaces.ILoopEngine.ResumeFromHaltAsync"/>.
/// </summary>
public class LoopEngineHaltTests
{
    // Drains the fire-and-forget background loop a resume kicks off so the
    // harness can be disposed without racing an in-flight run.
    private static async Task WaitForIdleAsync(LoopEngineHarness h)
    {
        for (var i = 0; i < 200; i++)
        {
            await Task.Delay(10);
            var active = await h.Engine.GetActiveRunIdsAsync();
            if (i > 3 && !active.Any()) return;
        }
    }

    [Fact]
    public async Task Halt_running_ai_node_parks_run_waiting_human_and_keeps_current_node()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI);
        var seeded = h.SeedRun("ai");

        await h.Engine.HaltRunAsync(h.RunId);

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.True(run.IsHalted);
        // CurrentNodeId is kept so the resume can re-run the same AI node.
        Assert.Equal(seeded.CurrentNodeId, run.CurrentNodeId);
    }

    [Fact]
    public async Task Halt_is_noop_when_current_node_is_not_ai()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("cmd", NodeType.Cmd);
        h.SeedRun("cmd");

        await h.Engine.HaltRunAsync(h.RunId);

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Running, run.Status);
        Assert.False(run.IsHalted);
    }

    [Fact]
    public async Task Halt_is_noop_when_run_is_not_running()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI);
        h.SeedRun("ai", LoopRunStatus.WaitingHuman);

        await h.Engine.HaltRunAsync(h.RunId);

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.False(run.IsHalted);
    }

    [Fact]
    public async Task ResumeFromHalt_sets_steering_note_clears_halt_and_runs()
    {
        using var h = new LoopEngineHarness();
        // A no-op AI executor lets the resume's background loop re-enter the
        // node and stop cleanly instead of crashing on a missing executor.
        h.Registry.Register(new ScriptedExecutor(NodeType.AI));
        h.AddNode("ai", NodeType.AI);
        h.SeedRun("ai");
        await h.Engine.HaltRunAsync(h.RunId);

        await h.Engine.ResumeFromHaltAsync(h.RunId, "tighten the tests");
        await WaitForIdleAsync(h);

        var run = h.ReloadRun();
        Assert.Equal("tighten the tests", run.SteeringNote);
        Assert.False(run.IsHalted);
        Assert.Equal(LoopRunStatus.Running, run.Status);
    }

    [Fact]
    public async Task ResumeFromHalt_is_noop_when_run_is_not_halted()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI);
        // Parked at a Human/PR node (WaitingHuman) but NOT halted — must be
        // refused so a steer can't hijack a genuine human-feedback park.
        h.SeedRun("ai", LoopRunStatus.WaitingHuman);

        await h.Engine.ResumeFromHaltAsync(h.RunId, "should be ignored");

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.Null(run.SteeringNote);
    }
}
