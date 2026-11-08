using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

/// <summary>
/// Every git call that acts on an agent-writable repository is agent-scoped (none of the
/// orchestrator's secrets or git credentials), and the orchestrator's own git (mirrors,
/// ls-remote) runs from a clean environment. The
/// <see cref="AuditingRunner"/> checks the start info each call would get; the inner
/// runner answers every command with success, so no git runs.
/// </summary>
[Collection(GitBundleScratchCollection.Name)]
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
    private readonly AuditingRunner _runner = new(inner: new SucceedingRunner());

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
        => new(_runner, null, Path.Combine(_tmp, "wt"), Path.Combine(_tmp, "mirrors"));

    [Fact]
    public async Task Every_git_call_on_a_clone_or_worktree_is_agent_scoped()
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
    public async Task The_mirror_is_read_as_the_orchestrator_and_the_clone_is_populated_by_an_agent_script()
    {
        var manager = NewManager();

        var (success, error) = await manager.CloneAsync("https://example.test/repo.git", Path.Combine(_tmp, "cloned"), TestContext.Current.CancellationToken);

        Assert.True(success, error);
        _runner.AssertClean();
        var calls = _runner.Calls;
        Assert.Contains(calls, call => call.Args.Contains("for-each-ref") && call.Orchestrator);
        Assert.DoesNotContain(calls, call => call.Args.Contains("clone"));
    }

    [Fact]
    public async Task The_orchestrators_own_git_is_never_agent_scoped()
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
