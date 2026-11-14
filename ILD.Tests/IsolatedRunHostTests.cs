using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Local;
using ILD.Core.Services.RunHosts.Tools;
using ILD.Tests.Fakes;

namespace ILD.Tests;

/// <summary>
/// What the isolated run host does whichever engine holds its runs, on an engine kept in memory: where a command goes and
/// what a stopped run costs it, the sign-in terminal's pod or container, a run that cannot be made ready, and the one
/// container every chat shares.
/// </summary>
public class IsolatedRunHostTests
{
    private static readonly TimeSpan IdleTimeout = InMemoryRunHost.IdleTimeout;

    private readonly ManualTimeProvider _time = new();

    private InMemoryRunHost Host() => new() { Time = _time };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_runs_command_is_started_in_the_runs_own_environment()
    {
        var host = Host();
        var run = Guid.NewGuid();
        host.OnExec = Prints("from the run");

        var output = await host.RunToolAsync(RunPaths.WorkingDirectory(run), "git", ["status"], ct: Ct);

        Assert.Equal("from the run", output.StdOut);
        Assert.Equal([host.NameOf(run)], host.Execs.Select(e => e.Name));
        Assert.Empty(host.Revived);
    }

    [Fact]
    public async Task A_command_that_meets_a_stopped_run_is_tried_once_more_after_the_run_is_brought_back()
    {
        var host = Host();
        var run = Guid.NewGuid();
        host.RefuseExec = _ => host.Revived.Count == 0 ? new IOException("the container is stopped") : null;
        host.OnExec = Prints("back");
        host.Revives = true;

        var output = await host.RunToolAsync(RunPaths.WorkingDirectory(run), "git", ["status"], ct: Ct);

        Assert.Equal("back", output.StdOut);
        Assert.Equal([run], host.Revived);
        Assert.Equal(2, host.Execs.Count);
    }

    [Fact]
    public async Task A_command_that_fails_to_start_in_a_run_that_did_not_stop_fails_with_what_the_engine_said()
    {
        var host = Host();
        var run = Guid.NewGuid();
        host.RefuseExec = _ => new IOException("no such container");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.RunToolAsync(RunPaths.WorkingDirectory(run), "git", ["status"], ct: Ct));

        Assert.Contains($"could not run a command in the pod of run {run:N}: no such container", failure.Message);
        Assert.Single(host.Execs);
    }

    [Fact]
    public async Task The_chats_container_is_made_for_the_first_chat_command_and_found_by_the_next()
    {
        var host = Host();
        var path = RunPaths.ChatRoot(Guid.NewGuid());

        await host.RunToolAsync(path, "git", ["status"], ct: Ct);
        await host.RunToolAsync(RunPaths.ChatRoot(Guid.NewGuid()), "git", ["status"], ct: Ct);

        Assert.Equal(1, host.ChatsMade);
        Assert.All(host.Execs, exec => Assert.Equal(InMemoryRunHost.ChatName, exec.Name));
    }

    [Fact]
    public async Task Chat_commands_that_start_together_make_the_chats_container_once()
    {
        var host = Host();
        host.MakingChat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = host.RunToolAsync(RunPaths.ChatRoot(Guid.NewGuid()), "git", ["status"], ct: Ct);
        var second = host.RunToolAsync(RunPaths.ChatRoot(Guid.NewGuid()), "git", ["status"], ct: Ct);
        host.MakingChat.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, host.ChatsMade);
    }

    [Fact]
    public async Task The_chats_container_is_removed_once_it_was_idle_for_the_timeout_and_made_again_for_the_next_chat()
    {
        var host = Host();
        var path = RunPaths.ChatRoot(Guid.NewGuid());
        await host.RunToolAsync(path, "git", ["status"], ct: Ct);

        _time.Advance(IdleTimeout - TimeSpan.FromSeconds(1));
        await host.ReapIdleChatAsync(Ct);
        Assert.Equal(0, host.ChatsRemoved);

        _time.Advance(TimeSpan.FromSeconds(2));
        await host.ReapIdleChatAsync(Ct);
        Assert.Equal(1, host.ChatsRemoved);

        await host.RunToolAsync(path, "git", ["status"], ct: Ct);
        Assert.Equal(2, host.ChatsMade);
    }

    [Fact]
    public async Task A_chat_with_work_open_is_not_idle_however_long_ago_it_started()
    {
        var host = Host();
        var path = RunPaths.ChatRoot(Guid.NewGuid());
        await host.RunToolAsync(path, "git", ["status"], ct: Ct);

        using (host.HoldChat(path))
        {
            _time.Advance(IdleTimeout * 2);
            await host.ReapIdleChatAsync(Ct);
            Assert.Equal(0, host.ChatsRemoved);
        }

        await host.ReapIdleChatAsync(Ct);
        Assert.Equal(0, host.ChatsRemoved);
        _time.Advance(IdleTimeout);
        await host.ReapIdleChatAsync(Ct);
        Assert.Equal(1, host.ChatsRemoved);
    }

    [Fact]
    public async Task A_sign_in_terminal_runs_in_an_environment_of_its_own_that_goes_with_it()
    {
        var host = Host();

        var terminal = await host.OpenSignInAsync(agent: null, "/usr/local/bin/claude", ["setup-token"], 80, 24, Ct);

        var name = Assert.Single(host.SignInsStarted);
        Assert.StartsWith(ExecRunHost.SignInPrefix, name);
        Assert.Equal([name], host.Open);
        var exec = Assert.Single(host.Execs);
        Assert.Equal(name, exec.Name);
        Assert.True(exec.Tty);
        Assert.Equal(["claude", "setup-token"], exec.Command.TakeLast(2));

        await terminal.DisposeAsync();

        Assert.Equal([name], host.SignInsRemoved);
        Assert.Empty(host.Open);
    }

    [Fact]
    public async Task A_sign_in_that_does_not_start_leaves_nothing_open()
    {
        var host = Host();
        host.SignInFailure = new InvalidOperationException("the runner image is not on this machine");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.OpenSignInAsync(agent: null, "claude", [], 80, 24, Ct));

        Assert.Equal("the runner image is not on this machine", failure.Message);
        Assert.Equal(host.SignInsStarted, host.SignInsRemoved);
        Assert.Empty(host.Open);
        Assert.Empty(host.Execs);
    }

    [Fact]
    public async Task A_run_that_is_ready_is_not_made_again()
    {
        var host = Host();
        var run = Guid.NewGuid();
        host.Ready.Add(run);

        var environment = await host.CreateAsync(new RunEnvironmentRequest(run, "/base", "main", Guid.NewGuid()), Ct);

        Assert.Equal(RunPaths.WorkingDirectory(run), environment.WorkingDirectory);
        Assert.Empty(host.Made);
        Assert.Empty(host.Execs);
    }

    [Fact]
    public async Task A_run_that_cannot_be_made_ready_to_work_in_loses_what_was_made_for_it()
    {
        var basePath = Directory.CreateTempSubdirectory("ild-isolated-host-");
        try
        {
            var host = Host();
            var run = Guid.NewGuid();
            host.OnExec = Prints("", "read-only file system", exitCode: 1);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => host.CreateAsync(new RunEnvironmentRequest(run, basePath.FullName, "main", Guid.NewGuid()), Ct));

            Assert.Equal("could not make the run's home in its pod: read-only file system", failure.Message);
            Assert.Equal([run], host.Made);
            Assert.Equal([run], host.Undone);
            Assert.Empty(host.Removed);
        }
        finally
        {
            basePath.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Deleting_a_run_removes_its_environment()
    {
        var host = Host();
        var run = Guid.NewGuid();

        await host.DeleteAsync(new RunEnvironment(run, RunPaths.WorkingDirectory(run)), Ct);

        Assert.Equal([run], host.Removed);
        Assert.Empty(host.Execs);
    }

    [Fact]
    public async Task A_preview_is_reached_at_the_runs_address_and_a_chat_has_none()
    {
        var host = Host();
        var run = Guid.NewGuid();

        var target = await host.PreviewTargetAsync(RunPaths.WorkingDirectory(run), 5173, Ct);

        Assert.Equal(new Uri($"http://{InMemoryRunHost.Address}:5173/"), target);
        await Assert.ThrowsAsync<NotSupportedException>(() => host.PreviewTargetAsync(RunPaths.ChatRoot(Guid.NewGuid()), 5173, Ct));
    }

    private static Func<InMemoryExec, Task> Prints(string stdout, string stderr = "", int exitCode = 0) => async exec =>
    {
        await exec.WriteStdoutAsync(stdout);
        await exec.WriteStderrAsync(stderr);
        await exec.ExitAsync(exitCode);
    };
}
