using System.Diagnostics;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Local;
using ILD.Data.Enums;
using ILD.Tests.Fakes;

namespace ILD.Tests;

/// <summary>
/// Where the routing host sends a command, a terminal, a bundle or a chat when an isolated host holds the runs and chats,
/// whichever engine that is, on an engine kept in memory: a run's path goes to the run, a chat to the chats' environment,
/// and a path on ILD's disk is read and deleted locally but never run, and never reaches the isolated host.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public sealed class IsolatedRunHostRoutingTests : IDisposable
{
    private static readonly string[] OldPaths = ["/worktrees/ild/old-run/repo", "/data/chat-sessions/11111111111111111111111111111111"];

    private readonly string? _previousProxyPort = Environment.GetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar);
    private readonly Guid _run = Guid.NewGuid();
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), $"ild-isolated-routing-{Guid.NewGuid():N}");
    private readonly InMemoryRunHost _host;

    public IsolatedRunHostRoutingTests()
    {
        _host = new InMemoryRunHost { Credentials = EgressProxyTests.Creds, HoldsRun = id => id == _run };
        Environment.SetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar, "3128");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar, _previousProxyPort);
        if (Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string WorkingDirectory => RunPaths.WorkingDirectory(_run);

    private static ProcessStartInfo Command(string workingDirectory, string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static async Task RunAsync(RoutingRunHost routing, RunCommand command)
    {
        using var process = await routing.StartCommandAsync(command, Ct);
        await process.WaitForExitAsync(Ct);
    }

    // -- Runs ----------------------------------------------------------------------

    [Fact]
    public async Task A_preview_step_in_its_state_directory_runs_in_the_runs_environment()
    {
        var local = new FakeRunHost();
        var routing = new RoutingRunHost(local, _host);
        var state = routing.PreviewStateDirectoryFor(WorkingDirectory);

        await RunAsync(routing, RunCommand.Prepared(Command(state + "/npm-cache", "npm", "ci")) with { RunIn = _run });

        Assert.Equal($"{RunPaths.RunRoot(_run)}/preview-state", state);
        Assert.Empty(local.Started);
        Assert.Equal(_host.NameOf(_run), _host.Execs.Last().Name);
    }

    [Fact]
    public async Task A_remote_branch_check_in_a_worktree_goes_to_the_run_for_a_run_path_and_is_refused_for_a_path_on_ilds_disk()
    {
        var routing = new RoutingRunHost(new FakeRunHost(), _host);
        var repositories = new RepositoryManager(new ProcessRunner(), routing, worktreesRoot: Path.Combine(_scratch, "wt"));
        var forged = Path.Combine(_scratch, "worktrees", "old-run");
        Directory.CreateDirectory(Path.Combine(forged, ".git"));

        await Assert.ThrowsAsync<PredatesContainersException>(() => repositories.RemoteBranchExistsAsync(forged, "ild/x"));

        Assert.Empty(_host.Execs);
        await repositories.RemoteBranchExistsAsync(WorkingDirectory, "ild/x");
        Assert.NotEmpty(_host.Execs);
    }

    // -- Runs and chats from before isolation: read and deleted, never run -----------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_terminal_a_tool_and_a_bundle_for_a_path_on_ilds_disk_are_refused_and_reach_no_host(int which)
    {
        var local = new FakeRunHost();
        var routing = new RoutingRunHost(local, _host);
        var path = OldPaths[which];

        await Assert.ThrowsAsync<PredatesContainersException>(() => routing.OpenTerminalAsync(new Porta.Pty.PtyOptions { Cwd = path, App = "sh" }, NetworkMode.Monitor, Ct));
        await Assert.ThrowsAsync<PredatesContainersException>(() => routing.RunToolAsync(path, "git", ["status"], null, Ct));
        await Assert.ThrowsAsync<PredatesContainersException>(() => routing.PushBundleAsync(path, "origin", new MemoryStream(), Ct));
        await Assert.ThrowsAsync<PredatesContainersException>(() => routing.FetchBundleAsync(path, new MemoryStream(), Ct));
        await Assert.ThrowsAsync<PredatesContainersException>(() => routing.PullBundleAsync(path, "main", false, new MemoryStream(), Ct));
        await Assert.ThrowsAsync<PredatesContainersException>(() => routing.AddChatToolsAsync(path, "/base", Ct));
        Assert.Throws<PredatesContainersException>(() => routing.HoldChat(path));

        Assert.Empty(local.Started);
        Assert.Empty(_host.Execs);
    }

    [Fact]
    public async Task Paths_on_ilds_disk_are_read_locally_but_never_run_and_loop_runs_go_to_the_run()
    {
        var local = new FakeRunHost().WithDirectory("/data/chat-sessions/c1");
        var routing = new RoutingRunHost(local, _host);
        var chat = Guid.NewGuid();

        Assert.True(await routing.DirectoryExistsAsync("/data/chat-sessions/c1", Ct));
        Assert.Empty(_host.Execs);

        var chatCommand = RunCommand.Prepared(Command("/data/chat-sessions/c1", "git", "status"), chat);
        await Assert.ThrowsAsync<PredatesContainersException>(() => routing.StartCommandAsync(chatCommand, Ct));
        Assert.Empty(local.Started);
        Assert.Empty(_host.Execs);

        await routing.DirectoryExistsAsync(WorkingDirectory, Ct);
        Assert.Single(_host.Execs);

        await routing.KillAllAsync(chat, Ct);
        Assert.Equal([chat], local.KillAllCalls);
        Assert.Single(_host.Execs);
    }

    [Fact]
    public async Task Reading_and_deleting_on_ilds_disk_still_reach_the_local_host()
    {
        var local = new FakeRunHost();
        var routing = new RoutingRunHost(local, _host);

        var notes = OldPaths[0] + "/notes.md";
        await local.WriteFileAsync(notes, "history", Ct);

        Assert.Equal("history", await routing.ReadTextFileAsync(notes, Ct));
        Assert.True(await routing.DeleteAsync([OldPaths[0], notes], Ct));
        Assert.Null(await local.ReadTextFileAsync(notes, Ct));
    }

    [Fact]
    public async Task Git_in_a_path_on_ilds_disk_is_refused_whatever_its_dot_git_looks_like_and_branch_cleanup_in_a_base_clone_still_works()
    {
        var forged = Path.Combine(_scratch, "worktrees", "old-run");
        Directory.CreateDirectory(Path.Combine(forged, ".git"));
        var routing = new RoutingRunHost(new FakeRunHost(), _host);
        await Assert.ThrowsAsync<PredatesContainersException>(() => routing.RunToolAsync(forged, "git", ["status"], null, Ct));

        var baseClone = Path.Combine(_scratch, "base");
        Directory.CreateDirectory(baseClone);
        using (var init = Process.Start(new ProcessStartInfo("git", ["init", "-q", baseClone])))
            await init!.WaitForExitAsync(Ct);
        var repositories = new RepositoryManager(new ProcessRunner(), routing, worktreesRoot: Path.Combine(_scratch, "wt"));

        Assert.False(await repositories.LocalBranchExistsAsync(baseClone, "ild/none"));
        Assert.False(await repositories.DeleteLocalBranchAsync(baseClone, "ild/none"));
    }

    [Fact]
    public async Task A_base_clone_named_by_a_repository_row_is_cloned_fetched_and_reset_on_ilds_disk_and_any_other_path_there_is_refused()
    {
        var remote = GitCli.MakeRemote(_scratch, "remote");
        var seed = Path.Combine(_scratch, "remote-seed");
        var auth = new GitAuthOptions(remote, "tok", "Forgejo");
        var baseClone = Path.Combine(_scratch, "repos", "base");
        var runner = new ProcessRunner();
        var routing = new RoutingRunHost(new LocalRunHost(runner), _host);
        var repositories = new RepositoryManager(
            runner, routing, worktreesRoot: Path.Combine(_scratch, "wt"), mirrorsRoot: Path.Combine(_scratch, "mirrors"),
            isBaseRepository: (path, _) => ValueTask.FromResult(path == baseClone));

        var (cloned, error) = await repositories.CloneAsync(remote, baseClone, Ct, auth);
        Assert.True(cloned, error);

        File.WriteAllText(Path.Combine(seed, "late.txt"), "late\n");
        GitCli.Run(seed, "add", "-A");
        GitCli.Run(seed, "commit", "-m", "late");
        GitCli.Run(seed, "push", remote, "main");
        Assert.True(await repositories.FetchAsync(baseClone, Ct, auth));
        Assert.True(await repositories.ResetHardAsync(baseClone, "origin/main", Ct));
        Assert.True(File.Exists(Path.Combine(baseClone, "late.txt")));

        var other = Path.Combine(_scratch, "repos", "other");
        await Assert.ThrowsAsync<PredatesContainersException>(() => repositories.CloneAsync(remote, other, Ct, auth));
        await Assert.ThrowsAsync<PredatesContainersException>(
            () => new RepositoryManager(runner, routing, worktreesRoot: Path.Combine(_scratch, "wt"), mirrorsRoot: Path.Combine(_scratch, "mirrors"))
                .ResetHardAsync(baseClone, "origin/main", Ct));
    }

    // -- Chats ---------------------------------------------------------------------

    [Fact]
    public async Task The_routing_host_keeps_chats_in_the_chats_environment_and_chats_from_before_on_the_local_host()
    {
        var chat = Guid.NewGuid();
        var local = new FakeRunHost().WithDirectory("/data/chat-sessions/old");
        var routing = new RoutingRunHost(local, _host);

        var path = await routing.CreateChatFolderAsync(chat, "/data/chat-sessions", Ct);
        Assert.Equal(RunPaths.ChatRoot(chat), path);
        Assert.Empty(local.ChatFolders);
        Assert.True(_host.HostsChats);
        Assert.Contains(RunPaths.ChatHome(chat), _host.Execs.First().Command);

        await RunAsync(routing, RunCommand.Prepared(Command(RunPaths.ChatRoot(chat), "git", "status"), chat));
        Assert.Empty(local.Started);
        Assert.All(_host.Execs, e => Assert.Equal(InMemoryRunHost.ChatName, e.Name));
        Assert.Contains(RunPaths.ChatRoot(chat), _host.Execs.Last().Command);

        var before = _host.Execs.Count;
        Assert.True(await routing.DirectoryExistsAsync("/data/chat-sessions/old", Ct));
        Assert.Equal(before, _host.Execs.Count);

        await routing.DeleteAsync([RunPaths.ChatRoot(chat), "/data/chat-sessions/old"], Ct);
        Assert.Equal(InMemoryRunHost.ChatName, _host.Execs.Last().Name);
        Assert.Contains(RunPaths.ChatRoot(chat), _host.Execs.Last().Command);
    }
}
