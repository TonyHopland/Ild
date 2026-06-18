using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
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

    public async Task<string> CreateWorktreeFromAsync(string repoPath, string branchName, string baseRef)
    {
        Directory.CreateDirectory(_worktreesRoot);
        var worktreePath = Path.GetFullPath(Path.Combine(_worktreesRoot, branchName));
        var parent = Path.GetDirectoryName(worktreePath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        // An integration worktree is throwaway and rebuilt on demand, so always
        // start from a clean slate: drop any prior worktree and its branch.
        await RunAsync(repoPath, "worktree", "prune");
        if (Directory.Exists(worktreePath))
        {
            await DestroyWorktreeAsync(worktreePath);
            await RunAsync(repoPath, "worktree", "prune");
        }
        await RunAsync(repoPath, "branch", "-D", branchName);

        var (code, _, stderr) = await RunAsync(repoPath, "worktree", "add", "-b", branchName, worktreePath, baseRef);
        if (code != 0)
            throw new InvalidOperationException(
                $"Failed to create integration worktree at {worktreePath} from {baseRef}: {FormatGitError(stderr)}");

        // Pin project root so opencode --dir works and doesn't walk up to parent workspace.
        var opencodeConfig = Path.Combine(worktreePath, "opencode.json");
        if (!File.Exists(opencodeConfig))
            File.WriteAllText(opencodeConfig, "{\"$schema\":\"https://opencode.ai/config.json\"}");

        return worktreePath;
    }

    public async Task<GitMergeResult> MergeAsync(string worktreePath, string branchRef, string commitMessage, CancellationToken cancellationToken = default)
    {
        var (code, _, stderr) = await RunAsync(worktreePath, new[] { "merge", "--no-ff", "-m", commitMessage, branchRef }, cancellationToken);
        if (code == 0)
            return new GitMergeResult(true, Array.Empty<string>());

        // A conflict leaves the unmerged paths listed by diff-filter=U; anything
        // else (e.g. an unknown ref) is a hard error with no conflicted files.
        var conflicted = await ListZeroSeparatedAsync(worktreePath, "diff", "--name-only", "--diff-filter=U", "-z");
        return new GitMergeResult(false, conflicted, conflicted.Count == 0 ? FormatGitError(stderr) : null);
    }

    public async Task AbortMergeAsync(string worktreePath, CancellationToken cancellationToken = default)
    {
        await RunAsync(worktreePath, new[] { "merge", "--abort" }, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetUnmergedFilesAsync(string worktreePath, CancellationToken cancellationToken = default)
    {
        var (code, stdout, _) = await RunAsync(worktreePath, new[] { "diff", "--name-only", "--diff-filter=U", "-z" }, cancellationToken);
        return code != 0
            ? Array.Empty<string>()
            : stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public async Task<bool> IsMergeInProgressAsync(string worktreePath, CancellationToken cancellationToken = default)
    {
        var (code, _, _) = await RunAsync(worktreePath, new[] { "rev-parse", "-q", "--verify", "MERGE_HEAD" }, cancellationToken);
        return code == 0;
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

    public async Task<IReadOnlyList<WorktreeFileEntry>> ListWorktreeFilesAsync(string worktreePath)
    {
        if (!await ValidateWorktreeHealthAsync(worktreePath))
            return Array.Empty<WorktreeFileEntry>();

        var baseRef = await ResolveDiffBaseAsync(worktreePath);

        // Present files (tracked + untracked), .gitignore honoured. Start every
        // one at "none"; the diff below promotes the ones that actually changed.
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in await ListZeroSeparatedAsync(worktreePath, "ls-files", "--cached", "--others", "--exclude-standard", "-z"))
            statuses[path] = "none";

        // Tracked changes against the fork point — working tree vs base, so
        // uncommitted edits show up too. Parse the NUL-delimited name-status
        // stream; renames/copies emit a source and a destination path.
        var (code, diffOut, _) = await RunAsync(worktreePath, "diff", "--name-status", "-z", baseRef);
        if (code == 0)
        {
            var tokens = diffOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < tokens.Length;)
            {
                var status = tokens[i++];
                if (status.Length == 0) continue;
                var kind = status[0];
                if ((kind == 'R' || kind == 'C') && i + 1 < tokens.Length)
                {
                    statuses[tokens[i++]] = "deleted"; // source
                    statuses[tokens[i++]] = "added";   // destination
                }
                else if (i < tokens.Length)
                {
                    statuses[tokens[i++]] = MapDiffStatus(kind);
                }
            }
        }

        // git diff ignores untracked files, so tag them explicitly.
        foreach (var path in await ListZeroSeparatedAsync(worktreePath, "ls-files", "--others", "--exclude-standard", "-z"))
            statuses[path] = "added";

        return statuses
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new WorktreeFileEntry { Path = kv.Key, ChangeStatus = kv.Value })
            .ToList();
    }

    public async Task<WorktreeFileContentResponse?> ReadWorktreeFileAsync(string worktreePath, string relativePath)
    {
        var full = ResolveSafePath(worktreePath, relativePath);
        if (full == null) return null;

        var baseRef = await ResolveDiffBaseAsync(worktreePath);

        var status = "none";
        string? diff = null;

        var (nsCode, ns, _) = await RunAsync(worktreePath, "diff", "--name-status", "-z", baseRef, "--", relativePath);
        var nsTokens = ns.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (nsCode == 0 && nsTokens.Length > 0 && nsTokens[0].Length > 0)
        {
            status = MapDiffStatus(nsTokens[0][0]);
            var (_, patch, _) = await RunAsync(worktreePath, "diff", baseRef, "--", relativePath);
            diff = string.IsNullOrEmpty(patch) ? null : patch;
        }

        var exists = File.Exists(full);
        if (status == "none" && exists)
        {
            // git diff ignores untracked files, so a brand-new file shows no
            // status above — detect it and synthesize the "all added" diff.
            var (othersCode, others, _) = await RunAsync(worktreePath, "ls-files", "--others", "--exclude-standard", "--", relativePath);
            if (othersCode == 0 && !string.IsNullOrWhiteSpace(others))
            {
                status = "added";
                var (_, addedDiff, _) = await RunAsync(worktreePath, "diff", "--no-index", "--", "/dev/null", relativePath);
                diff = string.IsNullOrEmpty(addedDiff) ? null : addedDiff;
            }
        }

        if (!exists && status == "none")
            return null;

        var response = new WorktreeFileContentResponse
        {
            Path = relativePath,
            ChangeStatus = status,
            Diff = diff,
        };

        if (exists)
        {
            var bytes = await File.ReadAllBytesAsync(full);
            if (IsBinary(bytes))
                response.IsBinary = true;
            else
                response.Content = System.Text.Encoding.UTF8.GetString(bytes);
        }

        return response;
    }

    /// <summary>
    /// Resolve the fork point the run branched from. <c>origin/HEAD</c> tracks
    /// the default branch; pinning the merge-base keeps later fast-forwards on
    /// the default branch from dragging unrelated commits into the diff.
    /// </summary>
    private async Task<string> ResolveDiffBaseAsync(string worktreePath)
    {
        var (code, stdout, _) = await RunAsync(worktreePath, "merge-base", "HEAD", "origin/HEAD");
        return code == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim() : "origin/HEAD";
    }

    private async Task<IReadOnlyList<string>> ListZeroSeparatedAsync(string worktreePath, params string[] args)
    {
        var (code, stdout, _) = await RunAsync(worktreePath, args, CancellationToken.None);
        return code != 0
            ? Array.Empty<string>()
            : stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string MapDiffStatus(char kind) => kind switch
    {
        'A' => "added",
        'D' => "deleted",
        _ => "modified",
    };

    private static string? ResolveSafePath(string worktreePath, string relativePath)
    {
        var root = Path.GetFullPath(worktreePath);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSep, StringComparison.Ordinal) ? full : null;
    }

    private static bool IsBinary(byte[] bytes)
    {
        var limit = Math.Min(bytes.Length, 8000);
        for (var i = 0; i < limit; i++)
            if (bytes[i] == 0) return true;
        return false;
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
