using System.Diagnostics;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

/// <summary>
/// The git behaviour an attached branch leans on, against real repositories: a
/// fetch that sees a branch deleted on origin, and a worktree started from
/// origin's copy that never resumes from history left behind locally.
/// </summary>
public sealed class RepositoryManagerAttachTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ild-attach-git-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private (string Origin, string Clone, RepositoryManager Git) OriginWithFeatureBranch()
    {
        var origin = Path.Combine(_root, "origin");
        var clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(origin);
        Git(origin, "init", "-b", "main");
        Git(origin, "config", "user.email", "t@example.test");
        Git(origin, "config", "user.name", "Tester");
        Commit(origin, "README.md", "init");
        Git(origin, "checkout", "-b", "feature/x");
        Commit(origin, "feature.txt", "feature work");
        Git(origin, "checkout", "main");
        Git(_root, "clone", origin, clone);
        Git(clone, "config", "user.email", "t@example.test");
        Git(clone, "config", "user.name", "Tester");
        return (origin, clone, new RepositoryManager(worktreesRoot: Path.Combine(_root, "wt")));
    }

    [Fact]
    public async Task A_fetch_sees_a_branch_deleted_on_origin()
    {
        var (origin, clone, git) = OriginWithFeatureBranch();
        Assert.True(await git.RemoteBranchExistsAsync(clone, "feature/x"));

        Git(origin, "branch", "-D", "feature/x");
        Assert.True(await git.FetchAsync(clone));

        Assert.False(await git.RemoteBranchExistsAsync(clone, "feature/x"));
    }

    [Fact]
    public async Task A_worktree_started_from_origin_replaces_a_stale_local_branch_and_directory()
    {
        var (origin, clone, git) = OriginWithFeatureBranch();
        // An earlier attempt left a worktree and a local branch at the old tip…
        var first = await git.CreateWorktreeAsync(clone, "feature/x", "origin/feature/x");
        Commit(first, "local.txt", "never pushed");
        // …and the branch has since moved on origin.
        Git(origin, "checkout", "feature/x");
        Commit(origin, "more.txt", "author pushed");
        Git(origin, "checkout", "main");
        Assert.True(await git.FetchAsync(clone));
        var originTip = GitOut(clone, "rev-parse", "origin/feature/x");

        var again = await git.CreateWorktreeAsync(clone, "feature/x", "origin/feature/x");

        Assert.Equal(originTip, GitOut(again, "rev-parse", "HEAD"));
        Assert.False(File.Exists(Path.Combine(again, "local.txt")));
    }

    [Fact]
    public async Task The_branch_another_worktree_has_checked_out_is_refused()
    {
        var (_, clone, git) = OriginWithFeatureBranch();
        var elsewhere = Path.Combine(_root, "elsewhere");
        Git(clone, "worktree", "add", "-b", "feature/x", elsewhere, "origin/feature/x");

        await Assert.ThrowsAsync<InvalidOperationException>(() => git.CreateWorktreeAsync(clone, "feature/x", "origin/feature/x"));
    }

    private static void Commit(string repo, string file, string message)
    {
        File.WriteAllText(Path.Combine(repo, file), message + "\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-m", message);
    }

    private static void Git(string cwd, params string[] args) => GitOut(cwd, args);

    private static string GitOut(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {p.StandardError.ReadToEnd()}");
        return stdout.Trim();
    }
}
