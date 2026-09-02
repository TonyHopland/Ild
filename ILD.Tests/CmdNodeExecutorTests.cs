using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

public class CmdNodeExecutorTests : IDisposable
{
    private readonly string _worktree;

    public CmdNodeExecutorTests()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "ild-cmd-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_worktree);
    }

    public void Dispose()
    {
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private NodeExecutionContext Context(
        string command, CancellationToken cancel = default, Func<string, Task>? progress = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IWorkItemManager>().Object);
        return new NodeExecutionContext(
            new LoopRun { Id = Guid.NewGuid(), WorktreePath = _worktree },
            new LoopNode
            {
                Id = Guid.NewGuid(),
                NodeType = NodeType.Cmd,
                Label = "cmd",
                Config = System.Text.Json.JsonSerializer.Serialize(new { command }),
            },
            services.BuildServiceProvider(),
            cancel,
            progress);
    }

    private static async Task<List<NodeOutcome>> RunAsync(NodeExecutionContext ctx)
    {
        var outcomes = new List<NodeOutcome>();
        await foreach (var outcome in new CmdNodeExecutor().ExecuteAsync(ctx)) outcomes.Add(outcome);
        return outcomes;
    }

    [Theory]
    // The command reaches /bin/sh as one argv entry, so the shell — not .NET's
    // argument parser, and not a hand-rolled escape — is what interprets it.
    [InlineData("echo 'it'\\''s \"quoted\"'", "it's \"quoted\"")]
    [InlineData("printf '%s\\n' 'C:\\path\\to\\thing'", "C:\\path\\to\\thing")]
    [InlineData("echo \\\\", "\\")]
    [InlineData("echo \"a  b\"", "a  b")]
    // A trailing backslash and a backslash before a closing quote are what the
    // old hand-rolled escaping mangled: it doubled quotes into the argument
    // string and .NET's parser then ate the backslash guarding them.
    [InlineData("echo \"x\\\\\"", "x\\")]
    [InlineData("echo hi \\", "hi \\")]
    public async Task A_command_is_interpreted_by_the_shell_verbatim(string command, string expected)
    {
        var outcomes = await RunAsync(Context(command));

        var success = Assert.IsType<NodeOutcome.Success>(Assert.Single(outcomes, o => o is NodeOutcome.Success));
        Assert.Equal(expected, success.Output?.TrimEnd('\n'));
    }

    [Fact]
    public async Task A_failing_command_fails_the_node_with_its_output()
    {
        var outcomes = await RunAsync(Context("echo nope; echo bad >&2; exit 3"));

        var fail = Assert.IsType<NodeOutcome.Fail>(Assert.Single(outcomes, o => o is NodeOutcome.Fail));
        Assert.Equal("exit code 3", fail.Reason);
        Assert.Contains("nope", fail.Output);
        Assert.Contains("bad", fail.Output);
    }

    [Fact]
    public async Task Cancelling_reaps_the_whole_process_tree()
    {
        // The node's own /bin/sh is not the risk — a background grandchild is: it
        // outlives a kill that only reaches the direct child, and keeps writing the
        // worktree the engine is about to commit and delete. This one announces
        // itself, then writes a marker two seconds later; the marker is the orphan.
        var survivor = Path.Combine(_worktree, "survivor.txt");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();

        var ctx = Context(
            $"(sleep 2; echo alive > '{survivor}') & echo started; sleep 30",
            cancel.Token,
            line =>
            {
                if (line.Contains("started", StringComparison.Ordinal)) started.TrySetResult();
                return Task.CompletedTask;
            });

        var run = RunAsync(ctx);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cancel.Cancel();

        var outcomes = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsType<NodeOutcome.Fail>(Assert.Single(outcomes, o => o is NodeOutcome.Fail));

        // Longer than the marker's delay, so "absent" means killed rather than
        // "not written yet".
        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(survivor), "a background child of the cancelled command survived the node");
    }
}
