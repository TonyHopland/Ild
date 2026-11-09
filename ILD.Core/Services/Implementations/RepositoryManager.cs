using ILD.Core.Services.Implementations.Files;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32.SafeHandles;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Local;
using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Wraps git CLI via <see cref="IProcessRunner"/> to manage repository worktrees.
/// All operations are best-effort; non-zero exit codes return false (or empty for queries).
///
/// <para>
/// Everything git does in a clone or worktree runs agent-scoped: those directories,
/// their config, hooks and attributes are the agent's to write, so git there gets none
/// of the orchestrator's secrets or credentials. The orchestrator runs git with its own
/// credentials only in its own per-remote mirror (<see cref="GitMirror"/>)
/// and for <c>ls-remote</c>, each with a clean configuration and an environment
/// built from scratch (plus the orchestrator's own proxy variables). The remote's
/// URL always comes from the caller, never from the clone's config.
/// </para>
/// </summary>
public class RepositoryManager : IRepositoryManager
{
    private const int RefusedExitCode = 128;

    internal static readonly string[] ProxyEnvironmentKeys =
        ["HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy", "NO_PROXY", "no_proxy"];

    private static readonly string AskPassScriptPath = EnsureAskPassScript();
    private readonly ILogger<RepositoryManager>? _logger;
    private readonly IProcessRunner _runner;
    private readonly IRunHost _host;
    private readonly string _worktreesRoot;
    private readonly GitMirror _mirrors;
    private readonly ProcessRunOptions _agentOptions;
    private readonly Func<string, string?> _readEnvironment;

    /// <param name="mirrorsRoot">Where the per-remote mirrors live.</param>
    public RepositoryManager(
        IProcessRunner runner,
        ILogger<RepositoryManager>? logger = null,
        string? worktreesRoot = null,
        string? mirrorsRoot = null,
        Func<string, string?>? readEnvironment = null,
        IRunHost? host = null)
    {
        _runner = runner;
        _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
        _logger = logger;
        _worktreesRoot = worktreesRoot ?? Path.Combine("/tmp", "ild-worktrees");
        _agentOptions = new ProcessRunOptions(AgentScoped: true);
        _host = host ?? new LocalRunHost(runner);
        _mirrors = new GitMirror(
            mirrorsRoot ?? Path.Combine(Path.GetTempPath(), "ild-git-mirrors"),
            (cwd, args, auth, ct, standardInput) => OrchestratorGitAsync(cwd, args, auth, ct, standardInput: standardInput),
            logger);
    }

    // Test/back-compat constructor: spins up a local ProcessRunner.
    public RepositoryManager(ILogger<RepositoryManager>? logger = null, string? worktreesRoot = null)
        : this(new ProcessRunner(), logger, worktreesRoot)
    {
    }

    /// <summary>Where the mirrors live for a deployment configured by <paramref name="config"/>.</summary>
    public static string MirrorRoot(IConfiguration? config) => BaseRepoPath.MirrorRoot(config);

    public async Task<(bool Success, string? Error)> CloneAsync(string cloneUrl, string targetPath, CancellationToken cancellationToken = default, GitAuthOptions? auth = null)
    {
        // git makes the folders above the clone.
        var target = Path.GetFullPath(targetPath);

        var remote = (auth ?? new GitAuthOptions(cloneUrl, null)) with { RemoteUrl = cloneUrl };
        var (mirror, openError) = await _mirrors.OpenAsync(cloneUrl, cancellationToken);
        if (mirror is null) return (false, openError);
        using (mirror)
        {
            var fetched = await FetchMirrorAsync(mirror, remote, cancellationToken);
            if (!fetched.Ok) return (false, fetched.Error);
            // The agent never reads the mirror: it is handed a bundle on stdin, which it writes to a
            // scratch directory of its own and clones from. A full copy, so the clone stays whole
            // whatever happens to the mirror, and its origin is the remote, not any path of ours.
            var (bundle, bundleError) = await mirror.BundleForAsync([], cancellationToken);
            if (bundle is null) return (false, bundleError);
            await using (bundle)
            {
                var r = await _host.PushBundleAsync(target, WithoutUserInfo(cloneUrl), bundle.Bundle ?? Stream.Null, cancellationToken);
                if (!r.Success)
                    _logger?.LogDebug("Cloning {Target} from the bundle exited {Code}: {Err}", target, r.ExitCode, r.StdErr);
                return r.Success ? (true, null) : (false, r.StdErr);
            }
        }
    }

    public async Task<string> CreateWorktreeAsync(string repoPath, string branchName)
    {
        Directory.CreateDirectory(_worktreesRoot);
        var worktreePath = Path.GetFullPath(Path.Combine(_worktreesRoot, branchName));
        await RunAsync(repoPath, "worktree", "prune");

        if (await _host.DirectoryExistsAsync(worktreePath))
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
        // Whatever is at the path, not just a directory: a file or a link left where
        // the worktree was is still disk nobody else will clear, and following it to
        // decide would be following exactly what the agent could have planted.
        if (!AgentWritableFiles.EntryExists(worktreePath)) return;

        string? repoPath = null;
        if (await _host.DirectoryExistsAsync(worktreePath) && LinkTargetOf(worktreePath) is null)
        {
            // Only a real directory can be a worktree; anything else here just goes.
            // Only a worktree whose pointer is ours is handed to git; anything else is
            // cleared like a stray directory, and its registration, if it has one,
            // is left for the next prune.
            repoPath = TrustedRepositoryRoot(worktreePath);
            if (repoPath is not null)
                await RunAsync(repoPath, "worktree", "remove", "--force", worktreePath);
        }

        if (AgentWritableFiles.EntryExists(worktreePath))
        {
            // The agent writes the worktree, so what git left is cleared without trusting it:
            // its read-only folders are opened first, and a link is unlinked, never
            // followed. What is left is reported, since a worktree nobody removes holds its disk forever.
            string? failure = null;
            try
            {
                if (!await AgentWritableFiles.DeleteAsync([worktreePath]))
                    failure = "not everything in it could be cleared";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                failure = ex.Message;
            }

            if (failure is not null)
                _logger?.LogWarning("Could not remove the worktree {Worktree}: {Reason}. It is left behind and has to be removed by hand.", worktreePath, failure);

            // Removing the directory ourselves leaves the worktree registration
            // behind in the base repo; prune it so the branch isn't pinned as
            // "checked out" by a ghost worktree (that would block `git branch -D`).
            if (repoPath is not null && repoPath != worktreePath)
                await RunAsync(repoPath, "worktree", "prune");
        }
    }

    public async Task<bool> ValidateWorktreeHealthAsync(string worktreePath)
    {
        if (!await _host.DirectoryExistsAsync(worktreePath)) return false;
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
        if (auth is null || string.IsNullOrWhiteSpace(auth.RemoteUrl) || !await _host.IsRunCloneAsync(worktreePath, cancellationToken))
            return false;

        var (mirror, _) = await _mirrors.OpenAsync(auth.RemoteUrl, cancellationToken);
        if (mirror is null) return false;
        using (mirror)
        {
            var fetched = await FetchMirrorAsync(mirror, auth, cancellationToken);
            return fetched.Ok && await SyncFromMirrorAsync(mirror, worktreePath, cancellationToken);
        }
    }

    /// <summary>The remote's branches into the mirror, with the remote's own default branch as the mirror's HEAD.</summary>
    private async Task<(bool Ok, string? Error)> FetchMirrorAsync(GitMirror.Session mirror, GitAuthOptions auth, CancellationToken ct)
    {
        var (code, stdout, _) = await LsRemoteAsync(["ls-remote", "--symref", "--", auth.RemoteUrl, "HEAD"], auth, ct);
        var defaultBranch = code == 0 ? ParseSymrefDefaultBranch(stdout) : null;
        var (ok, error) = await mirror.FetchAsync(auth, defaultBranch, ct);
        return (ok, error);
    }

    /// <summary>
    /// Bring the clone's remote-tracking refs to what the mirror holds: every branch, removing the ones the
    /// mirror no longer has, or just <paramref name="onlyBranch"/> when the mirror is known to hold no more
    /// than that (a push to a mirror not fetched yet). The mirror is not read by the agent: the orchestrator
    /// writes a bundle of what the clone lacks (the clone says what it has), the agent fetches it from its own
    /// scratch into a private namespace, and then the refs are set from the mirror's list of branches. Not
    /// <c>fetch --prune</c>: a narrow bundle leaves out branches the clone already has, and pruning would delete them.
    /// </summary>
    private async Task<bool> SyncFromMirrorAsync(GitMirror.Session mirror, string worktreePath, CancellationToken ct, string? onlyBranch = null)
    {
        const string Remote = "refs/remotes/origin/";
        var listed = await RunAsync(worktreePath, ["for-each-ref", "--format=%(objectname) %(refname)", "refs/remotes/origin", "refs/heads"], ct);
        if (listed.ExitCode != 0) return false;

        var tracking = new Dictionary<string, string>(StringComparer.Ordinal);
        var has = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in listed.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var space = line.IndexOf(' ');
            if (space <= 0 || !IsCommitSha(line[..space])) continue;
            has.Add(line[..space]);
            var name = line[(space + 1)..];
            if (name.StartsWith(Remote, StringComparison.Ordinal) && name != $"{Remote}HEAD")
                tracking[name[Remote.Length..]] = line[..space];
        }

        var mirrored = await BundleIntoAsync(mirror, worktreePath, has, ct);
        if (mirrored is null) return false;
        await using var _ = mirrored;

        var branches = mirrored.Branches.Where(b => onlyBranch is null || b.Name == onlyBranch).ToList();
        try
        {
            // Deletes first and on their own: a branch that became a directory name (feature -> feature/x)
            // cannot be created while the ref it replaces is still there, as `fetch --prune` also saw to.
            var deletes = onlyBranch is null
                ? string.Concat(tracking.Keys.Where(n => branches.All(b => b.Name != n)).Select(n => $"delete {Remote}{n}\n"))
                : string.Empty;
            if (deletes.Length > 0 && !await UpdateRefsAsync(worktreePath, deletes, ct))
                return false;
            if (!await UpdateRefsAsync(worktreePath, string.Concat(branches.Select(b => $"update {Remote}{b.Name} {b.Sha}\n")), ct))
                return false;
        }
        finally
        {
            await ClearSyncRefsAsync(worktreePath);
        }

        if (onlyBranch is null && mirrored.HeadBranch is { } head)
            await RunAsync(worktreePath, ["symbolic-ref", $"{Remote}HEAD", $"{Remote}{head}"], ct);
        return true;
    }

    private async Task<bool> UpdateRefsAsync(string worktreePath, string commands, CancellationToken ct)
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(commands));
        return (await RunAsync(worktreePath, ["update-ref", "--stdin"], ct, stdinFrom: input)).ExitCode == 0;
    }

    // Nothing of the sync namespace outlives a sync, however it ended: a leftover ref would keep objects alive
    // and, once its branch became a directory name, block every later fetch.
    private const string ClearSyncRefsScript = """
        git -C "$1" for-each-ref --format='delete %(refname)' refs/ild-sync | git -C "$1" update-ref --stdin
        """;

    private async Task ClearSyncRefsAsync(string worktreePath)
    {
        if (!await _host.IsRunCloneAsync(worktreePath)) return;
        await _host.RunToolAsync(worktreePath, "/bin/sh", ["-c", ClearSyncRefsScript, "sh", Path.GetFullPath(worktreePath)]);
    }

    /// <summary>The mirror bundled for the clone, with the bundle already fetched into it: a narrow one first, then everything.</summary>
    private async Task<MirrorBundle?> BundleIntoAsync(GitMirror.Session mirror, string worktreePath, IReadOnlyCollection<string> has, CancellationToken ct)
    {
        foreach (var runHas in new[] { has, [] })
        {
            var (bundle, error) = await mirror.BundleForAsync(runHas, ct);
            if (bundle is null)
            {
                _logger?.LogWarning("Could not bundle the mirror for {Worktree}: {Error}", worktreePath, error?.Trim());
                return null;
            }

            if (bundle.Bundle is null || await FetchBundleAsync(worktreePath, bundle.Bundle, ct))
                return bundle;
            var narrow = bundle.Narrow;
            await bundle.DisposeAsync();
            if (!narrow) return null;
        }

        return null;
    }

    private async Task<bool> FetchBundleAsync(string worktreePath, Stream bundle, CancellationToken ct)
    {
        if (!await _host.IsRunCloneAsync(worktreePath, ct))
        {
            _logger?.LogWarning("Refused fetching into {Directory}: its .git is not a clone or worktree of ours", worktreePath);
            return false;
        }

        var r = await _host.FetchBundleAsync(worktreePath, bundle, ct);
        if (!r.Success)
            _logger?.LogDebug("Fetching the bundle into {Worktree} exited {Code}: {Err}", worktreePath, r.ExitCode, r.StdErr);
        return r.Success;
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
        if (!await _host.IsRunCloneAsync(worktreePath)) return;

        // In a shell of its own: the exclude file lives where the agent can replace it with
        // a link, and the orchestrator must never open a path it chose. A link is left
        // alone, so nothing is written through it.
        var script = """
            wt="$1"; shift
            file=$(git -C "$wt" rev-parse --git-path info/exclude) || exit 0
            case "$file" in /*) ;; *) file="$wt/$file" ;; esac
            [ -L "$file" ] && exit 0
            mkdir -p -- "$(dirname -- "$file")" || exit 0
            if [ -s "$file" ] && [ -n "$(tail -c 1 -- "$file")" ]; then printf '\n' >> "$file"; fi
            for pattern in "$@"; do
              grep -qxF -- "$pattern" "$file" 2>/dev/null || printf '%s\n' "$pattern" >> "$file"
            done
            """;
        try
        {
            var result = await _host.RunToolAsync(
                worktreePath, "/bin/sh", ["-c", script, "sh", Path.GetFullPath(worktreePath), .. SecretExcludePatterns]);
            if (!result.Success)
                _logger?.LogDebug("Failed to write secret excludes for {Worktree}: {Error}", worktreePath, result.StdErr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            _logger?.LogDebug(ex, "Failed to write secret excludes for {Worktree}", worktreePath);
        }
    }

    public async Task<(bool Success, string? Error)> PushAsync(string worktreePath, string branchName, CancellationToken cancellationToken = default, GitAuthOptions? auth = null)
    {
        if (auth is null || string.IsNullOrWhiteSpace(auth.RemoteUrl))
            return (false, "no remote URL to push to");

        var (code, stdout, stderr) = await RunAsync(worktreePath, ["rev-parse", "--verify", $"refs/heads/{branchName}^{{commit}}"], cancellationToken);
        var sha = stdout.Trim();
        if (code != 0 || !IsCommitSha(sha))
            return (false, string.IsNullOrWhiteSpace(stderr) ? $"branch '{branchName}' has no commit to push" : stderr);

        if (await RunPrePushHookAsync(worktreePath, branchName, sha, auth, cancellationToken) is { } rejection)
            return (false, rejection);

        var (mirror, openError) = await _mirrors.OpenAsync(auth.RemoteUrl, cancellationToken);
        if (mirror is null) return (false, openError);
        using (mirror)
        {
            // The agent writes the bundle; what the remote receives is what the mirror
            // took from it, pushed by the orchestrator from a place the agent cannot write.
            var pushed = await mirror.PushAsync(
                auth, branchName, sha,
                async (incremental, sink) =>
                {
                    if (!await _host.IsRunCloneAsync(worktreePath, cancellationToken))
                        return new ProcessResult(RefusedExitCode, string.Empty, $"{worktreePath} is not a git clone or worktree created by ILD");
                    var r = await _host.PullBundleAsync(worktreePath, branchName, incremental, sink, cancellationToken);
                    return new ProcessResult(r.ExitCode, string.Empty, r.StdErr);
                },
                cancellationToken);
            if (!pushed.Ok)
                return (false, pushed.Error);

            if (!await SyncFromMirrorAsync(mirror, worktreePath, cancellationToken, branchName)
                || (await RunAsync(worktreePath, ["branch", "--set-upstream-to", $"origin/{branchName}", branchName], cancellationToken)).ExitCode != 0)
                _logger?.LogWarning("Pushed {Branch}, but could not record origin/{Branch} as its upstream in {Worktree}", branchName, branchName, worktreePath);
            return (true, null);
        }
    }

    private const int HookOutputTail = 2000;

    /// <summary>
    /// The worktree's <c>pre-push</c> hook, run agent-scoped before anything is sent, as <c>git push</c>
    /// would run it: with <c>origin</c> and the remote URL as arguments and the ref being pushed on stdin.
    /// The push happens from the mirror, where no clone's hooks run, so this is the only place the hook
    /// gets to object. Null when the hook accepts or there is none; otherwise why it did not. The hook has
    /// no credentials, so one that needs the repository token fails here.
    /// </summary>
    private async Task<string?> RunPrePushHookAsync(
        string worktreePath, string branchName, string sha, GitAuthOptions auth, CancellationToken ct)
    {
        var reference = $"refs/heads/{branchName}";
        var (code, stdout, _) = await RunAsync(worktreePath, ["rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branchName}^{{commit}}"], ct);
        var remoteSha = code == 0 && IsCommitSha(stdout.Trim()) ? stdout.Trim() : new string('0', sha.Length);

        if (!await _host.IsRunCloneAsync(worktreePath, ct))
            return $"{worktreePath} is not a git clone or worktree created by ILD";

        // The line goes in through stdin, so no file exists anywhere. git reopens its stdin by path
        // (/dev/stdin), and the pipe ILD makes belongs to ILD's uid, which the agent cannot open; so the
        // agent's own shell makes the pipe git reads, from a cat that already holds ILD's end open.
        var script = """
            cat | exec git -C "$1" hook run --ignore-missing --to-stdin=/dev/stdin pre-push -- origin "$2"
            """;
        using var line = new MemoryStream(Encoding.UTF8.GetBytes($"{reference} {sha} {reference} {remoteSha}\n"));
        var result = await _host.RunToolAsync(
            worktreePath, "/bin/sh", ["-c", script, "sh", Path.GetFullPath(worktreePath), WithoutUserInfo(auth.RemoteUrl)],
            new RunToolStreams(StandardInputFrom: line), ct);
        if (result.Success) return null;

        var output = (string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr).Trim();
        return $"pre-push hook failed: {(output.Length > HookOutputTail ? output[^HookOutputTail..] : output)}";
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

    public async Task<string?> GetHeadCommitAsync(string worktreePath)
    {
        var (code, stdout, _) = await RunAsync(worktreePath, "rev-parse", "HEAD");
        return code == 0 && stdout.Trim() is { Length: > 0 } sha ? sha : null;
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
        await using var stream = await _host.OpenReadAsync(full);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync();
    }

    public async Task<IReadOnlyList<WorktreeFileEntry>> ListWorktreeFilesAsync(string worktreePath, string? defaultBranch = null, WorktreeDiffRange? range = null)
    {
        RequireCommitShas(range);
        if (!await ValidateWorktreeHealthAsync(worktreePath))
            return Array.Empty<WorktreeFileEntry>();

        var baseRef = range?.From ?? await ResolveDiffBaseAsync(worktreePath, defaultBranch);

        // Present files (tracked + untracked), .gitignore honoured. Start every
        // one at "none"; the diff below promotes the ones that actually changed.
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in await ListZeroSeparatedAsync(worktreePath, "ls-files", "--cached", "--others", "--exclude-standard", "-z"))
            statuses[path] = "none";

        // Tracked changes against the fork point (or the range's start) —
        // working tree vs base, so uncommitted edits show up too, unless the
        // range ends at a commit. Parse the NUL-delimited name-status
        // stream; renames/copies emit a source and a destination path.
        var (code, diffOut, _) = await RunAsync(worktreePath, ["diff", "--name-status", "-z", .. DiffRevisions(baseRef, range)]);
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

        // git diff ignores untracked files, so tag them explicitly — unless the
        // range ends at a commit, which nothing untracked is part of.
        if (range?.To == null)
        {
            foreach (var path in await ListZeroSeparatedAsync(worktreePath, "ls-files", "--others", "--exclude-standard", "-z"))
                statuses[path] = "added";
        }
        else
        {
            // Nor is a deletion not yet committed: a file gone from disk that
            // the range left alone has nothing to show, and reading it is a 404.
            foreach (var path in await ListZeroSeparatedAsync(worktreePath, "ls-files", "--deleted", "-z"))
            {
                if (statuses.GetValueOrDefault(path) == "none")
                    statuses.Remove(path);
            }
        }

        return statuses
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new WorktreeFileEntry { Path = kv.Key, ChangeStatus = kv.Value })
            .ToList();
    }

    public async Task<WorktreeFileContentResponse?> ReadWorktreeFileAsync(string worktreePath, string relativePath, string? defaultBranch = null, WorktreeDiffRange? range = null)
    {
        RequireCommitShas(range);
        var full = ResolveSafePath(worktreePath, relativePath);
        if (full == null) return null;

        var bytes = await ReadOrExplainAsync(relativePath, () => ReadFileBytesAsync(full));

        var baseRef = range?.From ?? await ResolveDiffBaseAsync(worktreePath, defaultBranch);
        var revisions = DiffRevisions(baseRef, range);

        var status = "none";
        string? diff = null;

        var (nsCode, ns, _) = await RunAsync(worktreePath, ["diff", "--name-status", "-z", .. revisions, "--", relativePath]);
        var nsTokens = ns.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (nsCode == 0 && nsTokens.Length > 0 && nsTokens[0].Length > 0)
        {
            status = MapDiffStatus(nsTokens[0][0]);
            var (_, patch, _) = await RunAsync(worktreePath, ["diff", .. revisions, "--", relativePath]);
            diff = string.IsNullOrEmpty(patch) ? null : patch;
        }

        var exists = bytes is not null;
        if (status == "none" && exists && range?.To == null)
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

        if (bytes is not null)
        {
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

    private async Task<byte[]?> ReadFileBytesAsync(string full)
    {
        await using var stream = await _host.OpenFileForReadAsync(full);
        if (stream is null) return null;
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        return bytes.ToArray();
    }

    public async Task<Stream?> OpenWorktreeFileAsync(string worktreePath, string relativePath)
    {
        var full = ResolveSafePath(worktreePath, relativePath);
        if (full == null) return null;

        return await ReadOrExplainAsync(relativePath, () => _host.OpenFileForReadAsync(full));
    }

    public async Task<WorktreeFileWriteResult> WriteWorktreeFileAsync(string worktreePath, string relativePath, string content, string? defaultBranch = null, WorktreeDiffRange? range = null)
    {
        RequireCommitShas(range);
        if (!await ValidateWorktreeHealthAsync(worktreePath))
            return WorktreeFileWriteResult.WorktreeUnavailable;

        // A path that leads out of the worktree names nothing in it, which is
        // the same answer as one that is simply not there — and the same one the
        // read side gives, so neither endpoint tells a caller which it was.
        var full = ResolveSafePath(worktreePath, relativePath);
        if (full == null)
            return WorktreeFileWriteResult.NotFound;

        try
        {
            // One handle does the deciding and the writing, so the file sniffed
            // is the file written — asking whether it exists first and acting on
            // the answer afterwards leaves a gap that a delete fits through, and
            // opening for create rather than open would fill that gap by putting
            // the file back. A delete that beats this open is a refusal; one that
            // races the write takes the bytes with it, as it would for any writer.
            await using var file = await _host.OpenFileForWriteAsync(full);
            if (await IsBinaryAsync(file))
                return WorktreeFileWriteResult.NotText;

            file.SetLength(0);
            file.Position = 0;
            // UTF-8 without a byte order mark, as File.WriteAllTextAsync wrote:
            // a mark added here would show up as a change to the file's first
            // line in every diff taken afterwards.
            await file.WriteAsync(System.Text.Encoding.UTF8.GetBytes(content));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gone, replaced by a directory, or otherwise not something this can
            // write — all of which is the same to a caller as nothing writable
            // standing there, and none of which is worth a 500.
            return WorktreeFileWriteResult.NotFound;
        }

        // The run's agent works in this same worktree, so the file can be gone
        // again before the read that describes it — a save cannot answer with a
        // file it no longer holds, and says what a read of that path would now.
        var written = await ReadWorktreeFileAsync(worktreePath, relativePath, defaultBranch, range);
        return written == null ? WorktreeFileWriteResult.NotFound : WorktreeFileWriteResult.Saved(written);
    }

    public async Task<WorktreeCommitsResponse> ListWorktreeCommitsAsync(string worktreePath, string? defaultBranch)
    {
        if (!await ValidateWorktreeHealthAsync(worktreePath))
            return new WorktreeCommitsResponse();

        var baseSha = await TryResolveMergeBaseAsync(worktreePath, defaultBranch);
        if (baseSha == null || !IsCommitSha(baseSha))
            return new WorktreeCommitsResponse();

        // First-parent only: a merged-in side branch is one step of the run,
        // not several, and the steps stay a chain each a parent of the next.
        var (code, stdout, _) = await RunAsync(worktreePath, "log", "--first-parent", "-z", "--format=%H%x1f%P%x1f%s", $"{baseSha}..HEAD");
        if (code != 0)
            return new WorktreeCommitsResponse();

        var commits = stdout
            .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(record => record.Split('\x1f', 3))
            .Select(fields => new WorktreeCommit
            {
                Sha = fields[0],
                ParentSha = fields.Length > 1 ? fields[1].Split(' ')[0] : string.Empty,
                Subject = fields.Length > 2 ? fields[2] : string.Empty,
            })
            .ToList();
        return new WorktreeCommitsResponse { BaseSha = baseSha, Commits = commits };
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
        // Nothing resolved (e.g. origin/HEAD unset in this worktree); keep the
        // literal ref so the caller's diff fails loudly rather than silently.
        return await TryResolveMergeBaseAsync(worktreePath, defaultBranch) ?? "origin/HEAD";
    }

    private async Task<string?> TryResolveMergeBaseAsync(string worktreePath, string? defaultBranch)
    {
        foreach (var candidate in EnumerateBaseRefCandidates(defaultBranch))
        {
            var (code, stdout, _) = await RunAsync(worktreePath, "merge-base", "HEAD", candidate);
            if (code == 0 && !string.IsNullOrWhiteSpace(stdout))
                return stdout.Trim();
        }

        return null;
    }

    private static string[] DiffRevisions(string from, WorktreeDiffRange? range)
        => range?.To is { } to ? [from, to] : [from];

    // The controller only admits SHAs git itself listed, so this never fires
    // from a request; it keeps anything else from reaching git as an option or
    // a ref name.
    private static void RequireCommitShas(WorktreeDiffRange? range)
    {
        if (range == null) return;
        if ((range.From != null && !IsCommitSha(range.From)) || (range.To != null && !IsCommitSha(range.To)))
            throw new ArgumentException("A diff range is made of full commit SHAs.", nameof(range));
    }

    /// <summary>The clone URL with any credentials in it removed.</summary>
    internal static string WithoutUserInfo(string cloneUrl)
        => Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0
            ? new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.AbsoluteUri
            : cloneUrl;

    internal static bool IsCommitSha(string value)
        => value.Length is 40 or 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

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

        var (code, stdout, _) = await LsRemoteAsync(["ls-remote", "--symref", "--", cloneUrl, "HEAD"], auth, cancellationToken);
        if (code != 0)
            return null;

        return new RemoteRepositoryInfo(ParseSymrefDefaultBranch(stdout), ParseRepoNameFromUrl(cloneUrl));
    }

    public async Task<bool?> RemoteHasBranchAsync(string cloneUrl, string branchName, CancellationToken cancellationToken = default, GitAuthOptions? auth = null)
    {
        if (string.IsNullOrWhiteSpace(cloneUrl) || string.IsNullOrWhiteSpace(branchName))
            return null;

        var (code, _, _) = await LsRemoteAsync(
            ["ls-remote", "--heads", "--exit-code", "--", cloneUrl, $"refs/heads/{branchName}"], auth, cancellationToken);

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

    public async Task<GitRemoteProbe> ProbeRemoteAsync(string cloneUrl, string? branch, CancellationToken cancellationToken = default, GitAuthOptions? auth = null)
    {
        var target = string.IsNullOrWhiteSpace(branch) ? "HEAD" : $"refs/heads/{branch}";

        var r = await LsRemoteAsync(["ls-remote", "--exit-code", "--", cloneUrl, target], auth, cancellationToken);
        // ls-remote matches its pattern as a glob against the tail of each ref,
        // so "main*" or "x/refs/heads/main" can satisfy "main": only the exact
        // ref in its listing counts as found, anything else is an answer without it.
        var found = r.ExitCode == 0 && ListsRef(r.StdOut, target);
        return new GitRemoteProbe(r.ExitCode == 0 && !found ? 2 : r.ExitCode, r.StdErr);
    }

    private static bool ListsRef(string lsRemoteOutput, string refName)
        => lsRemoteOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => line.Split('\t', 2) is [_, var name] && name == refName);

    // `git ls-remote --symref <url> HEAD` advertises the default branch as a
    // line like "ref: refs/heads/main\tHEAD"; pull the branch name out of it.
    internal static string? ParseSymrefDefaultBranch(string lsRemoteOutput)
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

    /// <summary>
    /// Where <paramref name="relativePath"/> actually lands inside
    /// <paramref name="worktreePath"/>, or null if that is outside it. Links are
    /// followed before the boundary is drawn, on both sides: a lexical check
    /// answers where a path was spelled, not where it leads, so a link planted
    /// in the worktree pointing out of it would otherwise be a way through —
    /// one the write side would follow, carrying the user's text with it.
    /// </summary>
    private static string? ResolveSafePath(string worktreePath, string relativePath)
    {
        var root = ResolveLinks(Path.GetFullPath(worktreePath));
        if (root == null) return null;
        var full = ResolveLinks(Path.GetFullPath(Path.Combine(root, relativePath)));
        if (full == null) return null;
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

    /// <summary>
    /// Whether <paramref name="stream"/> reads as binary from where it stands,
    /// without pulling it into memory: the sniff only ever looks at the head of
    /// a file, so only the head is read. Leaves the stream wherever the read
    /// left it, which a caller about to truncate does not care about.
    /// </summary>
    private static async Task<bool> IsBinaryAsync(Stream stream)
    {
        var head = new byte[BinarySniffBytes];
        var read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false);
        return IsBinary(read == head.Length ? head : head[..read]);
    }

    /// <summary>
    /// How far into a file the binary sniff looks. A NUL this side of it is what
    /// separates text from bytes; text that long without one is taken as text.
    /// </summary>
    private const int BinarySniffBytes = 8000;

    private static bool IsBinary(byte[] bytes)
    {
        var limit = Math.Min(bytes.Length, BinarySniffBytes);
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

    internal static string? TrustedRepositoryRoot(string path) => LocalWorktrees.TrustedRepositoryRoot(path);

    private static string? ResolveLinks(string path) => LocalWorktrees.ResolveLinks(path);

    private static string? LinkTargetOf(string path) => LocalWorktrees.LinkTargetOf(path);

    public Task<string?> ResolveBaseRepoPathAsync(string worktreePath)
        => Task.FromResult(TrustedRepositoryRoot(worktreePath));

    private Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string cwd, params string[] args)
        => RunAsync(cwd, args, CancellationToken.None);

    /// <summary>Runs <paramref name="read"/>; a denial is <see cref="WorktreeFileUnreadableException"/>.</summary>
    private static async Task<T?> ReadOrExplainAsync<T>(string relativePath, Func<Task<T?>> read)
        where T : class
    {
        try
        {
            return await read();
        }
        catch (UnauthorizedAccessException)
        {
            throw new WorktreeFileUnreadableException($"{relativePath} can't be read: permission denied");
        }
    }

    /// <summary>
    /// <c>git -C <paramref name="cwd"/></c>, agent-scoped. Refused, without starting git, for a directory
    /// whose <c>.git</c> pointer is not one <see cref="TrustedRepositoryRoot"/> accepts.
    /// </summary>
    private async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string cwd, IReadOnlyList<string> args, CancellationToken ct, Stream? stdoutTo = null, Stream? stdinFrom = null)
    {
        if (!await _host.IsRunCloneAsync(cwd, ct))
        {
            _logger?.LogWarning("Refused git {Args} in {Directory}: its .git is not a clone or worktree of ours", string.Join(' ', args), cwd);
            return (RefusedExitCode, string.Empty, $"{cwd} is not a git clone or worktree created by ILD");
        }

        return await AgentGitAsync(cwd, ["-C", Path.GetFullPath(cwd), .. args], ct, stdoutTo, stdinFrom);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> AgentGitAsync(
        string runPath, IReadOnlyList<string> args, CancellationToken ct, Stream? stdoutTo = null, Stream? stdinFrom = null)
    {
        // From "/" and addressing the directory with -C: the orchestrator never enters
        // one the agent owns.
        var r = await _host.RunToolAsync(runPath, "git", args, new RunToolStreams(stdinFrom, stdoutTo), ct);
        if (!r.Success)
            _logger?.LogDebug("git {Args} exited {Code}: {Err}", string.Join(' ', args), r.ExitCode, r.StdErr);
        return (r.ExitCode, r.StdOut, r.StdErr);
    }

    private Task<ProcessResult> LsRemoteAsync(IReadOnlyList<string> args, GitAuthOptions? auth, CancellationToken ct)
    {
        // The key is in this process's environment, so it must not discover a repository
        // whose config another user could have planted (/tmp is agent-writable): it runs in
        // the owner-only private root, and discovery may not climb out of it.
        var cwd = AgentIsolation.CreatePrivateDirectory();
        return OrchestratorGitAsync(cwd, args, auth, ct, Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(cwd)));
    }

    private static readonly (string Key, string Value)[] OrchestratorGitConfig =
    [
        ("core.hooksPath", "/dev/null"),
        ("core.fsmonitor", "false"),
        ("credential.helper", ""),
        ("core.sshCommand", ""),
        ("protocol.ext.allow", "never"),
        ("gc.autoDetach", "false"),
    ];

    /// <summary>
    /// git as the orchestrator, for its own mirror and the remote. Nothing is inherited but
    /// the orchestrator's own proxy variables: not the rest of its environment, not the
    /// system or user configuration; only the credential when the caller has one.
    /// </summary>
    private async Task<ProcessResult> OrchestratorGitAsync(
        string cwd, IReadOnlyList<string> args, GitAuthOptions? auth, CancellationToken ct,
        string? ceilingDirectories = null, Stream? standardInput = null)
    {
        var home = AgentIsolation.CreatePrivateDirectory("git-home");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin",
            ["HOME"] = home,
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            ["GIT_CONFIG_COUNT"] = OrchestratorGitConfig.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        for (var i = 0; i < OrchestratorGitConfig.Length; i++)
        {
            environment[$"GIT_CONFIG_KEY_{i}"] = OrchestratorGitConfig[i].Key;
            environment[$"GIT_CONFIG_VALUE_{i}"] = OrchestratorGitConfig[i].Value;
        }
        foreach (var key in ProxyEnvironmentKeys)
            if (_readEnvironment(key) is { } proxy)
                environment[key] = proxy;
        if (ceilingDirectories is not null)
            environment["GIT_CEILING_DIRECTORIES"] = ceilingDirectories;
        foreach (var (key, value) in BuildGitEnvironment(auth) ?? new Dictionary<string, string?>())
            environment[key] = value;

        var r = await _runner.RunAsync("git", args, cwd, ct, environment, new ProcessRunOptions(CleanEnvironment: true, StandardInputFrom: standardInput));
        if (!r.Success)
            _logger?.LogDebug("git {Args} exited {Code}: {Err}", string.Join(' ', args), r.ExitCode, r.StdErr);
        return r;
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

    /// <summary>
    /// The Basic credential git sends for <paramref name="auth"/> (askpass answers
    /// the username and key from <see cref="BuildGitEnvironment"/>), or null when
    /// git sends none — the form a server echoing request headers would show.
    /// </summary>
    internal static string? GitBasicCredential(GitAuthOptions? auth)
        => auth == null || string.IsNullOrWhiteSpace(auth.ApiKey)
            ? null
            : Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ResolveGitUsername(auth.ProviderType, auth.RemoteUrl)}:{auth.ApiKey}"));

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
            // Azure DevOps reads the PAT from the password and ignores the
            // username, but git still needs a non-blank one to send at all.
            "azuredevops" => "pat",
            _ => "git",
        };
    }

    /// <summary>
    /// Materialize the helper git calls as <c>GIT_ASKPASS</c> to feed it the
    /// repository token. It lives under the orchestrator-private root, never a
    /// fixed path in world-writable <c>/tmp</c>: this script is executed by the
    /// orchestrator with <c>ILD_GIT_PASSWORD</c> in its environment, so a version
    /// planted by someone else would be both arbitrary code as the orchestrator and
    /// credential exfiltration — and the "only write it if it is missing" guard below
    /// is exactly what would trust the planted copy. See <see cref="AgentIsolation.PrivateRoot"/>.
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
