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

        // Deliberately nothing else here: the worktree must contain only what git
        // checked out. CommitAsync runs `git add -A`, so any file synthesized at
        // setup time rides into the run commit and from there into the PR.
        //
        // This used to drop a stub opencode.json to pin the project root. It was
        // both leaky and unnecessary: the adapter passes `--dir <worktreePath>`
        // explicitly and feeds config in-memory via OPENCODE_CONFIG_CONTENT
        // (OpenCodeAdapter.BuildRunProcessStartInfo), so the file was never read
        // for config, and the worktree's own .git already marks the root.
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
        var (code, _, _) = await RunAsync(
            worktreePath,
            new[] { "fetch", "origin", "--prune", "+refs/heads/*:refs/remotes/origin/*" },
            cancellationToken,
            auth);
        return code == 0;
    }


    public async Task<RebaseResult> RebaseAsync(string worktreePath, string upstreamBranch, CancellationToken cancellationToken = default)
    {
        int code;
        string stderr;
        try
        {
            (code, _, stderr) = await RunAsync(worktreePath, new[] { "rebase", upstreamBranch }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancellation kills the git process mid-rebase and propagates as an
            // exception, so this is the one path that MUST unwind explicitly — and
            // the one where a half-rebased worktree is most expensive: the worktree
            // outlives the run that owned it (ADR-0008), so nothing later comes
            // along to repair it.
            await AbortRebaseAsync(worktreePath);
            throw;
        }

        if (code == 0)
            return new RebaseResult(true, Array.Empty<string>(), null);

        // Read the unmerged index entries BEFORE unwinding — the abort is what
        // makes a failed rebase safe to retry, but it also erases the evidence.
        var conflicted = await ListZeroSeparatedAsync(worktreePath, "diff", "--name-only", "--diff-filter=U", "-z");
        await AbortRebaseAsync(worktreePath);

        return new RebaseResult(false, conflicted, FormatGitError(stderr));
    }

    /// <summary>
    /// Unwind an incomplete rebase, leaving the branch as it was. Always on
    /// <see cref="CancellationToken.None"/>, never the caller's: on the path that
    /// needs this most the caller's token is already cancelled, and an abort that
    /// was itself cancelled would leave precisely the state it exists to prevent.
    ///
    /// <para>
    /// Exits non-zero both when there was no rebase to unwind (the common, harmless
    /// case — a rebase git refused outright never started one) and when the unwind
    /// itself failed, which git does not distinguish in its exit code. Neither is
    /// worth failing the caller over, so the code is not inspected; the underlying
    /// runner logs it.
    /// </para>
    /// </summary>
    private Task AbortRebaseAsync(string worktreePath)
        => RunAsync(worktreePath, new[] { "rebase", "--abort" }, CancellationToken.None);

    public async Task<bool> ResetHardAsync(string worktreePath, string revision, CancellationToken cancellationToken = default)
    {
        var (code, _, _) = await RunAsync(worktreePath, new[] { "reset", "--hard", revision }, cancellationToken);
        return code == 0;
    }

    /// <summary>
    /// Untracked secret files ILD must never let ride into a run's commit — and from
    /// there into the PR — even when the repository forgot to <c>.gitignore</c> them.
    /// The repository custom <c>.env</c> (see <c>Repository.PreviewEnv</c>) is injected
    /// into preview processes as environment variables and never written here, but a
    /// preview/install step (or the repo) can materialise it as a dotenv file;
    /// <c>.ild.env</c> is the reserved path ILD guarantees is always excluded.
    /// </summary>
    private static readonly string[] SecretExcludePatterns = { ".env", ".ild.env" };

    public async Task<bool> CommitAsync(string worktreePath, string message)
    {
        await EnsureSecretExcludesAsync(worktreePath);
        await RunAsync(worktreePath, "add", "-A");
        var (code, _, _) = await RunAsync(worktreePath, "commit", "-m", message);
        return code == 0;
    }

    /// <summary>
    /// Writes <see cref="SecretExcludePatterns"/> into the worktree's local git
    /// exclude (<c>info/exclude</c>) so the subsequent <c>git add -A</c> can't stage
    /// them. The exclude is git-local — never committed or pushed — and ignore rules
    /// only apply to <em>untracked</em> paths, so a repository that deliberately
    /// tracks its own <c>.env</c> is unaffected while a stray secret file the repo
    /// forgot to ignore is kept out of the PR. Best-effort: failures never block the
    /// commit.
    /// </summary>
    private async Task EnsureSecretExcludesAsync(string worktreePath)
    {
        try
        {
            var (code, stdout, _) = await RunAsync(worktreePath, "rev-parse", "--git-path", "info/exclude");
            if (code != 0 || string.IsNullOrWhiteSpace(stdout)) return;

            var excludePath = stdout.Trim();
            if (!Path.IsPathRooted(excludePath))
                excludePath = Path.GetFullPath(Path.Combine(worktreePath, excludePath));

            var existing = File.Exists(excludePath)
                ? await File.ReadAllLinesAsync(excludePath)
                : Array.Empty<string>();
            var have = new HashSet<string>(existing.Select(l => l.Trim()), StringComparer.Ordinal);
            var missing = SecretExcludePatterns.Where(p => !have.Contains(p)).ToList();
            if (missing.Count == 0) return;

            Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);
            var prefix = existing.Length > 0 && existing[^1].Length > 0 ? Environment.NewLine : string.Empty;
            await File.AppendAllTextAsync(excludePath, prefix + string.Join(Environment.NewLine, missing) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to write secret excludes for {Worktree}", worktreePath);
        }
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

    public Task<IReadOnlyList<string>> GetUncommittedFilesAsync(string worktreePath)
        => ListZeroSeparatedAsync(worktreePath, "diff", "--name-only", "-z", "HEAD");

    public async Task<int> GetCommitsAheadCountAsync(string worktreePath, string targetBranch)
    {
        var (code, stdout, _) = await RunAsync(worktreePath, "rev-list", "--count", $"{targetBranch}..HEAD");
        return code == 0 && int.TryParse(stdout.Trim(), out var count) ? count : 0;
    }

    public async Task<int> GetCommitsBehindCountAsync(string worktreePath, string upstreamRef)
    {
        var (code, stdout, _) = await RunAsync(worktreePath, "rev-list", "--count", $"HEAD..{upstreamRef}");
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

    public async Task<IReadOnlyList<WorktreeFileEntry>> ListWorktreeFilesAsync(string worktreePath, string? defaultBranch = null)
    {
        if (!await ValidateWorktreeHealthAsync(worktreePath))
            return Array.Empty<WorktreeFileEntry>();

        var baseRef = await ResolveDiffBaseAsync(worktreePath, defaultBranch);

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

    public async Task<WorktreeFileContentResponse?> ReadWorktreeFileAsync(string worktreePath, string relativePath, string? defaultBranch = null)
    {
        var full = ResolveSafePath(worktreePath, relativePath);
        if (full == null) return null;

        var baseRef = await ResolveDiffBaseAsync(worktreePath, defaultBranch);

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
            {
                response.IsBinary = true;
                // A binary the viewer can draw ships its bytes inline; every
                // other one stays content-less, as does an image past the cap.
                var mime = InlineImageMimeType(relativePath);
                if (mime != null && bytes.Length <= MaxInlineImageBytes)
                {
                    response.ImageMimeType = mime;
                    response.ImageBase64 = Convert.ToBase64String(bytes);
                }
            }
            else
            {
                response.Content = System.Text.Encoding.UTF8.GetString(bytes);
            }
        }

        return response;
    }

    /// <summary>
    /// Resolve the fork point the run branched from. Prefers the repository's
    /// stored <paramref name="defaultBranch"/> (as <c>origin/&lt;branch&gt;</c>,
    /// then bare <c>&lt;branch&gt;</c>) and falls back to <c>origin/HEAD</c> when
    /// that branch is unset or doesn't resolve in the worktree. Pinning the
    /// merge-base keeps later fast-forwards on the default branch from dragging
    /// unrelated commits into the diff.
    /// </summary>
    private async Task<string> ResolveDiffBaseAsync(string worktreePath, string? defaultBranch)
    {
        foreach (var candidate in EnumerateBaseRefCandidates(defaultBranch))
        {
            var (code, stdout, _) = await RunAsync(worktreePath, "merge-base", "HEAD", candidate);
            if (code == 0 && !string.IsNullOrWhiteSpace(stdout))
                return stdout.Trim();
        }

        // Nothing resolved (e.g. origin/HEAD unset in this worktree); keep the
        // literal ref so the caller's diff fails loudly rather than silently.
        return "origin/HEAD";
    }

    private static IEnumerable<string> EnumerateBaseRefCandidates(string? defaultBranch)
    {
        if (!string.IsNullOrWhiteSpace(defaultBranch))
        {
            var trimmed = defaultBranch.Trim();
            yield return $"origin/{trimmed}";
            yield return trimmed;
        }

        yield return "origin/HEAD";
    }

    public async Task<RemoteRepositoryInfo?> InspectRemoteAsync(string cloneUrl, CancellationToken cancellationToken = default, GitAuthOptions? auth = null)
    {
        if (string.IsNullOrWhiteSpace(cloneUrl))
            return null;

        var (code, stdout, _) = await RunAsync(Path.GetTempPath(), new[] { "ls-remote", "--symref", cloneUrl, "HEAD" }, cancellationToken, auth);
        if (code != 0)
            return null;

        return new RemoteRepositoryInfo(ParseSymrefDefaultBranch(stdout), ParseRepoNameFromUrl(cloneUrl));
    }

    public async Task<bool?> RemoteHasBranchAsync(string cloneUrl, string branchName, CancellationToken cancellationToken = default, GitAuthOptions? auth = null)
    {
        if (string.IsNullOrWhiteSpace(cloneUrl) || string.IsNullOrWhiteSpace(branchName))
            return null;

        var (code, _, _) = await RunAsync(
            Path.GetTempPath(),
            new[] { "ls-remote", "--heads", "--exit-code", cloneUrl, $"refs/heads/{branchName}" },
            cancellationToken, auth);

        // `--exit-code` is what makes the three answers we need distinguishable,
        // and it encodes them in the exit code alone: 0 = the ref is there,
        // 2 = the remote answered and does not have it, anything else = we never
        // got an answer. Deliberately not corroborated against stdout — a
        // swallowed line would turn an existing branch into "free", which is the
        // one direction this check must never err in.
        return code switch
        {
            0 => true,
            2 => false,
            _ => null,
        };
    }

    // `git ls-remote --symref <url> HEAD` advertises the default branch as a
    // line like "ref: refs/heads/main\tHEAD"; pull the branch name out of it.
    private static string? ParseSymrefDefaultBranch(string lsRemoteOutput)
    {
        foreach (var line in lsRemoteOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("ref:", StringComparison.Ordinal)) continue;

            var rest = trimmed["ref:".Length..].Trim();
            var refName = rest.Split('\t', ' ')[0];
            const string prefix = "refs/heads/";
            if (refName.StartsWith(prefix, StringComparison.Ordinal))
                return refName[prefix.Length..];
        }

        return null;
    }

    private static string? ParseRepoNameFromUrl(string cloneUrl)
    {
        var trimmed = cloneUrl.Trim().TrimEnd('/');
        if (trimmed.Length == 0) return null;

        // Handle both URL ("https://host/group/repo.git") and scp-like
        // ("git@host:group/repo.git") forms by splitting on both separators.
        var lastSegment = trimmed.Split('/', ':')[^1];
        if (lastSegment.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            lastSegment = lastSegment[..^".git".Length];

        return string.IsNullOrWhiteSpace(lastSegment) ? null : lastSegment;
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

    /// <summary>
    /// Ceiling on the bytes of one image inlined into a file-content response.
    /// The payload rides in the JSON body, so an unbounded asset would be paid
    /// for base64-inflated by a third on top of its own size; past this the file
    /// falls back to the plain binary shape rather than failing the read.
    /// </summary>
    private const int MaxInlineImageBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The media type to render <paramref name="relativePath"/> as an image
    /// under, or null if the viewer has no business drawing it. Keyed on the
    /// extension rather than sniffed content: the value ends up in a data URL
    /// the browser trusts, so a mislabelled file should fail to draw rather than
    /// be re-typed into something the extension never claimed.
    /// <para>
    /// SVG is deliberately absent. It is text, so it never reaches this branch
    /// in the first place and renders as source in the code view — which is also
    /// the outcome we want: an SVG from a worktree is untrusted input, and
    /// drawing one carries its scripts along with it.
    /// </para>
    /// </summary>
    private static string? InlineImageMimeType(string relativePath) =>
        Path.GetExtension(relativePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".ico" => "image/x-icon",
            ".avif" => "image/avif",
            _ => null,
        };

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

    public async Task<bool> RemoteBranchExistsAsync(string repoPath, string branchName)
    {
        var (code, _, _) = await RunAsync(repoPath, "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branchName}");
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

    /// <summary>
    /// Materialize the helper git calls as <c>GIT_ASKPASS</c> to feed it the
    /// repository token. It lives under the orchestrator-private root, never a
    /// fixed path in world-writable <c>/tmp</c>: this script is executed by the
    /// orchestrator with <c>ILD_GIT_PASSWORD</c> in its environment, so a version
    /// planted by the (now separately-uid'd) agent would be both arbitrary code as
    /// the orchestrator and credential exfiltration — and the "only write it if it
    /// is missing" guard below is exactly what would trust the planted copy. See
    /// <see cref="AgentIsolation.PrivateRoot"/> and ADR-0014.
    /// </summary>
    private static string EnsureAskPassScript()
    {
        var path = Path.Combine(AgentIsolation.CreatePrivateDirectory(), "git-askpass.sh");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "#!/bin/sh\ncase \"$1\" in\n  *Username*) printf '%s\\n' \"${ILD_GIT_USERNAME:-git}\" ;;\n  *Password*) printf '%s\\n' \"${ILD_GIT_PASSWORD:-}\" ;;\n  *) printf '\\n' ;;\nesac\n");
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    // Owner-only: nothing outside the orchestrator needs to read or
                    // run it, and the private root already denies the agent access.
                    File.SetUnixFileMode(path,
                        UnixFileMode.UserRead |
                        UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute);
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
