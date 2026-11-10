using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Local;
using System.Diagnostics;
using ILD.Core.Services.Implementations;
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
        string command, CancellationToken cancel = default, Func<string, Task>? progress = null, ILD.Core.Services.RunHosts.IRunHost? host = null)
    {
        var services = new ServiceCollection();
        // Real-shell tests leave the host unset and get the local one.
        if (host is not null) services.AddSingleton(host);
        services.AddSingleton(new Mock<IWorkItemManager>().Object);
        services.AddSingleton(NoPackageFeeds.Resolver);
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
    public async Task A_command_is_interpreted_by_the_shell_verbatim(string command, string expected)
    {
        var outcomes = await RunAsync(Context(command, TestContext.Current.CancellationToken));

        var success = Assert.IsType<NodeOutcome.Success>(Assert.Single(outcomes, o => o is NodeOutcome.Success));
        Assert.Equal(expected, success.Output?.TrimEnd('\n'));
    }

    [Fact]
    public async Task A_failing_command_fails_the_node_with_its_output()
    {
        var host = new Fakes.FakeRunHost().WithDirectory(_worktree);
        host.Script = _ => Fakes.FakeCommandScript.Prints("nope\n", "bad\n", code: 3);

        var outcomes = await RunAsync(Context("echo nope; echo bad >&2; exit 3", TestContext.Current.CancellationToken, host: host));

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
        // worktree the engine is about to commit and delete. This one records its
        // pid before the node announces itself, then would write a marker once its
        // sleep ends; the marker is the orphan.
        var survivor = Path.Combine(_worktree, "survivor.txt");
        var pidFile = Path.Combine(_worktree, "gc.pid");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();

        var ctx = Context(
            $"sh -c 'echo $$ > \"{pidFile}\"; sleep 30; echo alive > \"{survivor}\"' & " +
            $"while [ ! -s '{pidFile}' ]; do :; done; echo started; sleep 30",
            cancel.Token,
            line =>
            {
                if (line.Contains("started", StringComparison.Ordinal)) started.TrySetResult();
                return Task.CompletedTask;
            });

        var run = RunAsync(ctx);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var grandchild = int.Parse(File.ReadAllText(pidFile).Trim());
        cancel.Cancel();

        var outcomes = await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var fail = Assert.IsType<NodeOutcome.Fail>(Assert.Single(outcomes, o => o is NodeOutcome.Fail));

        // The node does not report finished until the shell has actually exited,
        // so the caller can delete the worktree the moment it sees this outcome.
        Assert.DoesNotContain("still alive", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("failed to kill", fail.Reason, StringComparison.Ordinal);

        Assert.True(await HasExitedAsync(grandchild), "a background child of the cancelled command survived the node");
        Assert.False(File.Exists(survivor), "a background child of the cancelled command survived the node");
    }

    // A Cmd command runs in a worktree the agent just wrote, so in practice it
    // runs agent-authored code and gets what an agent launch gets (ADR-0016).
    private const string TrickyCommand = "echo \"a  b\" 'it'\\''s' $HOME \\";

    [Fact]
    public void The_command_is_a_plain_shell_in_the_worktree_and_carries_no_environment_of_its_own()
    {
        var psi = CmdNodeExecutor.ShellStartInfo(TrickyCommand, _worktree);
        psi.Environment["ILD_ONLY_ON_THE_START_INFO"] = "x";

        Assert.Equal("/bin/sh", psi.FileName);
        Assert.Equal(new[] { "-c", TrickyCommand }, psi.ArgumentList);
        Assert.Equal(_worktree, psi.WorkingDirectory);

        // What reaches the command is the environment its caller names (the package feeds), merged by the host.
        var launched = LocalRunHost.Launch(RunCommand.Prepared(psi, environment: new Dictionary<string, string?> { ["FEED"] = "f" }), EgressProxyTests.Creds);
        Assert.Equal("f", launched.Environment["FEED"]);
        Assert.False(launched.Environment.ContainsKey("ILD_ONLY_ON_THE_START_INFO"));
    }

    // The grandchild is not our child, so there is no exit to await: poll /proc
    // until it is gone or a zombie. The deadline is only the failure path.
    private static async Task<bool> HasExitedAsync(int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            string stat;
            try { stat = File.ReadAllText($"/proc/{pid}/stat"); }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }

            // The state follows the parenthesised command name, which may itself
            // contain spaces or parentheses.
            if (stat[(stat.LastIndexOf(')') + 2)..].StartsWith('Z')) return true;
            await Task.Delay(20);
        }

        return false;
    }
}
