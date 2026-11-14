using System.Diagnostics;
using System.Runtime.Versioning;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Local;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// Finding the processes an agent left running when its turn ended: by the launch id every process of the launch inherits,
/// leaving out the launch itself and the helpers that stay up by design. Uses real processes, on this machine and through
/// the scripts the container and pod hosts run in the run.
/// </summary>
[SupportedOSPlatform("linux")]
public class AgentChildDetectionTests
{
    private static Process Sleeper(string? launchId, string marker = "ild-test-sleeper")
    {
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-c");
        // The marker is in the command line so a test can tell its own processes apart; exec makes the shell the sleeper.
        psi.ArgumentList.Add($"exec sleep 60 # {marker}");
        if (launchId is not null) psi.Environment[AgentChildren.LaunchVariable] = launchId;
        return Process.Start(psi)!;
    }

    /// <summary>Waits until /proc shows the process as the program it exec'd into, which is what a launch's child looks like once it runs.</summary>
    private static async Task WaitUntilExecedAsync(Process process)
    {
        for (var i = 0; i < 200; i++)
        {
            try
            {
                if ((await File.ReadAllTextAsync($"/proc/{process.Id}/cmdline")).StartsWith("sleep", StringComparison.Ordinal)) return;
            }
            catch (IOException) { }
            await Task.Delay(10);
        }
        throw new TimeoutException("the test process never started");
    }

    private static string NewLaunchId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Local_host_finds_the_live_processes_of_the_launch_with_pid_and_command_line()
    {
        var launch = NewLaunchId();
        using var child = Sleeper(launch);
        using var stranger = Sleeper(NewLaunchId());
        using var unmarked = Sleeper(null);
        try
        {
            await WaitUntilExecedAsync(child);
            await WaitUntilExecedAsync(stranger);
            await WaitUntilExecedAsync(unmarked);

            var found = await LocalRunHost.Shared.ListAgentChildrenAsync(Guid.NewGuid(), launch, [], TestContext.Current.CancellationToken);

            var only = Assert.Single(found);
            Assert.Equal(child.Id, only.Pid);
            Assert.Equal("sleep 60", only.CommandLine);
        }
        finally
        {
            child.Kill(); stranger.Kill(); unmarked.Kill();
        }
    }

    [Fact]
    public async Task Local_host_leaves_out_processes_whose_command_line_is_on_the_ignore_list()
    {
        var launch = NewLaunchId();
        using var helper = Sleeper(launch);
        try
        {
            await WaitUntilExecedAsync(helper);

            var found = await LocalRunHost.Shared.ListAgentChildrenAsync(Guid.NewGuid(), launch, ["sleep 60"], TestContext.Current.CancellationToken);

            Assert.Empty(found);
        }
        finally { helper.Kill(); }
    }

    [Fact]
    public async Task Local_host_reports_a_process_as_gone_once_it_ends_and_stops_one_that_runs()
    {
        var launch = NewLaunchId();
        using var child = Sleeper(launch);
        try
        {
            await WaitUntilExecedAsync(child);
            var found = await LocalRunHost.Shared.ListAgentChildrenAsync(Guid.NewGuid(), launch, [], TestContext.Current.CancellationToken);
            Assert.Single(await LocalRunHost.Shared.StillRunningAsync(Guid.NewGuid(), found, TestContext.Current.CancellationToken));

            await LocalRunHost.Shared.StopProcessesAsync(Guid.NewGuid(), found, TestContext.Current.CancellationToken);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.Empty(await LocalRunHost.Shared.StillRunningAsync(Guid.NewGuid(), found, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }

    [Fact]
    public async Task Local_host_does_not_take_a_process_that_reused_the_pid_for_the_one_it_saw()
    {
        // Pid 1 is running, but it is not the process that was seen.
        var gone = new RunChildProcess(1, "dotnet test");
        Assert.Empty(await LocalRunHost.Shared.StillRunningAsync(Guid.NewGuid(), [gone], TestContext.Current.CancellationToken));
    }

    private static async Task<(int ExitCode, string StdOut)> RunScriptAsync(string script, params string[] arguments)
    {
        var psi = new ProcessStartInfo("/usr/bin/env") { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var part in ExecRunHost.IldScript("/bin/bash", script, arguments)) psi.ArgumentList.Add(part);
        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output);
    }

    [Fact]
    public async Task The_script_the_container_and_pod_hosts_run_lists_the_launchs_processes_with_their_output_file()
    {
        var launch = NewLaunchId();
        var outFile = Path.Combine(Path.GetTempPath(), $"ild-child-out-{Guid.NewGuid():N}.txt");
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"exec sleep 60 > '{outFile}'");
        psi.Environment[AgentChildren.LaunchVariable] = launch;
        using var child = Process.Start(psi)!;
        try
        {
            await WaitUntilExecedAsync(child);

            var (exit, output) = await RunScriptAsync(ExecRunHost.ListChildrenScript, launch, "ild-mcp-server");

            Assert.Equal(0, exit);
            var line = Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            Assert.Equal($"{child.Id}\t{outFile}\tsleep 60 ", line);
        }
        finally
        {
            child.Kill();
            File.Delete(outFile);
        }
    }

    [Fact]
    public async Task The_script_leaves_out_ignored_processes_and_those_of_other_launches()
    {
        var launch = NewLaunchId();
        using var ignored = Sleeper(launch);
        using var other = Sleeper(NewLaunchId());
        try
        {
            await WaitUntilExecedAsync(ignored);
            await WaitUntilExecedAsync(other);

            var (_, output) = await RunScriptAsync(ExecRunHost.ListChildrenScript, launch, "sleep 60");

            Assert.Empty(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        finally { ignored.Kill(); other.Kill(); }
    }

    [Fact]
    public async Task The_scripts_tell_a_running_process_from_a_gone_one_and_stop_it()
    {
        var launch = NewLaunchId();
        using var child = Sleeper(launch);
        try
        {
            await WaitUntilExecedAsync(child);
            var (_, listed) = await RunScriptAsync(ExecRunHost.ListChildrenScript, launch);
            var parts = listed.TrimEnd('\n').Split('\t');
            var pid = parts[0];
            var command = parts[2];

            Assert.Equal(pid, (await RunScriptAsync(ExecRunHost.StillRunningScript, pid, command)).StdOut.Trim());
            Assert.Empty((await RunScriptAsync(ExecRunHost.StillRunningScript, pid, "something else")).StdOut.Trim());

            await RunScriptAsync(ExecRunHost.StopProcessesScript, pid, command);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.Empty((await RunScriptAsync(ExecRunHost.StillRunningScript, pid, command)).StdOut.Trim());
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }

    // -- The adapter ---------------------------------------------------------------------

    /// <summary>A stand-in for the CLI: starts a background process (output away from the CLI's pipe), then prints its result and exits.</summary>
    private static string FakeCli(string directory, string background)
    {
        var path = Path.Combine(directory, "fake-claude.sh");
        File.WriteAllText(path,
            "#!/bin/sh\n" +
            background + "\n" +
            "echo '{\"type\":\"assistant\",\"session_id\":\"s1\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Waiting on the run.\"}]}}'\n" +
            "echo '{\"type\":\"result\",\"session_id\":\"s1\",\"is_error\":false,\"result\":\"Waiting on the run.\"}'\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static AgentExecutionContext Context(string binary, string worktree, Guid? chat = null)
        => new(
            new AiProvider { Name = "claude-test", Type = "claude-code", BaseUrl = "", Model = "", Config = $"{{\"binaryPath\":\"{binary}\"}}" },
            "test prompt",
            new LoopRunContext(Guid.NewGuid(), Guid.NewGuid().ToString(), "Task", "Description", worktree, "main", new List<string>(), null),
            ExecutionCount: 1, Cancel: CancellationToken.None, ChatSessionId: chat);

    private static string Worktree()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ild-children-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task A_turn_that_ends_with_a_process_still_running_reports_it_and_leaves_it_alone()
    {
        var worktree = Worktree();
        IReadOnlyList<RunChildProcess> children = [];
        try
        {
            var cli = FakeCli(worktree, "sleep 61 >/dev/null 2>&1 &");

            var result = await new ClaudeCodeAdapter().ExecuteAsync(Context(cli, worktree));

            Assert.True(result.Success);
            children = result.LiveChildren ?? [];
            var child = Assert.Single(children);
            Assert.Equal("sleep 61", child.CommandLine);
            // The agent itself is not one of its own children, and the process is still there for ILD to wait for.
            Assert.Single(await LocalRunHost.Shared.StillRunningAsync(Guid.NewGuid(), children, TestContext.Current.CancellationToken));
        }
        finally
        {
            await LocalRunHost.Shared.StopProcessesAsync(Guid.NewGuid(), children, TestContext.Current.CancellationToken);
            Directory.Delete(worktree, true);
        }
    }

    [Fact]
    public async Task A_turn_that_ends_with_no_process_running_has_no_live_children()
    {
        var worktree = Worktree();
        try
        {
            var result = await new ClaudeCodeAdapter().ExecuteAsync(Context(FakeCli(worktree, ":"), worktree));

            Assert.True(result.Success);
            Assert.Null(result.LiveChildren is { Count: > 0 } ? result.LiveChildren : null);
        }
        finally { Directory.Delete(worktree, true); }
    }

    [Fact]
    public async Task A_process_that_ended_with_the_cli_is_not_waited_for()
    {
        var worktree = Worktree();
        try
        {
            // Alive when the result event is read, gone by the time the CLI has exited: what a CLI that cleans up on exit looks like.
            var cli = FakeCli(worktree, "sleep 61 >/dev/null 2>&1 &\nkid=$!\ntrap 'kill $kid' EXIT");

            var result = await new ClaudeCodeAdapter().ExecuteAsync(Context(cli, worktree));

            Assert.True(result.Success);
            Assert.Empty(result.LiveChildren ?? []);
        }
        finally { Directory.Delete(worktree, true); }
    }

    [Fact]
    public async Task A_chat_turn_never_waits_for_processes()
    {
        var worktree = Worktree();
        try
        {
            var cli = FakeCli(worktree, "sleep 62 >/dev/null 2>&1 &");

            var result = await new ClaudeCodeAdapter().ExecuteAsync(Context(cli, worktree, chat: Guid.NewGuid()));

            Assert.True(result.Success);
            Assert.Empty(result.LiveChildren ?? []);
        }
        finally
        {
            // The chat's process was never looked for, so the test finds and ends it itself.
            var leftovers = Process.GetProcessesByName("sleep").Where(p => TryCommand(p) == "sleep 62").Select(p => new RunChildProcess(p.Id, "sleep 62")).ToList();
            await LocalRunHost.Shared.StopProcessesAsync(Guid.NewGuid(), leftovers, TestContext.Current.CancellationToken);
            Directory.Delete(worktree, true);
        }
    }

    private static string? TryCommand(Process process)
    {
        try { return File.ReadAllText($"/proc/{process.Id}/cmdline").Replace('\0', ' ').TrimEnd(); }
        catch (IOException) { return null; }
    }
}
