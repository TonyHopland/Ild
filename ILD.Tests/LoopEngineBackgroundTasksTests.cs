using Microsoft.Extensions.DependencyInjection;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Moq;

namespace ILD.Tests;

/// <summary>What the engine does with the background work an agent CLI says it is waiting for: show it on the running node, and nothing else.</summary>
public class LoopEngineBackgroundTasksTests
{
    private static readonly RunBackgroundTask Server = new("local_bash", "Run the dev server");

    /// <summary>An AI node that reports its tasks, then keeps running until the run is cancelled.</summary>
    private sealed class WaitingExecutor : INodeExecutor
    {
        private readonly TaskCompletionSource _reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NodeType NodeType => NodeType.AI;
        public Task Reported => _reported.Task;

        public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
        {
            yield return new NodeOutcome.NodeStarting("work");
            await ctx.BackgroundTasksCallback!([Server]);
            _reported.TrySetResult();
            await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
        }
    }

    private static (LoopEngineHarness H, List<IReadOnlyList<RunBackgroundTask>> Seen, RunProgressBuffer Buffer) Harness(WaitingExecutor executor)
    {
        var seen = new List<IReadOnlyList<RunBackgroundTask>>();
        var notifier = new Mock<IRunNotifier>();
        notifier.Setup(n => n.BackgroundTasksChangedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<RunBackgroundTask>>()))
            .Callback((Guid _, Guid _, IReadOnlyList<RunBackgroundTask> tasks) => seen.Add(tasks)).Returns(Task.CompletedTask);
        var buffer = new RunProgressBuffer();
        var h = new LoopEngineHarness(notifier.Object, configure: s => s.AddSingleton<IRunProgressBuffer>(buffer));
        h.AddNode("ai", NodeType.AI);
        h.Registry.Register(executor);
        h.SeedRun("ai");
        return (h, seen, buffer);
    }

    [Fact]
    public async Task The_tasks_are_pushed_and_replayed_while_the_node_runs_and_the_run_stays_running()
    {
        var executor = new WaitingExecutor();
        var (h, seen, _) = Harness(executor);
        using var _h = h;

        await h.LaunchAsync();
        await executor.Reported;

        Assert.Equal(Server, Assert.Single(Assert.Single(seen)));
        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Running, run.Status);
        Assert.False(run.IsHalted);
        Assert.Null(run.HaltReason);
    }

    [Fact]
    public async Task A_person_can_halt_a_node_that_is_waiting_for_a_task_and_steer_it_on_and_the_list_clears()
    {
        var executor = new WaitingExecutor();
        var (h, seen, _) = Harness(executor);
        using var _h = h;
        await h.LaunchAsync();
        await executor.Reported;

        await h.Engine.HaltRunAsync(h.RunId);
        await h.WaitUntilIdleAsync();

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.True(run.IsHalted);
        Assert.Equal(2, seen.Count);
        Assert.Empty(seen[1]);

        h.Registry.Register(new ScriptedExecutor(NodeType.AI));
        await h.Engine.ResumeFromHaltAsync(h.RunId, "carry on without the server");
        await h.WaitUntilIdleAsync();

        run = h.ReloadRun();
        Assert.False(run.IsHalted);
        Assert.Equal("carry on without the server", run.SteeringNote);
    }

    [Fact]
    public void The_replay_for_a_client_that_reloads_carries_the_tasks_until_they_are_cleared()
    {
        var buffer = new RunProgressBuffer();
        var run = Guid.NewGuid();
        Assert.Null(buffer.Snapshot(run).BackgroundTasks);

        buffer.SetBackgroundTasks(run, [Server]);
        Assert.Equal([Server], buffer.Snapshot(run).BackgroundTasks);

        buffer.SetBackgroundTasks(run, []);
        Assert.Null(buffer.Snapshot(run).BackgroundTasks);
    }
}
