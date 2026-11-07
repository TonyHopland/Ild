using ILD.Core.Services.RunHosts;
using ILD.Data.Enums;
using ILD.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

/// <summary>A node never runs again beside a command its earlier attempt left in the run host.</summary>
public class LoopEngineRunHostTests
{
    private static LoopEngineHarness Harness(FakeRunHost host)
        => new(configure: services => services.AddSingleton<IRunHost>(host));

    [Fact]
    public async Task A_halted_run_that_resumes_has_its_leftover_commands_killed_before_the_node_runs_again()
    {
        var host = new FakeRunHost();
        using var h = Harness(host);
        var executor = new ScriptedExecutor(NodeType.AI);
        h.Registry.Register(executor);
        h.AddNode("ai", NodeType.AI);
        h.SeedRun("ai");
        await h.Engine.HaltRunAsync(h.RunId);
        var invocationsAtKill = -1;
        host.OnKillAll = _ => invocationsAtKill = executor.Invocations;

        await h.Engine.ResumeFromHaltAsync(h.RunId, "go on");
        await h.WaitUntilIdleAsync();

        Assert.Equal([h.RunId], host.KillAllCalls);
        Assert.Equal(0, invocationsAtKill);
        Assert.True(executor.Invocations > 0, "the node should have run after the kill");
    }

    [Fact]
    public async Task A_retried_node_has_its_leftover_commands_killed_before_it_runs_again()
    {
        var host = new FakeRunHost();
        using var h = Harness(host);
        var executor = new ScriptedExecutor(NodeType.AI);
        h.Registry.Register(executor);
        h.AddNode("ai", NodeType.AI);
        h.SeedRun("ai");
        await h.Engine.HaltRunAsync(h.RunId);
        var target = new ILD.Data.Entities.LoopRunNode
        {
            Id = Guid.NewGuid(), LoopRunId = h.RunId, LoopNodeId = h.NodesById["ai"].Id, Status = LoopRunNodeStatus.Interrupted,
        };
        h.Db.Context.LoopRunNodes.Add(target);
        h.Db.Context.SaveChanges();
        var invocationsAtKill = -1;
        host.OnKillAll = _ => invocationsAtKill = executor.Invocations;

        await h.Engine.RetryFromNodeAsync(h.RunId, target.Id);
        await h.WaitUntilIdleAsync();

        Assert.Equal([h.RunId], host.KillAllCalls);
        Assert.Equal(0, invocationsAtKill);
        Assert.True(executor.Invocations > 0, "the node should have run after the kill");
    }

    [Fact]
    public async Task A_host_that_cannot_be_reached_does_not_stop_the_resume()
    {
        var host = new FakeRunHost { KillAllFailure = new IOException("the host is gone") };
        using var h = Harness(host);
        var executor = new ScriptedExecutor(NodeType.AI);
        h.Registry.Register(executor);
        h.AddNode("ai", NodeType.AI);
        h.SeedRun("ai");
        await h.Engine.HaltRunAsync(h.RunId);

        await h.Engine.ResumeFromHaltAsync(h.RunId, "go on");
        await h.WaitUntilIdleAsync();

        Assert.False(h.ReloadRun().IsHalted);
        Assert.True(executor.Invocations > 0);
    }
}
