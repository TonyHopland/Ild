using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Wraps git CLI via <see cref="IProcessRunner"/> to manage repository worktrees.
/// All operations are best-effort; non-zero exit codes return false (or empty for queries).
/// </summary>
public class RepositoryManager : IRepositoryManager
{
    private static readonly string AskPassScriptPath = EnsureAskPassScript();
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

    public async Task<(bool Success, string? Error)> CloneAsync(string cloneUrl, string targetPath, CancellationToken cancellationToken = default, GitAuthOptions? auth = null)
    {
        var parent = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        var result = await _runner.RunAsync("git", new[] { "clone", cloneUrl, targetPath }, parent, cancellationToken, BuildGitEnvironment(auth));
        return result.Success ? (true, null) : (false, result.StdErr);
    }

    public async Task<string> CreateWorktreeAsync(string repoPath, string branchName)
    {
        Directory.CreateDirectory(_worktreesRoot);
        var worktreePath = Path.GetFullPath(Path.Combine(_worktreesRoot, branchName));
        var parent = Path.GetDirectoryName(worktreePath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        await RunAsync(repoPath, "worktree", "prune");

        if (Directory.Exists(worktreePath))
        {
            if (await ValidateWorktreeHealthAsync(worktreePath))
                return worktreePath;

            await DestroyWorktreeAsync(worktreePath);
            await RunAsync(repoPath, "worktree", "prune");
        }

        // Try add as new branch first; if branch already exists, attach to it.
        var (code, _, stderr) = await RunAsync(repoPath, "worktree", "add", "-b", branchName, worktreePath);
        if (code != 0)
        {
            var (code2, _, stderr2) = await RunAsync(repoPath, "worktree", "add", worktreePath, branchName);
            if (code2 != 0)
                throw new InvalidOperationException(
                    $"Failed to create worktree at {worktreePath}. git add -b stderr: {FormatGitError(stderr)} git add existing-branch stderr: {FormatGitError(stderr2)}");
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
            // The fallback delete leaves the worktree registration behind in the
            // base repo; prune it so the branch isn't pinned as "checked out"
            // by a ghost worktree (that would block `git branch -D` later).
            if (repoPath != worktreePath)
                await RunAsync(repoPath, "worktree", "prune");
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

    public async Task<bool> FetchAsync(string worktreePath, CancellationToken cancellationToken = default, GitAuthOptions? auth = null)
    {
        var (code, _, _) = await RunAsync(worktreePath, new[] { "fetch", "origin" }, cancellationToken, auth);
        return code == 0;
    }


    public async Task<bool> RebaseAsync(string worktreePath, string upstreamBranch, CancellationToken cancellationToken = default)
    {
        var (code, _, _) = await RunAsync(worktreePath, new[] { "rebase", upstreamBranch }, cancellationToken);
        return code == 0;
    }

    public async Task<bool> ResetHardAsync(string worktreePath, string revision, CancellationToken cancellationToken = default)
    {
        var (code, _, _) = await RunAsync(worktreePath, new[] { "reset", "--hard", revision }, cancellationToken);
        return code == 0;
    }

    public async Task<bool> CommitAsync(string worktreePath, string message)
    {
        await RunAsync(worktreePath, "add", "-A");
        var (code, _, _) = await RunAsync(worktreePath, "commit", "-m", message);
        return code == 0;
    }

    public async Task<(bool Success, string? Error)> PushAsync(string worktreePath, string branchName, CancellationToken cancellationToken = default, GitAuthOptions? auth = null)
    {
        var (code, _, stderr) = await RunAsync(worktreePath, new[] { "push", "-u", "origin", branchName }, cancellationToken, auth);
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

    public async Task<bool> DeleteLocalBranchAsync(string repoPath, string branchName)
    {
        var (code, _, _) = await RunAsync(repoPath, "branch", "-D", branchName);
        return code == 0;
    }

    public async Task<bool> LocalBranchExistsAsync(string repoPath, string branchName)
    {
        var (code, _, _) = await RunAsync(repoPath, "rev-parse", "--verify", "--quiet", $"refs/heads/{branchName}");
        return code == 0;
    }

    public async Task PruneWorktreesAsync(string repoPath)
    {
        await RunAsync(repoPath, "worktree", "prune");
    }

    public Task<string?> ResolveBaseRepoPathAsync(string worktreePath)
        => ResolveMainRepoPathAsync(worktreePath);

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
        => await RunAsync(cwd, args, ct, null);

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string cwd, IReadOnlyList<string> args, CancellationToken ct, GitAuthOptions? auth)
    {
        var r = await _runner.RunAsync("git", args, cwd, ct, BuildGitEnvironment(auth));
        if (!r.Success)
            _logger?.LogDebug("git {Args} in {Worktree} exited {Code}: {Err}", string.Join(' ', args), cwd, r.ExitCode, r.StdErr);
        return (r.ExitCode, r.StdOut, r.StdErr);
    }

    private static IReadOnlyDictionary<string, string?>? BuildGitEnvironment(GitAuthOptions? auth)
    {
        if (auth == null || string.IsNullOrWhiteSpace(auth.ApiKey))
            return null;

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_ASKPASS"] = AskPassScriptPath,
            ["ILD_GIT_USERNAME"] = ResolveGitUsername(auth.ProviderType, auth.RemoteUrl),
            ["ILD_GIT_PASSWORD"] = auth.ApiKey,
        };
    }

    private static string ResolveGitUsername(string? providerType, string remoteUrl)
    {
        if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            if (!string.IsNullOrWhiteSpace(parts[0]))
                return Uri.UnescapeDataString(parts[0]);
        }

        return providerType?.Trim().ToLowerInvariant() switch
        {
            "github" => "x-access-token",
            "gitlab" => "oauth2",
            _ => "git",
        };
    }

    private static string EnsureAskPassScript()
    {
        var path = Path.Combine(Path.GetTempPath(), "ild-git-askpass.sh");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "#!/bin/sh\ncase \"$1\" in\n  *Username*) printf '%s\\n' \"${ILD_GIT_USERNAME:-git}\" ;;\n  *Password*) printf '%s\\n' \"${ILD_GIT_PASSWORD:-}\" ;;\n  *) printf '\\n' ;;\nesac\n");
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path,
                        UnixFileMode.UserRead |
                        UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead |
                        UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead |
                        UnixFileMode.OtherExecute);
                }
            }
            catch
            {
                // Best effort only.
            }
        }

        return path;
    }

    private static string FormatGitError(string stderr)
        => string.IsNullOrWhiteSpace(stderr) ? "<empty>" : stderr.Trim().Replace(Environment.NewLine, " | ");
}
