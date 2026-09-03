using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Moq;

namespace ILD.Tests;

/// <summary>
/// What the engine does with <see cref="NodeOutcome.Interrupted"/>: park the run
/// exactly where a halt parks it (ADR-0017) rather than route it onto the
/// <c>on_failure</c> edge, so the existing Resume path picks it up when the
/// human decides the provider's limit has reset.
/// </summary>
public class LoopEngineThrottleParkTests
{
    private const string ParkReason =
        "Provider throttled this AI node — Resume will continue the same agent session where it left off.";
    private const string ProviderNotice = "You've hit your session limit · resets 9:40am (UTC)";

    private static LoopEngineHarness Harness(params NodeOutcome[] script)
    {
        var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI, "Coder");
        h.AddNode("recover", NodeType.Cmd);
        h.AddEdge("ai", "recover", EdgeType.OnFailure);
        h.Registry.Register(new ScriptedExecutor(NodeType.AI, script));
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd, new NodeOutcome.NodeStarting(), new NodeOutcome.Terminal("recovered")));
        h.SeedRun("ai");
        return h;
    }

    [Fact]
    public async Task Interrupted_parks_the_run_as_a_resumable_throttle_halt()
    {
        using var h = Harness(
            new NodeOutcome.NodeStarting("do the work"),
            new NodeOutcome.Interrupted(ParkReason, ProviderNotice));

        await h.RunAsync();

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.True(run.IsHalted);
        Assert.Equal(HaltReason.Throttled, run.HaltReason);
        // The frontend keys its badge off this exact string.
        Assert.Equal(HumanFeedbackReasons.AiProviderThrottled, run.HumanFeedbackReason);
        // Kept so Resume re-enters the same node.
        Assert.Equal(h.NodesById["ai"].Id, run.CurrentNodeId);
    }

    [Fact]
    public async Task Interrupted_records_the_node_and_keeps_the_providers_raw_output()
    {
        using var h = Harness(
            new NodeOutcome.NodeStarting("do the work"),
            new NodeOutcome.Interrupted(ParkReason, ProviderNotice));

        await h.RunAsync();

        var node = Assert.Single(h.ReloadRunNodes());
        Assert.Equal(LoopRunNodeStatus.Interrupted, node.Status);
        // No timestamp parsing, no scheduling: the human reads "resets 9:40am"
        // off the adapter's own words and decides when to click Resume.
        Assert.Equal(ProviderNotice, node.Output);
        Assert.Equal(ParkReason, node.Error);
    }

    [Fact]
    public async Task Interrupted_does_not_follow_the_on_failure_edge()
    {
        using var h = Harness(
            new NodeOutcome.NodeStarting("do the work"),
            new NodeOutcome.Interrupted(ParkReason, ProviderNotice));

        await h.RunAsync();

        // The recovery node wired on on_failure must not have run: a throttle is
        // not the node failing, and routing it would spend the loop's error
        // handling on a provider that simply said "not now".
        Assert.Single(h.ReloadRunNodes());
        Assert.Equal(h.NodesById["ai"].Id, h.ReloadRun().CurrentNodeId);
    }

    [Fact]
    public async Task Interrupted_parks_the_work_item_for_a_human_not_for_the_scheduler()
    {
        using var h = Harness(
            new NodeOutcome.NodeStarting("do the work"),
            new NodeOutcome.Interrupted(ParkReason, ProviderNotice));

        await h.RunAsync();

        // HumanFeedback, not WaitingForIld: the scheduler auto-resumes what it
        // finds waiting on ILD, and this park is waiting on a person.
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            h.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
            ParkReason, It.IsAny<string?>(), It.IsAny<Guid?>(),
            HumanFeedbackReasons.AiProviderThrottled, "Coder"), Times.Once);
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            It.IsAny<string>(), RemoteWorkItemStatus.WaitingForIld,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(),
            It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task A_throttle_park_is_not_recoverable_on_startup()
    {
        // The landmine: HaltReason.Shutdown means "ILD's own bookmark, resume
        // it"; a throttle park must read like a human halt instead, or the
        // rejected blanket auto-resume comes back through the startup door.
        using var h = Harness(
            new NodeOutcome.NodeStarting("do the work"),
            new NodeOutcome.Interrupted(ParkReason, ProviderNotice));

        await h.RunAsync();

        var run = h.ReloadRun();
        Assert.False(run.IsShutdownHalted);
        Assert.False(run.IsRecoverable);
    }

    [Fact]
    public async Task Resume_clears_the_throttle_park_and_re_enters_the_node()
    {
        using var h = Harness(
            new NodeOutcome.NodeStarting("do the work"),
            new NodeOutcome.Interrupted(ParkReason, ProviderNotice));
        await h.RunAsync();

        // The unchanged resume path: it gates on WaitingHuman + IsHalted, which
        // is exactly the shape a throttle park writes.
        await h.Engine.ResumeFromHaltAsync(h.RunId, "");
        await h.WaitUntilIdleAsync();

        var run = h.ReloadRun();
        Assert.False(run.IsHalted);
        Assert.Null(run.HaltReason);
        // A non-null note (empty allowed) is what makes the AI node continue the
        // captured session rather than start a fresh one.
        Assert.Equal(string.Empty, run.SteeringNote);
    }

    [Fact]
    public async Task The_park_records_when_the_provider_said_the_limit_lifts()
    {
        var resetAt = DateTime.UtcNow.AddHours(1);
        using var h = Harness(
            new NodeOutcome.NodeStarting("do the work"),
            new NodeOutcome.Interrupted(ParkReason, ProviderNotice, resetAt));

        await h.RunAsync();

        // Carried from the notice the adapter read, so the retry has a deadline
        // to respect without re-reading the provider's prose.
        Assert.Equal(resetAt, h.ReloadRun().ThrottleResetAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_park_whose_notice_stated_no_time_overwrites_an_older_one()
    {
        var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI, "Coder");
        h.Registry.Register(new ScriptedExecutor(NodeType.AI,
                new NodeOutcome.NodeStarting("do the work"),
                new NodeOutcome.Interrupted(ParkReason, ProviderNotice, DateTime.UtcNow.AddHours(1)))
            .Then(new NodeOutcome.NodeStarting("do the work"),
                new NodeOutcome.Interrupted(ParkReason, ProviderNotice)));
        h.SeedRun("ai");
        using var harness = h;

        await harness.RunAsync();
        Assert.NotNull(harness.ReloadRun().ThrottleResetAt);

        // The second notice named no time. A deadline left over from the first
        // would hold this interruption's retry against a limit that has already
        // lifted, so the park states its own or states none.
        await harness.Engine.ResumeFromHaltAsync(harness.RunId, "");
        await harness.WaitUntilIdleAsync();

        var run = harness.ReloadRun();
        Assert.Equal(HaltReason.Throttled, run.HaltReason);
        Assert.Null(run.ThrottleResetAt);
    }

    [Fact]
    public async Task An_automatic_resume_buys_no_fresh_traversal_budget_and_counts_itself()
    {
        using var h = ThrottlesEveryTime();
        await h.RunAsync();
        var parked = h.ReloadRun();
        Assert.Equal(1, parked.AiTraversalCount);
        Assert.Equal(0, parked.ThrottleAutoResumeCount);

        await h.Engine.ResumeFromHaltAsync(h.RunId, "auto", automatic: true);
        await h.WaitUntilIdleAsync();

        var run = h.ReloadRun();
        // The landmine ADR-0018 guards: a resume nobody asked for must not
        // refill the budget that measures a run left to its own devices, or an
        // unattended loop would buy a fresh 25 steps per provider hiccup.
        Assert.Equal(2, run.AiTraversalCount);
        Assert.Equal(1, run.ThrottleAutoResumeCount);
        // Still throttled, so the run is back where it started — which is what
        // makes the re-park the next attempt rather than a new situation.
        Assert.Equal(HaltReason.Throttled, run.HaltReason);
    }

    [Fact]
    public async Task Getting_past_the_interruption_does_not_refund_the_automatic_resume()
    {
        // The counter deliberately measures the unattended stretch, not the one
        // interruption the retries were spent on: a run that auto-resumes, works
        // on and is stopped again is further into its budget, not back at the
        // start of a fresh one. Only a human refills it.
        var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI, "Coder");
        h.AddNode("next", NodeType.Cmd);
        h.AddEdge("ai", "next", EdgeType.OnSuccess);
        h.Registry.Register(new ScriptedExecutor(NodeType.AI,
                new NodeOutcome.NodeStarting("do the work"),
                new NodeOutcome.Interrupted(ParkReason, ProviderNotice))
            .Then(new NodeOutcome.NodeStarting("do the work"), new NodeOutcome.Terminal("done")));
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting(), new NodeOutcome.Terminal("carried on")));
        h.SeedRun("ai");
        using var harness = h;

        await harness.RunAsync();
        await harness.Engine.ResumeFromHaltAsync(harness.RunId, "auto", automatic: true);
        await harness.WaitUntilIdleAsync();

        // The AI node completed this time and the run moved on.
        Assert.Equal(1, harness.ReloadRun().ThrottleAutoResumeCount);
    }

    [Fact]
    public async Task A_human_resume_clears_the_streak_of_automatic_ones()
    {
        using var h = ThrottlesEveryTime();
        await h.RunAsync();
        await h.Engine.ResumeFromHaltAsync(h.RunId, "auto", automatic: true);
        await h.WaitUntilIdleAsync();
        Assert.Equal(1, h.ReloadRun().ThrottleAutoResumeCount);

        await h.Engine.ResumeFromHaltAsync(h.RunId, "");
        await h.WaitUntilIdleAsync();

        var run = h.ReloadRun();
        // A person is at the run again: both counters that measure its absence
        // start over, so a steered run gets the full retry budget back too.
        Assert.Equal(0, run.ThrottleAutoResumeCount);
        Assert.Equal(1, run.AiTraversalCount);
    }

    private static LoopEngineHarness ThrottlesEveryTime()
    {
        var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI, "Coder");
        h.Registry.Register(new ScriptedExecutor(NodeType.AI,
                new NodeOutcome.NodeStarting("do the work"),
                new NodeOutcome.Interrupted(ParkReason, ProviderNotice))
            .Then(new NodeOutcome.NodeStarting("do the work"), new NodeOutcome.Interrupted(ParkReason, ProviderNotice))
            .Then(new NodeOutcome.NodeStarting("do the work"), new NodeOutcome.Interrupted(ParkReason, ProviderNotice)));
        h.SeedRun("ai");
        return h;
    }
}
