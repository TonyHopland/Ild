using System.Diagnostics;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Local;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>The Cmd node and the setting, driven through an in-memory run host.</summary>
public class RunHostSeamTests
{
    private const string Worktree = "/fake/worktree";

    private static NodeExecutionContext Context(FakeRunHost host, string command, CancellationToken cancel = default, Func<string, Task>? progress = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IWorkItemManager>().Object);
        services.AddSingleton(NoPackageFeeds.Resolver);
        services.AddSingleton<IRunHost>(host);
        return new NodeExecutionContext(
            new LoopRun { Id = Guid.NewGuid(), WorktreePath = Worktree },
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

    [Fact]
    public async Task A_command_runs_in_the_host_and_its_output_is_the_nodes()
    {
        var host = new FakeRunHost().WithDirectory(Worktree);
        host.Script = _ => FakeCommandScript.Prints("hello\nworld\n", "warn\n");
        var progress = new List<string>();

        var outcomes = await RunAsync(Context(host, "echo hi", TestContext.Current.CancellationToken, progress: line => { lock (progress) progress.Add(line); return Task.CompletedTask; }));

        var success = Assert.IsType<NodeOutcome.Success>(outcomes.Last());
        Assert.Contains("hello", success.Output);
        Assert.Contains("world", success.Output);
        Assert.Contains("warn", success.Output);
        var started = Assert.Single(host.Started);
        Assert.Equal(["-c", "echo hi"], started.Start.ArgumentList);
        Assert.Equal(Worktree, started.Start.WorkingDirectory);
        Assert.Contains("hello\n", progress);
    }

    [Fact]
    public async Task A_command_that_fails_fails_the_node_with_its_exit_code()
    {
        var host = new FakeRunHost().WithDirectory(Worktree);
        host.Script = _ => FakeCommandScript.Prints("", "nope\n", code: 3);

        var outcomes = await RunAsync(Context(host, "false", TestContext.Current.CancellationToken));

        var fail = Assert.IsType<NodeOutcome.Fail>(outcomes.Last());
        Assert.Equal("exit code 3", fail.Reason);
    }

    [Fact]
    public async Task A_worktree_the_host_does_not_have_fails_the_node_without_starting_anything()
    {
        var host = new FakeRunHost();

        var outcomes = await RunAsync(Context(host, "true", TestContext.Current.CancellationToken));

        Assert.Equal("No worktree available for Cmd node", Assert.IsType<NodeOutcome.Fail>(outcomes.Last()).Reason);
        Assert.Empty(host.Started);
    }

    [Fact]
    public async Task A_cancelled_command_is_killed_through_the_host()
    {
        var host = new FakeRunHost().WithDirectory(Worktree);
        host.Script = _ => FakeCommandScript.Hangs();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var outcomes = await RunAsync(Context(host, "sleep 1000", cancel.Token));

        var fail = Assert.IsType<NodeOutcome.Fail>(outcomes.Last());
        Assert.DoesNotContain("failed to kill", fail.Reason);
        Assert.True(Assert.Single(host.Started) is { });
    }

    [Fact]
    public async Task KillAll_ends_what_is_still_running_for_the_owner_and_only_that()
    {
        var host = new FakeRunHost();
        host.Script = _ => FakeCommandScript.Hangs();
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        using var first = await host.StartCommandAsync(RunCommand.Routed(new ProcessStartInfo("x"), mine), TestContext.Current.CancellationToken);
        using var second = await host.StartCommandAsync(RunCommand.Routed(new ProcessStartInfo("y"), other), TestContext.Current.CancellationToken);

        Assert.Equal(1, await host.KillAllAsync(mine, TestContext.Current.CancellationToken));

        Assert.True(first.HasExited);
        Assert.False(second.HasExited);
    }

    [Fact]
    public async Task The_local_host_kills_what_a_run_still_has_running()
    {
        var host = new LocalRunHost();
        var owner = Guid.NewGuid();
        var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("sleep 60");
        using var process = await host.StartCommandAsync(RunCommand.Routed(AgentIsolation.Route(psi, AgentIsolation.AgentUser, AgentIsolation.AgentGroup, AgentIsolation.AgentHome), owner), TestContext.Current.CancellationToken);
        Assert.False(process.HasExited);

        Assert.Equal(1, await host.KillAllAsync(owner, TestContext.Current.CancellationToken));

        Assert.True(process.HasExited);
        Assert.Equal(0, await host.KillAllAsync(owner, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null, RunHostKind.Local)]
    [InlineData("", RunHostKind.Local)]
    [InlineData("local", RunHostKind.Local)]
    [InlineData(" Container ", RunHostKind.Container)]
    [InlineData("KUBERNETES", RunHostKind.Kubernetes)]
    public void The_setting_names_a_backend_and_defaults_to_local(string? value, RunHostKind expected)
        => Assert.Equal(expected, RunHostOptions.Parse(value));

    [Fact]
    public void An_unknown_backend_is_refused()
        => Assert.Contains("ILD_RUN_HOST", Assert.Throws<InvalidOperationException>(() => RunHostOptions.Parse("docker")).Message);

    [Theory]
    [InlineData("container")]
    [InlineData("kubernetes")]
    public void A_backend_that_is_not_built_yet_stops_startup(string value)
        => Assert.Contains("not implemented yet", Assert.Throws<InvalidOperationException>(() => RunHostOptions.Select(value)).Message);

    [Fact]
    public void The_local_host_points_the_preview_proxy_at_loopback()
        => Assert.Equal("http://127.0.0.1:5173/", new LocalRunHost().PreviewTarget(5173).ToString());
}

public class LocalRunHostTrackingTests
{
    private static ProcessStartInfo Sleeper()
    {
        var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("sleep 60");
        return AgentIsolation.Route(psi, AgentIsolation.AgentUser, AgentIsolation.AgentGroup, AgentIsolation.AgentHome);
    }

    [Fact]
    public async Task KillAll_leaves_another_owners_command_and_one_that_was_disposed_alone()
    {
        var host = new LocalRunHost();
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        using var theirs = await host.StartCommandAsync(RunCommand.Routed(Sleeper(), other), TestContext.Current.CancellationToken);
        var released = await host.StartCommandAsync(RunCommand.Routed(Sleeper(), mine), TestContext.Current.CancellationToken);
        var releasedId = released.Id;
        released.TryKill();
        await released.WaitForExitAsync(TestContext.Current.CancellationToken);
        released.Dispose();

        Assert.Equal(0, await host.KillAllAsync(mine, TestContext.Current.CancellationToken));
        Assert.False(theirs.HasExited);
        Assert.Equal(1, await host.KillAllAsync(other, TestContext.Current.CancellationToken));
        Assert.NotEqual(0, releasedId);
    }

    [Fact]
    public async Task Deleting_an_environment_ends_its_commands_before_the_worktree_is_removed()
    {
        var owner = Guid.NewGuid();
        var repositories = new Mock<ILD.Core.Services.Interfaces.IRepositoryManager>();
        var host = new LocalRunHost(repositories: repositories.Object);
        using var process = await host.StartCommandAsync(RunCommand.Routed(Sleeper(), owner), TestContext.Current.CancellationToken);
        var exitedWhenRemoved = false;
        repositories.Setup(r => r.DestroyWorktreeAsync("/w")).Callback(() => exitedWhenRemoved = process.HasExited).Returns(Task.CompletedTask);

        await host.DeleteAsync(new RunEnvironment(owner, "/w"), TestContext.Current.CancellationToken);

        Assert.True(exitedWhenRemoved, "the command was still running when the worktree was removed");
    }

    [Fact]
    public async Task A_command_that_was_not_routed_is_refused_when_agent_isolation_is_on()
    {
        Assert.SkipWhen(AgentIsolation.AgentUser is null, "agent uid isolation is off in this process");
        var host = new LocalRunHost();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartCommandAsync(RunCommand.Routed(new ProcessStartInfo("/bin/true")), TestContext.Current.CancellationToken));
    }
}
