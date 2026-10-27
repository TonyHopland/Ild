using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

/// <summary>
/// In a uid-isolated deployment (ADR-0014) every git call that acts on an agent-writable
/// repository is launched as <c>setpriv --reuid=&lt;agent&gt; ... -- git</c>, and the
/// orchestrator's own git (mirrors, ls-remote) never crosses to another uid. The
/// <see cref="AuditingRunner"/> checks the start info each call would get; the inner
/// runner answers every command with success, so no git runs.
/// </summary>
public sealed class RepositoryManagerAgentWrapTests : IDisposable
{
    private sealed class SucceedingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> args, string? workingDirectory = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string?>? environmentVariables = null, ProcessRunOptions? options = null)
            => Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
    }

    private readonly string _tmp = Directory.CreateTempSubdirectory("ild-agent-wrap-").FullName;
    private readonly string _clone;
    private readonly AuditingRunner _runner = new(isolated: true, inner: new SucceedingRunner());

    public RepositoryManagerAgentWrapTests()
    {
        // A directory whose .git is a real directory is a clone of ours, which is all the manager checks before running git.
        _clone = Path.Combine(_tmp, "clone");
        Directory.CreateDirectory(Path.Combine(_clone, ".git"));
        _runner.Forbid(_clone);
        _runner.Forbid(Path.Combine(_tmp, "wt"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private RepositoryManager NewManager()
        => new(_runner, null, Path.Combine(_tmp, "wt"), _runner.ManagerAgent, Path.Combine(_tmp, "mirrors"), sharedGroup: "agents");

    [Fact]
    public async Task Every_git_call_on_a_clone_or_worktree_is_launched_as_the_agent()
    {
        var manager = NewManager();
        var ct = TestContext.Current.CancellationToken;

        await manager.DeleteLocalBranchAsync(_clone, "old-branch");
        await manager.ResetHardAsync(_clone, "origin/main", ct);
        await manager.GetUncommittedFilesAsync(_clone);
        await manager.GetHeadCommitAsync(_clone);
        await manager.GetDiffAsync(_clone);
        await manager.LocalBranchExistsAsync(_clone, "feature");
        await manager.PruneWorktreesAsync(_clone);

        _runner.AssertClean();
        var calls = _runner.Calls;
        Assert.True(calls.Count >= 7, "each operation runs git");
        Assert.All(calls, call => Assert.False(call.Orchestrator));
    }

    [Fact]
    public async Task The_git_that_populates_a_clone_runs_as_the_agent_and_the_mirror_it_reads_from_as_the_orchestrator()
    {
        var manager = NewManager();

        var (success, error) = await manager.CloneAsync("https://example.test/repo.git", Path.Combine(_tmp, "cloned"), TestContext.Current.CancellationToken);

        Assert.True(success, error);
        _runner.AssertClean();
        var calls = _runner.Calls;
        Assert.Contains(calls, call => call.Args.Contains("clone") && !call.Orchestrator);
        Assert.Contains(calls, call => call.Orchestrator);
    }

    [Fact]
    public async Task The_orchestrators_own_git_never_crosses_to_the_agent_uid()
    {
        var manager = NewManager();
        var ct = TestContext.Current.CancellationToken;

        await manager.InspectRemoteAsync("https://example.test/repo.git", ct);
        await manager.RemoteHasBranchAsync("https://example.test/repo.git", "main", ct);
        await manager.ProbeRemoteAsync("https://example.test/repo.git", "main", ct);

        _runner.AssertClean();
        var calls = _runner.Calls;
        Assert.True(calls.Count >= 3, "each operation runs git");
        Assert.All(calls, call => Assert.True(call.Orchestrator));
    }
}
