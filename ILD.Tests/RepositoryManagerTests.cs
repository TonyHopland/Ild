using System.Diagnostics;
using FluentAssertions;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

[Collection("Git")]
public class RepositoryManagerTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _repo;

    public RepositoryManagerTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ild-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        _repo = Path.Combine(_tmp, "repo");
        Directory.CreateDirectory(_repo);

        Git(_repo, "init", "-b", "main");
        Git(_repo, "config", "user.email", "t@t.io");
        Git(_repo, "config", "user.name", "Tester");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "hello\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "init");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    [Fact]
    public async Task CreateWorktree_creates_a_new_branch_and_directory()
    {
        var mgr = new RepositoryManager();
        var path = await mgr.CreateWorktreeAsync(_repo, "feature-x");

        Directory.Exists(path).Should().BeTrue();
        File.Exists(Path.Combine(path, "README.md")).Should().BeTrue();

        (await mgr.ValidateWorktreeHealthAsync(path)).Should().BeTrue();
    }

    [Fact]
    public async Task Commit_and_diff_round_trip()
    {
        var mgr = new RepositoryManager();
        var path = await mgr.CreateWorktreeAsync(_repo, "feature-y");
        File.WriteAllText(Path.Combine(path, "new.txt"), "content\n");

        (await mgr.CommitAsync(path, "add file")).Should().BeTrue();

        var diff = await mgr.GetDiffAsync(path);
        diff.Should().NotBeNull();
    }

    [Fact]
    public async Task DestroyWorktree_removes_directory()
    {
        var mgr = new RepositoryManager();
        var path = await mgr.CreateWorktreeAsync(_repo, "feature-z");
        Directory.Exists(path).Should().BeTrue();

        await mgr.DestroyWorktreeAsync(path);
        Directory.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task ReadFile_returns_content_and_blocks_path_traversal()
    {
        var mgr = new RepositoryManager();
        var path = await mgr.CreateWorktreeAsync(_repo, "feature-r");

        (await mgr.ReadFileAsync(path, "README.md")).Should().Contain("hello");
        (await mgr.ReadFileAsync(path, "../README.md")).Should().BeNull();
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {p.StandardError.ReadToEnd()}");
    }
}
