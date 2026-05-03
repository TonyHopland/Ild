using FluentAssertions;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Tests;

public class CmdNodeExecutorTests
{
    private static NodeExecutionContext MakeContext(string command, int timeoutSeconds = 300)
    {
        var node = new LoopNode
        {
            Id = Guid.NewGuid(),
            Label = "n",
            NodeType = NodeType.Cmd,
            Config = $"{{\"command\":\"{command}\"}}",
            TimeoutSeconds = timeoutSeconds,
        };
        var wi = new WorkItem
        {
            Id = Guid.NewGuid(),
            Title = "t",
            Description = "d",
            WorktreePath = Path.GetTempPath(),
        };
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = wi.Id };
        var rn = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
        return new NodeExecutionContext(run, rn, node, wi, null, CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_succeeds_for_normal_echo_command()
    {
        var exec = new CmdNodeExecutor();
        var ctx = MakeContext("echo hello");

        var result = await exec.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("hello");
    }

    [Fact]
    public async Task ExecuteAsync_times_out_long_running_command()
    {
        var exec = new CmdNodeExecutor();
        var ctx = MakeContext("sleep 5", timeoutSeconds: 1);

        var result = await exec.ExecuteAsync(ctx);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("timed out");
    }

    [Fact]
    public async Task ExecuteAsync_returns_failure_with_nonzero_exit_code()
    {
        var exec = new CmdNodeExecutor();
        var ctx = MakeContext("false");

        var result = await exec.ExecuteAsync(ctx);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("exit=1");
    }
}
