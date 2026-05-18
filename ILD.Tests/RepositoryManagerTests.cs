using System.Diagnostics;
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

        Assert.True(Directory.Exists(path));
        Assert.True(File.Exists(Path.Combine(path, "README.md")));

        Assert.True((await mgr.ValidateWorktreeHealthAsync(path)));
    }

    [Fact]
    public async Task CreateWorktree_supports_branch_names_with_slashes()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var path = await mgr.CreateWorktreeAsync(_repo, "ild/wi-11");

        Assert.EndsWith(Path.Combine("ild", "wi-11"), path);
        Assert.True(Directory.Exists(path));
        Assert.True(File.Exists(Path.Combine(path, "README.md")));
        Assert.True((await mgr.ValidateWorktreeHealthAsync(path)));
    }

    [Fact]
    public async Task Commit_and_diff_round_trip()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var path = await mgr.CreateWorktreeAsync(_repo, "feature-y");
        File.WriteAllText(Path.Combine(path, "new.txt"), "content\n");

        Assert.True((await mgr.CommitAsync(path, "add file")));

        var diff = await mgr.GetDiffAsync(path);
        Assert.NotNull(diff);
    }

    [Fact]
    public async Task DestroyWorktree_removes_directory()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var path = await mgr.CreateWorktreeAsync(_repo, "feature-z");
        Assert.True(Directory.Exists(path));

        await mgr.DestroyWorktreeAsync(path);
        Assert.False(Directory.Exists(path));
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

        Assert.Equal(stalePath, path);
        Assert.True(File.Exists(Path.Combine(path, "README.md")));
        Assert.False(File.Exists(Path.Combine(path, "leftover.txt")));
        Assert.True((await mgr.ValidateWorktreeHealthAsync(path)));
    }

    [Fact]
    public async Task ReadFile_returns_content_and_blocks_path_traversal()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var path = await mgr.CreateWorktreeAsync(_repo, "feature-r");

        Assert.Contains("hello", (await mgr.ReadFileAsync(path, "README.md")));
        Assert.Null((await mgr.ReadFileAsync(path, "../README.md")));
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

        Assert.Single(runner.Calls);
        Assert.NotNull(runner.Calls[0].Environment);
        Assert.False(string.IsNullOrWhiteSpace(runner.Calls[0].Environment!["GIT_ASKPASS"]));
        Assert.Equal("oauth2", runner.Calls[0].Environment!["ILD_GIT_USERNAME"]);
        Assert.Equal("token-123", runner.Calls[0].Environment!["ILD_GIT_PASSWORD"]);
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

        Assert.Single(runner.Calls);
        Assert.NotNull(runner.Calls[0].Environment);
        Assert.Equal("git", runner.Calls[0].Environment!["ILD_GIT_USERNAME"]);
        Assert.Equal("token-123", runner.Calls[0].Environment!["ILD_GIT_PASSWORD"]);
    }

    [Fact]
    public async Task DeleteLocalBranchAsync_deletes_existing_branch()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        // Create a branch to delete
        Git(_repo, "branch", "to-delete");

        var success = await mgr.DeleteLocalBranchAsync(_repo, "to-delete");
        Assert.True(success);

        // Verify branch is gone
        var listResult = GitOutput(_repo, "branch", "--list", "to-delete");
        Assert.Empty(listResult.Trim());
    }

    [Fact]
    public async Task DeleteLocalBranchAsync_calls_git_branch_D_with_correct_args()
    {
        var runner = new RecordingRunner();
        var mgr = new RepositoryManager(runner, worktreesRoot: Path.Combine(_tmp, "wt"));

        await mgr.DeleteLocalBranchAsync(_repo, "does-not-exist");

        Assert.Single(runner.Calls);
        Assert.Equal("git", runner.Calls[0].FileName);
        Assert.Contains("-D", runner.Calls[0].Args);
        Assert.Contains("does-not-exist", runner.Calls[0].Args);
    }

    [Fact]
    public async Task ResolveBaseRepoPathAsync_returns_base_repo_from_worktree()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var wtPath = await mgr.CreateWorktreeAsync(_repo, "resolve-test");

        var basePath = await mgr.ResolveBaseRepoPathAsync(wtPath);
        Assert.NotNull(basePath);
        Assert.Equal(_repo, Path.GetFullPath(basePath));
    }

    private static string GitOutput(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.StandardOutput.ReadToEnd();
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
