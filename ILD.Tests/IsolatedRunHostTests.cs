using System.Text;
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
    public async Task A_chat_with_a_command_running_is_not_idle_and_the_time_counts_from_when_it_ends()
    {
        var host = Host();
        var path = RunPaths.ChatRoot(Guid.NewGuid());
        var release = new TaskCompletionSource();
        host.OnExec = async e =>
        {
            await release.Task;
            await e.ExitAsync(0);
        };
        var psi = new System.Diagnostics.ProcessStartInfo("npm") { WorkingDirectory = path };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("dev");

        using var process = await host.StartCommandAsync(RunCommand.Prepared(psi, Guid.NewGuid()), Ct);
        _time.Advance(IdleTimeout * 2);
        await host.ReapIdleChatAsync(Ct);
        Assert.Equal(0, host.ChatsRemoved);

        release.SetResult();
        await process.WaitForExitAsync(Ct);
        _time.Advance(IdleTimeout - TimeSpan.FromSeconds(1));
        await host.ReapIdleChatAsync(Ct);
        Assert.Equal(0, host.ChatsRemoved);

        _time.Advance(TimeSpan.FromSeconds(1));
        await host.ReapIdleChatAsync(Ct);
        Assert.Equal(1, host.ChatsRemoved);
    }


    [Fact]
    public async Task A_chat_command_runs_in_the_chats_environment_as_that_chat_with_its_home()
    {
        var host = Host();
        var chat = Guid.NewGuid();
        var folder = $"{RunPaths.ChatRoot(chat)}/repos/widgets";
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = folder };
        psi.ArgumentList.Add("status");

        using var process = await host.StartCommandAsync(RunCommand.Prepared(psi, chat, new Dictionary<string, string?> { ["NPM_CONFIG_PREFIX"] = "/caller/prefix" }), Ct);
        await process.WaitForExitAsync(Ct);

        var exec = host.Execs.Last();
        var home = RunPaths.ChatHome(chat);
        Assert.Equal(InMemoryRunHost.ChatName, exec.Name);
        Assert.Equal($"HOME={home}", exec.Env.Single(c => c.StartsWith("HOME=", StringComparison.Ordinal)));
        // Whatever the caller sets, global installs go to the chat's own prefix.
        Assert.Equal($"NPM_CONFIG_PREFIX={home}/.local", exec.Env.Single(c => c.StartsWith("NPM_CONFIG_PREFIX=", StringComparison.Ordinal)));
        Assert.Contains(folder, exec.Command);
        Assert.Equal(["git", "status"], exec.Command.TakeLast(2));
        Assert.Contains($"{chat:N}/", exec.Command.Single(c => c.StartsWith(chat.ToString("N") + "/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_command_in_a_chat_folder_is_the_chats_even_when_its_caller_names_another_id_to_kill_it_by()
    {
        var host = Host();
        var chat = Guid.NewGuid();
        var title = Guid.NewGuid();
        var psi = new System.Diagnostics.ProcessStartInfo("claude") { WorkingDirectory = RunPaths.ChatRoot(chat) };

        using var process = await host.StartCommandAsync(RunCommand.Prepared(psi, title), Ct);
        await process.WaitForExitAsync(Ct);

        var exec = host.Execs.Last();
        Assert.Equal(InMemoryRunHost.ChatName, exec.Name);
        Assert.Equal($"HOME={RunPaths.ChatHome(chat)}", exec.Env.Single(c => c.StartsWith("HOME=", StringComparison.Ordinal)));
        Assert.Contains(exec.Command, c => c.StartsWith(title.ToString("N") + "/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sign_in_terminal_runs_in_an_environment_of_its_own_that_goes_with_it()
    {
        var host = Host();

        var terminal = await host.OpenSignInAsync(agent: null, "/usr/local/bin/claude", ["setup-token"], 80, 24, Ct);

        var name = Assert.Single(host.SignInsStarted);
        Assert.StartsWith(RunNames.SignInPrefix, name);
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

    private async Task<SignInTerminal> OpenReadingAsync(InMemoryRunHost host, Func<InMemoryExec, Task> answer)
    {
        // The terminal runs until it is disposed; what reads the login file is the one command that is not a terminal.
        host.OnExec = e => e.Tty ? Task.CompletedTask : answer(e);
        return await host.OpenSignInAsync(agent: null, "claude", [], 80, 24, Ct);
    }

    [Fact]
    public async Task A_login_file_is_read_out_of_the_sign_in_environment_as_text()
    {
        var host = Host();
        await using var terminal = await OpenReadingAsync(host, async e =>
        {
            await e.WriteStdoutAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"a":1}""")));
            await e.ExitAsync(0);
        });

        var text = await terminal.ReadLoginAsync(".claude/.credentials.json", Ct);

        Assert.Equal("""{"a":1}""", text);
        var script = host.Execs.Last().Command;
        Assert.Contains("! -L", script[5]);
        Assert.Contains("-le \"$2\"", script[5]);
        Assert.Equal([".claude/.credentials.json", "262144", "/tmp/home"], script.TakeLast(3));
    }

    [Fact]
    public async Task A_link_or_an_oversized_login_file_is_not_read()
    {
        // The script's own refusal (not a regular file, or over the cap) is its exit status 3: nothing is printed.
        await using var terminal = await OpenReadingAsync(Host(), e => e.ExitAsync(3));

        Assert.Null(await terminal.ReadLoginAsync(".codex/auth.json", Ct));
    }

    [Fact]
    public async Task A_sign_in_login_read_that_fails_otherwise_is_an_error()
    {
        await using var terminal = await OpenReadingAsync(Host(), async e =>
        {
            await e.WriteStderrAsync("base64: boom");
            await e.ExitAsync(1);
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => terminal.ReadLoginAsync(".codex/auth.json", Ct));
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task Deleting_a_run_the_engine_no_longer_holds_is_not_an_error_and_asks_the_engine_to_remove_it()
    {
        var host = Host();
        host.HoldsRun = _ => false;
        var run = Guid.NewGuid();

        await host.DeleteAsync(new RunEnvironment(run, RunPaths.WorkingDirectory(run)), Ct);

        Assert.Equal([run], host.Removed);
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
