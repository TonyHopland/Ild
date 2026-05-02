using System.Diagnostics;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Wraps git CLI via System.Diagnostics.Process to manage repository worktrees.
/// All operations are best-effort; non-zero exit codes return false (or empty for queries).
/// </summary>
public class RepositoryManager : IRepositoryManager
{
    private readonly ILogger<RepositoryManager>? _logger;
    private readonly string _worktreesRoot;

    public RepositoryManager(ILogger<RepositoryManager>? logger = null, string? worktreesRoot = null)
    {
        _logger = logger;
        _worktreesRoot = worktreesRoot ?? Path.Combine("/tmp", "ild-worktrees");
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

    public async Task<bool> PushAsync(string worktreePath, string branchName, CancellationToken cancellationToken = default)
    {
        var (code, _, _) = await RunAsync(worktreePath, new[] { "push", "-u", "origin", branchName }, cancellationToken);
        return code == 0;
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
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        _logger?.LogDebug("Executing git {Args} in {Worktree}", string.Join(' ', args), cwd);

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("git {Args} in {Worktree} timed out, killing process", string.Join(' ', args), cwd);
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process has already exited; nothing to kill.
            }
            catch (Exception killEx)
            {
                _logger?.LogWarning(killEx, "Unexpected error killing git process");
            }
            throw;
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        _logger?.LogDebug("git {Args} in {Worktree} exited {Code}", string.Join(' ', args), cwd, proc.ExitCode);
        if (proc.ExitCode != 0)
            _logger?.LogDebug("git {Args} exited {Code}: {Err}", string.Join(' ', args), proc.ExitCode, stderr);
        return (proc.ExitCode, stdout, stderr);
    }
}
