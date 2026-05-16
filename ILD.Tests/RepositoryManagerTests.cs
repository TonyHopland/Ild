using System.Diagnostics;
using FluentAssertions;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

[Collection("Git")]
public class RepositoryManagerTests : IDisposable
{
    private sealed class RecordingRunner : IProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Args, string? WorkingDirectory, IReadOnlyDictionary<string, string?>? Environment)> Calls { get; } = new();

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, string? workingDirectory = null, CancellationToken ct = default, IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            Calls.Add((fileName, args, workingDirectory, environmentVariables == null ? null : new Dictionary<string, string?>(environmentVariables)));
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

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
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var path = await mgr.CreateWorktreeAsync(_repo, "feature-x");

        Directory.Exists(path).Should().BeTrue();
        File.Exists(Path.Combine(path, "README.md")).Should().BeTrue();

        (await mgr.ValidateWorktreeHealthAsync(path)).Should().BeTrue();
    }

    [Fact]
    public async Task CreateWorktree_supports_branch_names_with_slashes()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var path = await mgr.CreateWorktreeAsync(_repo, "ild/wi-11");

        path.Should().EndWith(Path.Combine("ild", "wi-11"));
        Directory.Exists(path).Should().BeTrue();
        File.Exists(Path.Combine(path, "README.md")).Should().BeTrue();
        (await mgr.ValidateWorktreeHealthAsync(path)).Should().BeTrue();
    }

    [Fact]
    public async Task Commit_and_diff_round_trip()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var path = await mgr.CreateWorktreeAsync(_repo, "feature-y");
        File.WriteAllText(Path.Combine(path, "new.txt"), "content\n");

        (await mgr.CommitAsync(path, "add file")).Should().BeTrue();

        var diff = await mgr.GetDiffAsync(path);
        diff.Should().NotBeNull();
    }

    [Fact]
    public async Task DestroyWorktree_removes_directory()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var path = await mgr.CreateWorktreeAsync(_repo, "feature-z");
        Directory.Exists(path).Should().BeTrue();

        await mgr.DestroyWorktreeAsync(path);
        Directory.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task CreateWorktree_recreates_stale_non_repo_directory()
    {
        var root = Path.Combine(_tmp, "wt");
        var stalePath = Path.Combine(root, "ild", "wi-11");
        Directory.CreateDirectory(stalePath);
        File.WriteAllText(Path.Combine(stalePath, "leftover.txt"), "stale");

        var mgr = new RepositoryManager(worktreesRoot: root);
        var path = await mgr.CreateWorktreeAsync(_repo, "ild/wi-11");

        path.Should().Be(stalePath);
        File.Exists(Path.Combine(path, "README.md")).Should().BeTrue();
        File.Exists(Path.Combine(path, "leftover.txt")).Should().BeFalse();
        (await mgr.ValidateWorktreeHealthAsync(path)).Should().BeTrue();
    }

    [Fact]
    public async Task ReadFile_returns_content_and_blocks_path_traversal()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var path = await mgr.CreateWorktreeAsync(_repo, "feature-r");

        (await mgr.ReadFileAsync(path, "README.md")).Should().Contain("hello");
        (await mgr.ReadFileAsync(path, "../README.md")).Should().BeNull();
    }

    [Fact]
    public async Task CloneAsync_passes_git_askpass_environment_when_api_key_is_present()
    {
        var runner = new RecordingRunner();
        var mgr = new RepositoryManager(runner, worktreesRoot: Path.Combine(_tmp, "wt"));
        var targetPath = Path.Combine(_tmp, "clone-target");

        await mgr.CloneAsync(
            "https://gitlab.example.com/group/repo.git",
            targetPath,
            auth: new GitAuthOptions("https://gitlab.example.com/group/repo.git", "token-123", "GitLab"));

        runner.Calls.Should().ContainSingle();
        runner.Calls[0].Environment.Should().NotBeNull();
        runner.Calls[0].Environment!["GIT_ASKPASS"].Should().NotBeNullOrWhiteSpace();
        runner.Calls[0].Environment!["ILD_GIT_USERNAME"].Should().Be("oauth2");
        runner.Calls[0].Environment!["ILD_GIT_PASSWORD"].Should().Be("token-123");
    }

    [Fact]
    public async Task PushAsync_uses_non_blank_username_for_forgejo_style_remotes()
    {
        var runner = new RecordingRunner();
        var mgr = new RepositoryManager(runner, worktreesRoot: Path.Combine(_tmp, "wt"));

        await mgr.PushAsync(
            _repo,
            "ild/wi-17",
            auth: new GitAuthOptions("https://git.kube/team/repo.git", "token-123", "Forgejo"));

        runner.Calls.Should().ContainSingle();
        runner.Calls[0].Environment.Should().NotBeNull();
        runner.Calls[0].Environment!["ILD_GIT_USERNAME"].Should().Be("git");
        runner.Calls[0].Environment!["ILD_GIT_PASSWORD"].Should().Be("token-123");
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
