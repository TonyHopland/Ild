using ILD.Core.Services.RunHosts.Local;
using System.Runtime.Versioning;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

/// <summary>
/// Code enters a run as a bundle the orchestrator writes from its mirror and leaves as one the agent writes,
/// so the agent never reads the mirror store (ADR-0014). The store is owner-only and
/// <see cref="AuditingRunner"/> fails any agent call that names it.
/// </summary>
[Collection(GitBundleScratchCollection.Name)]
[SupportedOSPlatform("linux")]
public class RepositoryManagerBundleFlowTests : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "ild-bundle-flow-" + Guid.NewGuid().ToString("N"));
    private readonly string _mirrors;
    private readonly string _worktrees;
    private readonly List<AuditingRunner> _runners = new();

    public RepositoryManagerBundleFlowTests()
    {
        Directory.CreateDirectory(_tmp);
        _mirrors = Path.Combine(_tmp, "mirrors");
        _worktrees = Path.Combine(_tmp, "wt");
    }

    public void Dispose()
    {
        var violations = _runners.SelectMany(r => r.Violations).ToList();
        try { Directory.Delete(_tmp, recursive: true); } catch { }
        GC.SuppressFinalize(this);
        Assert.True(violations.Count == 0, "git isolation violated:\n" + string.Join("\n", violations));
    }

    /// <summary>Passes every call on and remembers the orchestrator's <c>bundle create</c> calls.</summary>
    private sealed class BundleCountingRunner : IProcessRunner
    {
        private readonly IProcessRunner _inner = new ProcessRunner();
        private readonly List<IReadOnlyList<string>> _bundles = new();

        public IReadOnlyList<IReadOnlyList<string>> Bundles { get { lock (_bundles) return _bundles.ToList(); } }

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, string? workingDirectory = null, CancellationToken ct = default, IReadOnlyDictionary<string, string?>? environmentVariables = null, ProcessRunOptions? options = null)
        {
            if (fileName == "git" && options?.CleanEnvironment == true && args.Contains("bundle") && args.Contains("create"))
                lock (_bundles) _bundles.Add(args.ToList());
            return _inner.RunAsync(fileName, args, workingDirectory, ct, environmentVariables, options);
        }
    }

    private (RepositoryManager Mgr, BundleCountingRunner Bundles) NewManager()
    {
        var bundles = new BundleCountingRunner();
        var runner = new AuditingRunner(inner: bundles);
        runner.Forbid(_worktrees);
        runner.Forbid(Path.Combine(_tmp, "base"));
        runner.ForbidAgentAccess(_mirrors);
        _runners.Add(runner);
        return (new RepositoryManager(runner, null, _worktrees, _mirrors), bundles);
    }

    private static void Upstream(string remote, string seed, string file)
    {
        File.WriteAllText(Path.Combine(seed, file), file + "\n");
        GitCli.Run(seed, "add", "-A");
        GitCli.Run(seed, "commit", "-m", file);
        GitCli.Run(seed, "push", remote, "main");
    }

    [Fact]
    public async Task Start_fetch_rebase_and_push_work_with_the_mirror_store_closed_to_the_agent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mgr, _) = NewManager();
        var remote = GitCli.MakeRemote(_tmp, "remote");
        var seed = Path.Combine(_tmp, "remote-seed");
        var auth = new GitAuthOptions(remote, "tok", "Forgejo");
        var basePath = Path.Combine(_tmp, "base");

        // Start.
        var (ok, error) = await mgr.CloneAsync(remote, basePath, ct, auth);
        Assert.True(ok, error);
        Assert.Equal(remote, GitCli.Out(basePath, "config", "remote.origin.url"));
        Assert.Equal("refs/remotes/origin/main", GitCli.Out(basePath, "symbolic-ref", "refs/remotes/origin/HEAD"));
        Assert.True(File.Exists(Path.Combine(basePath, "README.md")));
        GitCli.Run(basePath, "config", "user.name", "Tester");
        GitCli.Run(basePath, "config", "user.email", "t@t.io");

        var wt = await mgr.CreateWorktreeAsync(basePath, "ild/wi-1-run-1");
        File.WriteAllText(Path.Combine(wt, "run.txt"), "run output\n");
        Assert.True(await mgr.CommitAsync(wt, "run work"));

        // Fetch and rebase onto the base after it moved.
        Upstream(remote, seed, "late.txt");
        Assert.True(await mgr.FetchAsync(wt, ct, auth));
        Assert.Equal(GitCli.Out(seed, "rev-parse", "HEAD"), GitCli.Out(basePath, "rev-parse", "refs/remotes/origin/main"));
        Assert.True((await mgr.RebaseAsync(wt, "origin/main", ct)).Success);
        Assert.True(File.Exists(Path.Combine(wt, "late.txt")));

        // Push, then fetch into the run again.
        var head = GitCli.Out(wt, "rev-parse", "HEAD");
        var (pushed, pushError) = await mgr.PushAsync(wt, "ild/wi-1-run-1", ct, auth);
        Assert.True(pushed, pushError);
        Assert.Equal(head, GitCli.Out(remote, "rev-parse", "refs/heads/ild/wi-1-run-1"));
        Assert.Equal(head, GitCli.Out(basePath, "rev-parse", "refs/remotes/origin/ild/wi-1-run-1"));
        Upstream(remote, seed, "later.txt");
        Assert.True(await mgr.FetchAsync(wt, ct, auth));
        Assert.Equal(GitCli.Out(seed, "rev-parse", "HEAD"), GitCli.Out(basePath, "rev-parse", "refs/remotes/origin/main"));
        Assert.Equal(head, GitCli.Out(basePath, "rev-parse", "refs/remotes/origin/ild/wi-1-run-1"));

        // The store never opened to anyone but the orchestrator.
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(_mirrors));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(Assert.Single(Directory.GetDirectories(_mirrors))));
        Assert.Empty(Directory.GetFiles(basePath, "*.bundle", SearchOption.AllDirectories));
        Assert.Equal("", GitCli.Out(basePath, "for-each-ref", "refs/ild-sync"));
    }

    [Fact]
    public async Task A_store_an_earlier_version_shared_with_the_agent_is_closed_when_it_is_next_used()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mgr, _) = NewManager();
        var remote = GitCli.MakeRemote(_tmp, "remote");
        var auth = new GitAuthOptions(remote, null);
        Assert.True((await mgr.CloneAsync(remote, Path.Combine(_tmp, "base"), ct, auth)).Success);
        var mirror = Assert.Single(Directory.GetDirectories(_mirrors));
        File.SetUnixFileMode(_mirrors, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.SetGroup);
        File.SetUnixFileMode(mirror, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        Assert.True((await mgr.CloneAsync(remote, Path.Combine(_tmp, "base2"), ct, auth)).Success);

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(_mirrors));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(mirror));
    }

    [Fact]
    public async Task A_fetch_sends_only_what_the_run_lacks_keeps_branches_it_already_has_and_removes_deleted_ones()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mgr, bundles) = NewManager();
        var remote = GitCli.MakeRemote(_tmp, "remote");
        var seed = Path.Combine(_tmp, "remote-seed");
        var auth = new GitAuthOptions(remote, "tok", "Forgejo");
        var basePath = Path.Combine(_tmp, "base");
        Assert.True((await mgr.CloneAsync(remote, basePath, ct, auth)).Success);
        var wt = await mgr.CreateWorktreeAsync(basePath, "ild/wi-2-run-1");
        var clonedBundles = bundles.Bundles.Count;
        Assert.Contains(bundles.Bundles, b => !b.Contains("--stdin"));

        // A branch at a commit the run already has: nothing to send, and the branch still appears.
        GitCli.Run(remote, "branch", "keep", "main");
        Assert.True(await mgr.FetchAsync(wt, ct, auth));
        Assert.Equal(clonedBundles, bundles.Bundles.Count);
        Assert.Equal(GitCli.Out(remote, "rev-parse", "keep"), GitCli.Out(basePath, "rev-parse", "refs/remotes/origin/keep"));

        // main moves: a narrow bundle, and `keep`, whose tip the run has and the bundle leaves out, survives.
        var before = GitCli.Out(remote, "rev-parse", "main");
        Upstream(remote, seed, "moved.txt");
        Assert.True(await mgr.FetchAsync(wt, ct, auth));
        var narrow = Assert.Single(bundles.Bundles.Skip(clonedBundles));
        Assert.Contains("--stdin", narrow);
        Assert.Equal(GitCli.Out(remote, "rev-parse", "main"), GitCli.Out(basePath, "rev-parse", "refs/remotes/origin/main"));
        Assert.Equal(before, GitCli.Out(basePath, "rev-parse", "refs/remotes/origin/keep"));

        // A branch deleted upstream is removed; one that is new and has its own commit arrives.
        GitCli.Run(remote, "branch", "-D", "keep");
        GitCli.Run(remote, "branch", "fresh", GitCli.Out(remote, "commit-tree", "-p", "main", "-m", "fresh", "main^{tree}"));
        Assert.True(await mgr.FetchAsync(wt, ct, auth));
        Assert.Equal("", GitCli.Out(basePath, "for-each-ref", "refs/remotes/origin/keep"));
        Assert.Equal(GitCli.Out(remote, "rev-parse", "fresh"), GitCli.Out(basePath, "rev-parse", "refs/remotes/origin/fresh"));
        Assert.Equal("refs/remotes/origin/main", GitCli.Out(basePath, "symbolic-ref", "refs/remotes/origin/HEAD"));
    }

    [Fact]
    public async Task A_branch_that_becomes_a_directory_name_upstream_syncs_after_a_fetch_and_after_a_push()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mgr, _) = NewManager();
        var remote = GitCli.MakeRemote(_tmp, "remote");
        var auth = new GitAuthOptions(remote, "tok", "Forgejo");
        var basePath = Path.Combine(_tmp, "base");
        Assert.True((await mgr.CloneAsync(remote, basePath, ct, auth)).Success);
        GitCli.Run(basePath, "config", "user.name", "Tester");
        GitCli.Run(basePath, "config", "user.email", "t@t.io");
        var wt = await mgr.CreateWorktreeAsync(basePath, "ild/wi-3-run-1");

        // A branch exists upstream, the run learns of it, then it is replaced by a directory of branches.
        GitCli.Run(remote, "branch", "feature", GitCli.Out(remote, "commit-tree", "-p", "main", "-m", "f", "main^{tree}"));
        Assert.True(await mgr.FetchAsync(wt, ct, auth));
        Assert.NotEqual("", GitCli.Out(basePath, "for-each-ref", "refs/remotes/origin/feature"));
        GitCli.Run(remote, "branch", "-D", "feature");
        GitCli.Run(remote, "branch", "feature/x", GitCli.Out(remote, "commit-tree", "-p", "main", "-m", "fx", "main^{tree}"));
        Assert.True(await mgr.FetchAsync(wt, ct, auth));
        Assert.Equal(GitCli.Out(remote, "rev-parse", "feature/x"), GitCli.Out(basePath, "rev-parse", "refs/remotes/origin/feature/x"));
        Assert.Equal("", GitCli.Out(basePath, "for-each-ref", "refs/ild-sync"));

        // After a push (which syncs only the pushed branch) the same swap must not leave anything that blocks later fetches.
        File.WriteAllText(Path.Combine(wt, "run.txt"), "out\n");
        Assert.True(await mgr.CommitAsync(wt, "work"));
        GitCli.Run(remote, "branch", "other", GitCli.Out(remote, "commit-tree", "-p", "main", "-m", "o", "main^{tree}"));
        Assert.True((await mgr.PushAsync(wt, "ild/wi-3-run-1", ct, auth)).Success);
        Assert.Equal("", GitCli.Out(basePath, "for-each-ref", "refs/ild-sync"));
        GitCli.Run(remote, "branch", "-D", "other");
        GitCli.Run(remote, "branch", "other/y", GitCli.Out(remote, "commit-tree", "-p", "main", "-m", "oy", "main^{tree}"));
        Assert.True(await mgr.FetchAsync(wt, ct, auth));
        Assert.Equal("refs/remotes/origin/other/y", GitCli.Out(basePath, "for-each-ref", "--format=%(refname)", "refs/remotes/origin/other"));
        Assert.Equal(GitCli.Out(remote, "rev-parse", "other/y"), GitCli.Out(basePath, "rev-parse", "refs/remotes/origin/other/y"));
        Assert.Equal("", GitCli.Out(basePath, "for-each-ref", "refs/ild-sync"));
    }
}
