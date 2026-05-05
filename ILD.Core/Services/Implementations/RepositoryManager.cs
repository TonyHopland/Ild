using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Wraps git CLI via <see cref="IProcessRunner"/> to manage repository worktrees.
/// All operations are best-effort; non-zero exit codes return false (or empty for queries).
/// </summary>
public class RepositoryManager : IRepositoryManager
{
    private readonly ILogger<RepositoryManager>? _logger;
    private readonly IProcessRunner _runner;
    private readonly string _worktreesRoot;

    public RepositoryManager(IProcessRunner runner, ILogger<RepositoryManager>? logger = null, string? worktreesRoot = null)
    {
        _runner = runner;
        _logger = logger;
        _worktreesRoot = worktreesRoot ?? Path.Combine("/tmp", "ild-worktrees");
    }

    // Test/back-compat constructor: spins up a local ProcessRunner.
    public RepositoryManager(ILogger<RepositoryManager>? logger = null, string? worktreesRoot = null)
        : this(new ProcessRunner(), logger, worktreesRoot)
    {
    }

    public async Task<(bool Success, string? Error)> CloneAsync(string cloneUrl, string targetPath, CancellationToken cancellationToken = default)
    {
        var parent = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        var result = await _runner.RunAsync("git", new[] { "clone", cloneUrl, targetPath }, parent, cancellationToken);
        return result.Success ? (true, null) : (false, result.StdErr);
    }

    public async Task<string> CreateWorktreeAsync(string repoPath, string branchName)
    {
        Directory.CreateDirectory(_worktreesRoot);
        var worktreePath = Path.GetFullPath(Path.Combine(_worktreesRoot, branchName));

        // Try add as new branch first; if branch already exists, attach to it.
        var (code, _, _) = await RunAsync(repoPath, "worktree", "add", "-b", branchName, worktreePath);
        if (code != 0)
        {
            var (code2, _, _) = await RunAsync(repoPath, "worktree", "add", worktreePath, branchName);
            if (code2 != 0)
                throw new InvalidOperationException($"Failed to create worktree at {worktreePath}");
        }

        // Pin project root so opencode --dir works and doesn't walk up to parent workspace.
        var opencodeConfig = Path.Combine(worktreePath, "opencode.json");
        if (!File.Exists(opencodeConfig))
            File.WriteAllText(opencodeConfig, "{\"$schema\":\"https://opencode.ai/config.json\"}");

        return worktreePath;
    }

    public async Task DestroyWorktreeAsync(string worktreePath)
    {
        if (!Directory.Exists(worktreePath)) return;
        var repoPath = await ResolveMainRepoPathAsync(worktreePath) ?? worktreePath;
        await RunAsync(repoPath, "worktree", "remove", "--force", worktreePath);
        if (Directory.Exists(worktreePath))
        {
            try { Directory.Delete(worktreePath, recursive: true); } catch { /* best effort */ }
        }
    }

    public async Task<bool> ValidateWorktreeHealthAsync(string worktreePath)
    {
        if (!Directory.Exists(worktreePath)) return false;
        var (code, _, _) = await RunAsync(worktreePath, "rev-parse", "--is-inside-work-tree");
        return code == 0;
    }

    public async Task<bool> CheckoutBranchAsync(string worktreePath, string branchName)
    {
        var (code, _, _) = await RunAsync(worktreePath, "checkout", branchName);
        return code == 0;
    }

    public async Task<bool> FetchAsync(string worktreePath, CancellationToken cancellationToken = default)
    {
        var (code, _, _) = await RunAsync(worktreePath, new[] { "fetch", "origin" }, cancellationToken);
        return code == 0;
    }

    public async Task<bool> PullAsync(string worktreePath, CancellationToken cancellationToken = default)
    {
        var (code, _, _) = await RunAsync(worktreePath, new[] { "pull", "--ff-only" }, cancellationToken);
        return code == 0;
    }

    public async Task<bool> RebaseAsync(string worktreePath, string upstreamBranch, CancellationToken cancellationToken = default)
    {
        var (code, _, _) = await RunAsync(worktreePath, new[] { "rebase", upstreamBranch }, cancellationToken);
        return code == 0;
    }

    public async Task<bool> CommitAsync(string worktreePath, string message)
    {
        await RunAsync(worktreePath, "add", "-A");
        var (code, _, _) = await RunAsync(worktreePath, "commit", "-m", message);
        return code == 0;
    }

    public async Task<(bool Success, string? Error)> PushAsync(string worktreePath, string branchName, CancellationToken cancellationToken = default)
    {
        var (code, _, stderr) = await RunAsync(worktreePath, new[] { "push", "-u", "origin", branchName }, cancellationToken);
        return code == 0 ? (true, null) : (false, stderr);
    }

    public async Task<string?> GetDiffAsync(string worktreePath)
    {
        var (code, stdout, _) = await RunAsync(worktreePath, "diff", "HEAD");
        return code == 0 ? stdout : null;
    }

    public async Task<int> GetCommitsAheadCountAsync(string worktreePath, string targetBranch)
    {
        var (code, stdout, _) = await RunAsync(worktreePath, "rev-list", "--count", $"{targetBranch}..HEAD");
        return code == 0 && int.TryParse(stdout.Trim(), out var count) ? count : 0;
    }

    public async Task<string?> ReadFileAsync(string worktreePath, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(worktreePath, relativePath));
        // Path traversal guard.
        var root = Path.GetFullPath(worktreePath);
        if (!full.StartsWith(root, StringComparison.Ordinal))
            return null;
        if (!File.Exists(full)) return null;
        return await File.ReadAllTextAsync(full);
    }

    private async Task<string?> ResolveMainRepoPathAsync(string worktreePath)
    {
        var (code, stdout, _) = await RunAsync(worktreePath, "rev-parse", "--git-common-dir");
        if (code != 0) return null;
        var gitDir = stdout.Trim();
        if (!Path.IsPathRooted(gitDir)) gitDir = Path.GetFullPath(Path.Combine(worktreePath, gitDir));
        return Path.GetDirectoryName(gitDir);
    }

    private Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string cwd, params string[] args)
        => RunAsync(cwd, args, CancellationToken.None);

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string cwd, IReadOnlyList<string> args, CancellationToken ct)
    {
        var r = await _runner.RunAsync("git", args, cwd, ct);
        if (!r.Success)
            _logger?.LogDebug("git {Args} in {Worktree} exited {Code}: {Err}", string.Join(' ', args), cwd, r.ExitCode, r.StdErr);
        return (r.ExitCode, r.StdOut, r.StdErr);
    }
}
