using System.Diagnostics;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

/// <summary>
/// The orchestrator never runs git in an agent-writable repository: network git goes
/// through its own mirror, and everything in the clone and worktrees runs as the agent
/// (ADR-0014). Every test runs once per mode, with each git call checked by
/// <see cref="AuditingRunner"/>.
/// </summary>
[Collection(GitBundleScratchCollection.Name)]
public class RepositoryManagerMirrorTests : IDisposable
{
    // Not a group this host knows, so the mirrors stay where they are made.
    private const string SharedGroup = "agents";

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "ild-mirror-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _mirrors;
    private readonly string _worktrees;
    private readonly List<AuditingRunner> _runners = new();

    public RepositoryManagerMirrorTests()
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

    private (RepositoryManager Mgr, AuditingRunner Runner) NewManager(bool isolated, IProcessRunner? inner = null)
    {
        var runner = new AuditingRunner(isolated, inner: inner);
        runner.Forbid(_worktrees);
        runner.Forbid(Path.Combine(_tmp, "base"));
        _runners.Add(runner);
        return (new RepositoryManager(runner, null, _worktrees, runner.ManagerAgent, _mirrors, sharedGroup: SharedGroup), runner);
    }

    private static GitAuthOptions Auth(string remote) => new(remote, "tok-" + Guid.NewGuid().ToString("N"), "Forgejo");

    private async Task<(string Remote, string Base, GitAuthOptions Auth)> ClonedAsync(RepositoryManager mgr, string name = "remote")
    {
        var remote = GitCli.MakeRemote(_tmp, name);
        var basePath = Path.Combine(_tmp, "base");
        var auth = Auth(remote);
        var (ok, error) = await mgr.CloneAsync(remote, basePath, TestContext.Current.CancellationToken, auth);
        Assert.True(ok, error);
        GitCli.Run(basePath, "config", "user.name", "Tester");
        GitCli.Run(basePath, "config", "user.email", "t@t.io");
        return (remote, basePath, auth);
    }

    private async Task<string> CommittedWorktreeAsync(RepositoryManager mgr, string basePath, string branch, string content = "run output\n")
    {
        var wt = await mgr.CreateWorktreeAsync(basePath, branch);
        File.WriteAllText(Path.Combine(wt, "run.txt"), content);
        Assert.True(await mgr.CommitAsync(wt, "run work"));
        return wt;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clone_gives_an_independent_copy_and_push_leaves_the_clone_tracking_the_remote(bool isolated)
    {
        var (mgr, runner) = NewManager(isolated);
        var (remote, basePath, auth) = await ClonedAsync(mgr);

        Assert.False(File.Exists(Path.Combine(basePath, ".git", "objects", "info", "alternates")));
        var mirror = Assert.Single(Directory.GetDirectories(_mirrors));
        Assert.Empty(ObjectInodes(basePath).Intersect(ObjectInodes(mirror)));

        var bundlesBefore = AuditingRunner.BundleFilesUnderPrivateRoot();
        var wt = await CommittedWorktreeAsync(mgr, basePath, "ild/wi-9-run-1");
        var head = GitCli.Out(wt, "rev-parse", "HEAD");

        var (ok, error) = await mgr.PushAsync(wt, "ild/wi-9-run-1", TestContext.Current.CancellationToken, auth);

        Assert.True(ok, error);
        Assert.Equal(head, GitCli.Out(remote, "rev-parse", "refs/heads/ild/wi-9-run-1"));
        Assert.Equal(head, GitCli.Out(basePath, "rev-parse", "refs/remotes/origin/ild/wi-9-run-1"));
        Assert.Equal("origin/ild/wi-9-run-1", GitCli.Out(wt, "rev-parse", "--abbrev-ref", "ild/wi-9-run-1@{upstream}"));

        // Pushing what is already there is not an error, and a later commit goes up
        // on top of it.
        Assert.True((await mgr.PushAsync(wt, "ild/wi-9-run-1", TestContext.Current.CancellationToken, auth)).Success);
        File.WriteAllText(Path.Combine(wt, "run.txt"), "second\n");
        Assert.True(await mgr.CommitAsync(wt, "second"));
        Assert.True((await mgr.PushAsync(wt, "ild/wi-9-run-1", TestContext.Current.CancellationToken, auth)).Success);
        Assert.Equal(GitCli.Out(wt, "rev-parse", "HEAD"), GitCli.Out(remote, "rev-parse", "refs/heads/ild/wi-9-run-1"));

        // The pushed stream is held in a file only the orchestrator can reach, and
        // is gone afterwards.
        Assert.NotEmpty(runner.BundlesSeen);
        Assert.All(runner.BundlesSeen, b => Assert.Equal(UnixFileMode.None, b.Mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)));
        Assert.Empty(AuditingRunner.BundleFilesUnderPrivateRoot().Except(bundlesBefore));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_mirror_is_reused_per_remote_cannot_be_written_by_the_agent_and_remembers_no_remote(bool isolated)
    {
        var (mgr, _) = NewManager(isolated);
        var (remote, basePath, auth) = await ClonedAsync(mgr);
        var mirror = Assert.Single(Directory.GetDirectories(_mirrors));
        var mirrorInode = Inode(mirror);

        var wt = await CommittedWorktreeAsync(mgr, basePath, "ild/wi-10-run-1");
        Assert.True((await mgr.PushAsync(wt, "ild/wi-10-run-1", TestContext.Current.CancellationToken, auth)).Success);
        Assert.True(await mgr.FetchAsync(wt, TestContext.Current.CancellationToken, auth));

        Assert.Equal(mirror, Assert.Single(Directory.GetDirectories(_mirrors)));
        Assert.Equal(mirrorInode, Inode(mirror));

        foreach (var entry in Directory.EnumerateFileSystemEntries(mirror, "*", SearchOption.AllDirectories)
                     .Where(e => new FileInfo(e).LinkTarget is null))
        {
            var mode = File.GetUnixFileMode(entry);
            var ceiling = Directory.Exists(entry)
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
            Assert.True((mode & ~ceiling) == 0, $"{entry} is {Convert.ToString((int)mode, 8)}: the agent group may only read the mirror");
        }

        Assert.False(File.Exists(Path.Combine(mirror, "FETCH_HEAD")));
        var token = auth.ApiKey!;
        foreach (var file in Directory.EnumerateFiles(mirror, "*", SearchOption.AllDirectories))
        {
            var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(file));
            Assert.DoesNotContain(remote, text);
            Assert.DoesNotContain(token, text);
        }

        // Another remote is another mirror; the first stays as it was.
        var second = GitCli.MakeRemote(_tmp, "other");
        Assert.True((await mgr.CloneAsync(second, Path.Combine(_tmp, "base2"), TestContext.Current.CancellationToken, Auth(second))).Success);
        Assert.Equal(2, Directory.GetDirectories(_mirrors).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fetch_and_push_use_the_callers_remote_not_the_one_the_clones_config_names(bool isolated)
    {
        var (mgr, _) = NewManager(isolated);
        var (remote, basePath, auth) = await ClonedAsync(mgr);
        var planted = GitCli.MakeRemote(_tmp, "planted");
        GitCli.Run(planted, "branch", "planted-branch", "main");
        GitCli.Run(basePath, "config", "remote.origin.url", planted);
        GitCli.Run(basePath, "config", "remote.origin.pushurl", planted);
        var wt = await CommittedWorktreeAsync(mgr, basePath, "ild/wi-11-run-1");

        var seed = Path.Combine(_tmp, "remote-seed");
        File.WriteAllText(Path.Combine(seed, "late.txt"), "late\n");
        GitCli.Run(seed, "add", "-A");
        GitCli.Run(seed, "commit", "-m", "late");
        GitCli.Run(seed, "push", remote, "main");

        Assert.True(await mgr.FetchAsync(wt, TestContext.Current.CancellationToken, auth));
        Assert.True((await mgr.PushAsync(wt, "ild/wi-11-run-1", TestContext.Current.CancellationToken, auth)).Success);

        Assert.Equal(GitCli.Out(seed, "rev-parse", "HEAD"), GitCli.Out(basePath, "rev-parse", "refs/remotes/origin/main"));
        Assert.Equal("", GitCli.Out(basePath, "for-each-ref", "refs/remotes/origin/planted-branch"));
        Assert.Equal(GitCli.Out(wt, "rev-parse", "HEAD"), GitCli.Out(remote, "rev-parse", "refs/heads/ild/wi-11-run-1"));
        Assert.Equal("", GitCli.Out(planted, "for-each-ref", "refs/heads/ild"));
    }

    private sealed class CancelOnPushRunner(IProcessRunner inner) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, string? workingDirectory = null, CancellationToken ct = default, IReadOnlyDictionary<string, string?>? environmentVariables = null, ProcessRunOptions? options = null)
        {
            if (options?.CleanEnvironment == true && args.Contains("push"))
                throw new OperationCanceledException(ct);
            return inner.RunAsync(fileName, args, workingDirectory, ct, environmentVariables, options);
        }
    }

    [Theory]
    [InlineData(false, "fails")]
    [InlineData(true, "fails")]
    [InlineData(false, "is-cancelled")]
    [InlineData(true, "is-cancelled")]
    public async Task A_push_that_fails_or_is_cancelled_leaves_no_bundle_behind(bool isolated, string how)
    {
        var (mgr, _) = NewManager(isolated, how == "is-cancelled" ? new CancelOnPushRunner(new ProcessRunner()) : null);
        var (_, basePath, auth) = await ClonedAsync(mgr);
        var wt = await CommittedWorktreeAsync(mgr, basePath, "ild/wi-12-run-1");
        var before = AuditingRunner.BundleFilesUnderPrivateRoot();
        var target = how == "fails" ? auth with { RemoteUrl = Path.Combine(_tmp, "no-such-remote.git") } : auth;

        try
        {
            var (ok, error) = await mgr.PushAsync(wt, "ild/wi-12-run-1", TestContext.Current.CancellationToken, target);
            if (how == "fails")
            {
                Assert.False(ok);
                Assert.False(string.IsNullOrWhiteSpace(error));
            }
        }
        catch (OperationCanceledException) when (how == "is-cancelled")
        {
        }

        Assert.Empty(AuditingRunner.BundleFilesUnderPrivateRoot().Except(before));
    }

    [Theory]
    [InlineData(false, "symlink")]
    [InlineData(false, "relative-gitdir")]
    [InlineData(false, "gitdir-outside-worktrees")]
    [InlineData(false, "repo-git-is-a-link")]
    [InlineData(false, "backlink-names-another-path")]
    [InlineData(true, "symlink")]
    [InlineData(true, "relative-gitdir")]
    [InlineData(true, "gitdir-outside-worktrees")]
    [InlineData(true, "repo-git-is-a-link")]
    [InlineData(true, "backlink-names-another-path")]
    public async Task A_worktree_whose_git_pointer_cannot_be_trusted_is_refused_without_running_git_in_it(bool isolated, string tampering)
    {
        var (mgr, runner) = NewManager(isolated);
        var (_, basePath, auth) = await ClonedAsync(mgr);
        var wt = await CommittedWorktreeAsync(mgr, basePath, "ild/wi-13-run-1");
        var pointer = Path.Combine(wt, ".git");
        var gitdir = File.ReadAllText(pointer).Trim()["gitdir:".Length..].Trim();
        var elsewhere = Directory.CreateDirectory(Path.Combine(_tmp, "elsewhere")).FullName;

        switch (tampering)
        {
            case "symlink":
                File.Delete(pointer);
                File.CreateSymbolicLink(pointer, gitdir);
                break;
            case "relative-gitdir":
                File.WriteAllText(pointer, "gitdir: " + Path.GetRelativePath(wt, gitdir) + "\n");
                break;
            case "gitdir-outside-worktrees":
                File.WriteAllText(pointer, "gitdir: " + Directory.CreateDirectory(Path.Combine(basePath, ".git", "modules", "x")).FullName + "\n");
                break;
            case "repo-git-is-a-link":
                var linked = Directory.CreateDirectory(Path.Combine(_tmp, "linked")).FullName;
                File.CreateSymbolicLink(Path.Combine(linked, ".git"), Path.Combine(basePath, ".git"));
                File.WriteAllText(pointer, "gitdir: " + Path.Combine(linked, ".git", "worktrees", Path.GetFileName(gitdir)) + "\n");
                break;
            case "backlink-names-another-path":
                File.WriteAllText(Path.Combine(gitdir, "gitdir"), Path.Combine(elsewhere, ".git") + "\n");
                break;
        }

        var before = runner.Calls.Count;
        Assert.False(await mgr.CommitAsync(wt, "refused"));
        Assert.Null(await mgr.GetDiffAsync(wt));
        Assert.Null(await mgr.GetHeadCommitAsync(wt));
        Assert.False(await mgr.ValidateWorktreeHealthAsync(wt));
        Assert.False(await mgr.FetchAsync(wt, TestContext.Current.CancellationToken, auth));
        Assert.False((await mgr.PushAsync(wt, "ild/wi-13-run-1", TestContext.Current.CancellationToken, auth)).Success);
        Assert.False((await mgr.RebaseAsync(wt, "origin/main", TestContext.Current.CancellationToken)).Success);

        var touching = runner.Calls.Skip(before)
            .Where(c => (c.Cwd is not null && IsUnder(c.Cwd, wt)) || c.Args.Contains(wt))
            .Select(c => string.Join(' ', c.Args))
            .ToList();
        Assert.True(touching.Count == 0, "git was started for the refused worktree: " + string.Join("; ", touching));

        // The same path is rebuilt the way an unhealthy worktree is.
        var rebuilt = await mgr.CreateWorktreeAsync(basePath, "ild/wi-13-run-1");
        Assert.Equal(wt, rebuilt);
        Assert.True(File.Exists(Path.Combine(rebuilt, ".git")));
        Assert.True(await mgr.ValidateWorktreeHealthAsync(rebuilt));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_valid_worktree_and_a_plain_base_repo_are_accepted(bool isolated)
    {
        var (mgr, _) = NewManager(isolated);
        var (_, basePath, _) = await ClonedAsync(mgr);
        var wt = await CommittedWorktreeAsync(mgr, basePath, "ild/wi-14-run-1");

        Assert.True(await mgr.ValidateWorktreeHealthAsync(wt));
        Assert.True(await mgr.ValidateWorktreeHealthAsync(basePath));
        Assert.NotNull(await mgr.GetHeadCommitAsync(basePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_symlinked_exclude_file_is_not_written_through_and_the_commit_still_goes_ahead(bool isolated)
    {
        var (mgr, _) = NewManager(isolated);
        var (_, basePath, _) = await ClonedAsync(mgr);
        var info = Directory.CreateDirectory(Path.Combine(basePath, ".git", "info")).FullName;
        var exclude = Path.Combine(info, "exclude");
        var victim = Path.Combine(_tmp, "orchestrator-owned.txt");
        File.WriteAllText(victim, "KEEP\n");
        File.Delete(exclude);
        File.CreateSymbolicLink(exclude, victim);
        var wt = await mgr.CreateWorktreeAsync(basePath, "ild/wi-15-run-1");
        File.WriteAllText(Path.Combine(wt, "app.txt"), "code\n");

        Assert.True(await mgr.CommitAsync(wt, "work"));

        Assert.Equal("KEEP\n", File.ReadAllText(victim));
        Assert.Contains("app.txt", GitCli.Out(wt, "show", "--pretty=", "--name-only", "HEAD"));
    }

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path);
        return full == root || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string Inode(string path) => Stat(path, "%i").Single();

    private static IReadOnlyList<string> ObjectInodes(string repo)
    {
        var objects = Directory.Exists(Path.Combine(repo, ".git", "objects")) ? Path.Combine(repo, ".git", "objects") : Path.Combine(repo, "objects");
        return Stat(objects, "%i", files: true);
    }

    private static List<string> Stat(string path, string format, bool files = false)
    {
        var psi = new ProcessStartInfo("find") { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var a in files ? new[] { path, "-type", "f", "-printf", format + "\\n" } : new[] { path, "-maxdepth", "0", "-printf", format + "\\n" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var lines = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        p.WaitForExit();
        return lines;
    }
}
