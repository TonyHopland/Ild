using ILD.Core.Services.RunHosts.Local;
using System.Security.Cryptography;
using System.Text;
using ILD.Core.Services.Implementations.Storage;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>Runs git as the orchestrator in <paramref name="directory"/>, with a clean configuration and environment.</summary>
internal delegate Task<ProcessResult> OrchestratorGit(
    string directory, IReadOnlyList<string> args, GitAuthOptions? auth, CancellationToken ct, Stream? standardInput = null);

/// <summary>
/// The per-remote bare repositories the orchestrator talks to remotes through
/// (ADR-0014). The orchestrator runs git only in a mirror, never in a clone or
/// worktree the agent can write, so nothing the agent plants in those can run with
/// the repository token in its environment. Objects travel between the remote and
/// the agent only by way of the mirror, and only as bundles: the orchestrator writes
/// a bundle from the mirror for the agent to clone or fetch from (<see cref="Session.BundleForAsync"/>),
/// and the agent's bundle is imported into the mirror before anything is pushed.
///
/// <para>
/// A mirror belongs to the orchestrator alone: the store and every mirror in it are
/// owner-only, so the agent has no access to it at all and needs none. It keeps no
/// remote and no credential, and a mirror that is lost costs a refetch.
/// </para>
/// </summary>
internal sealed class GitMirror
{
    private const UnixFileMode Closed = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const string StagingRef = "refs/ild-push/staged";

    private static readonly SharedEntries<string, SemaphoreSlim> Locks = new(() => new SemaphoreSlim(1, 1));

    private readonly string _root;
    private readonly OrchestratorGit _git;
    private readonly ILogger? _logger;

    /// <summary>The largest bundle taken from a run: past it the bundle is refused before anything is indexed.</summary>
    public long MaxBundleBytes { get; init; } = RunHosts.RunReadLimits.Default.MaxBundleBytes;

    public GitMirror(string root, OrchestratorGit git, ILogger? logger = null)
    {
        _root = root;
        _git = git;
        _logger = logger;
    }

    /// <summary>Where the mirror of <paramref name="cloneUrl"/> lives, keyed by the URL without credentials.</summary>
    public string PathFor(string cloneUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(RepositoryManager.WithoutUserInfo(cloneUrl)));
        return Path.Combine(_root, Convert.ToHexStringLower(hash));
    }

    /// <summary>
    /// The mirror of <paramref name="cloneUrl"/>, made if it is not there yet, held
    /// by the caller until the session is disposed: every operation on one mirror is
    /// serialized, agent reads included.
    /// </summary>
    public async Task<(Session? Session, string? Error)> OpenAsync(string cloneUrl, CancellationToken ct)
    {
        var path = PathFor(cloneUrl);
        var gate = Locks.Enter(path);
        try
        {
            await gate.WaitAsync(ct);
        }
        catch
        {
            Locks.Leave(path);
            throw;
        }

        string? error;
        try
        {
            error = await EnsureAsync(path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        catch
        {
            Release(path, gate);
            throw;
        }

        if (error is not null)
        {
            Release(path, gate);
            return (null, error);
        }

        return (new Session(this, path, gate), null);
    }

    private static void Release(string path, SemaphoreSlim gate)
    {
        gate.Release();
        Locks.Leave(path);
    }

    private async Task<string?> EnsureAsync(string path, CancellationToken ct)
    {
        // Closed every time it is opened, so a store an earlier version shared with the agent is closed on upgrade.
        if (!Directory.Exists(_root))
            CreateDirectory(_root, Closed);
        Close(_root);

        if (File.Exists(Path.Combine(path, "HEAD")))
        {
            Close(path);
            return null;
        }

        // Built beside its place and renamed into it, so a mirror that exists is a
        // whole one however init is interrupted; what an interrupted one left is cleared.
        var name = Path.GetFileName(path);
        foreach (var leftover in Directory.GetDirectories(_root, $"{name}*"))
            Directory.Delete(leftover, recursive: true);

        var building = $"{path}.building-{Guid.NewGuid():N}";
        try
        {
            CreateDirectory(building, Closed);
            var init = await _git(_root, ["init", "--quiet", "--bare", "--template=", "--initial-branch=main", building], null, ct);
            if (!init.Success)
                return RepositoryManager.RedactUserInfo(init.StdErr);
            Directory.Move(building, path);
        }
        finally
        {
            if (Directory.Exists(building))
                Directory.Delete(building, recursive: true);
        }

        return null;
    }

    private static void CreateDirectory(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, mode);
    }

    private static void Close(string directory)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(directory, Closed);
    }

    internal sealed class Session : IDisposable
    {
        private readonly GitMirror _owner;
        private readonly SemaphoreSlim _gate;

        public Session(GitMirror owner, string path, SemaphoreSlim gate)
        {
            _owner = owner;
            _gate = gate;
            Path = path;
        }

        public string Path { get; }

        public async Task<ProcessResult> GitAsync(IReadOnlyList<string> args, GitAuthOptions? auth, CancellationToken ct, Stream? standardInput = null)
        {
            return await _owner._git(Path, args, auth, ct, standardInput);
        }

        /// <summary>Bring every branch of the remote into the mirror, and make <paramref name="defaultBranch"/>, when known, its HEAD.</summary>
        public async Task<(bool Ok, string? Error)> FetchAsync(GitAuthOptions auth, string? defaultBranch, CancellationToken ct)
        {
            var fetch = await GitAsync(
                ["fetch", "--quiet", "--no-write-fetch-head", "--prune", "--", auth.RemoteUrl, "+refs/heads/*:refs/heads/*"], auth, ct);
            if (!fetch.Success)
                return (false, RepositoryManager.RedactUserInfo(fetch.StdErr));

            if (defaultBranch is not null)
                await GitAsync(["symbolic-ref", "HEAD", $"refs/heads/{defaultBranch}"], null, ct);
            return (true, null);
        }

        /// <summary>
        /// Push the commit <paramref name="sha"/> of the agent's <paramref name="branch"/> to the remote, as that
        /// branch. A commit the mirror does not have yet is taken from the bundle <paramref name="createBundle"/>
        /// writes (first only what the agent's remote-tracking refs lack, then everything).
        /// </summary>
        public async Task<(bool Ok, string? Error)> PushAsync(
            GitAuthOptions auth, string branch, string sha, Func<bool, Stream, Task<ProcessResult>> createBundle, CancellationToken ct)
        {
            var imported = false;
            if (!await HasCommitAsync(sha, ct))
            {
                var import = await ImportAsync(branch, true, createBundle, ct);
                if (!import.Ok)
                    import = await ImportAsync(branch, false, createBundle, ct);
                if (!import.Ok)
                    return (false, import.Error);
                sha = import.Sha!;
                imported = true;
            }

            var push = await GitAsync(["push", "--quiet", "--", auth.RemoteUrl, $"{sha}:refs/heads/{branch}"], auth, ct);
            if (push.Success)
            {
                var track = await GitAsync(["update-ref", $"refs/heads/{branch}", sha], null, ct);
                if (!track.Success)
                    _owner._logger?.LogWarning("The mirror could not record {Branch} at {Sha}: {Error}", branch, sha, RepositoryManager.RedactUserInfo(track.StdErr.Trim()));
            }
            if (imported)
                await GitAsync(["update-ref", "-d", StagingRef], null, ct);
            return push.Success ? (true, null) : (false, RepositoryManager.RedactUserInfo(push.StdErr));
        }

        private async Task<bool> HasCommitAsync(string sha, CancellationToken ct)
            => (await GitAsync(["cat-file", "-e", $"{sha}^{{commit}}"], null, ct)).Success;

        /// <summary>
        /// Take the branch out of the bundle the agent writes. The bundle is the agent's output, so it lives only
        /// in a file of ours the agent cannot reach, its objects are checked one by one as they are indexed, and
        /// it is gone before anything else is done with what came out of it.
        /// </summary>
        private async Task<(bool Ok, string? Error, string? Sha)> ImportAsync(
            string branch, bool incremental, Func<bool, Stream, Task<ProcessResult>> createBundle, CancellationToken ct)
        {
            var file = System.IO.Path.Combine(AgentIsolation.CreatePrivateDirectory("git-bundles"), $"{Guid.NewGuid():N}.bundle");
            try
            {
                try
                {
                    ProcessResult written;
                    var create = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                    if (!OperatingSystem.IsWindows())
                        create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    await using (var sink = new FileStream(file, create))
                    {
                        written = await createBundle(incremental, new RunHosts.CappedWriteStream(sink, _owner.MaxBundleBytes));
                    }
                    if (!written.Success)
                        return (false, RepositoryManager.RedactUserInfo(written.StdErr), null);
                }
                catch (RunHosts.ReadLimitExceededException)
                {
                    return (false, $"the bundle is larger than {_owner.MaxBundleBytes / RunHosts.RunReadLimits.Megabyte} MB", null);
                }
                catch (IOException ex)
                {
                    return (false, ex.Message, null);
                }

                // Not `git fetch <bundle>`: git before 2.46 does not apply transfer.fsckObjects to a
                // bundle, so the pack is indexed by hand, with its objects checked.
                await using var bundle = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                var (tip, prerequisites) = await ReadBundleHeaderAsync(bundle, $"refs/heads/{branch}", ct);
                if (tip is null)
                    return (false, "the bundle does not hold the branch", null);
                foreach (var prerequisite in prerequisites)
                {
                    if (!await HasCommitAsync(prerequisite, ct))
                        return (false, $"the mirror lacks {prerequisite}, which the bundle builds on", null);
                }

                var index = await GitAsync(["index-pack", "--stdin", "--fix-thin", "--fsck-objects"], null, ct, bundle);
                if (!index.Success)
                    return (false, RepositoryManager.RedactUserInfo(index.StdErr), null);

                var staged = await GitAsync(["update-ref", StagingRef, tip], null, ct);
                return staged.Success ? (true, null, tip) : (false, RepositoryManager.RedactUserInfo(staged.StdErr), null);
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>
        /// The commit the bundle names for <paramref name="reference"/> and the commits it builds on, leaving
        /// <paramref name="bundle"/> at the start of its pack; no tip when the file is not a bundle that names it.
        /// </summary>
        private static async Task<(string? Tip, List<string> Prerequisites)> ReadBundleHeaderAsync(
            Stream bundle, string reference, CancellationToken ct)
        {
            string? tip = null;
            var prerequisites = new List<string>();
            var first = true;
            while (await ReadLineAsync(bundle, ct) is { } line)
            {
                if (first && !line.StartsWith("# v", StringComparison.Ordinal))
                    return (null, prerequisites);
                first = false;
                if (line.Length == 0)
                    return (tip, prerequisites);
                if (line[0] is '#' or '@')
                    continue;
                if (line[0] == '-')
                {
                    var id = line[1..].Split(' ', 2)[0];
                    if (!RepositoryManager.IsCommitSha(id))
                        return (null, prerequisites);
                    prerequisites.Add(id);
                    continue;
                }

                var space = line.IndexOf(' ');
                if (space > 0 && line[(space + 1)..] == reference && RepositoryManager.IsCommitSha(line[..space]))
                    tip = line[..space];
            }

            return (null, prerequisites);
        }

        private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
        {
            const int MaxHeaderLine = 4096;
            var bytes = new List<byte>();
            var one = new byte[1];
            while (await stream.ReadAsync(one, ct) == 1)
            {
                if (one[0] == '\n')
                    return Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add(one[0]);
                if (bytes.Count > MaxHeaderLine)
                    return null;
            }

            return null;
        }

        /// <summary>
        /// What the mirror holds, as a bundle the run can clone or fetch from. <paramref name="runHas"/> are commits the
        /// run already has; they come from the agent, so only those the mirror really holds as commits are used. The
        /// bundle covers only what the run lacks and falls back to everything when git cannot write that; when the run
        /// lacks nothing there is no bundle at all, because git refuses an empty one. Branches whose tips the run has
        /// are left out of a narrow bundle, so <see cref="MirrorBundle.Branches"/> is the whole truth about the mirror.
        /// </summary>
        public async Task<(MirrorBundle? Bundle, string? Error)> BundleForAsync(IReadOnlyCollection<string> runHas, CancellationToken ct)
        {
            var list = await GitAsync(["for-each-ref", "--format=%(objectname) %(refname)", "refs/heads"], null, ct);
            if (!list.Success)
                return (null, RepositoryManager.RedactUserInfo(list.StdErr));
            var branches = new List<MirrorBranch>();
            foreach (var line in list.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var space = line.IndexOf(' ');
                if (space > 0 && RepositoryManager.IsCommitSha(line[..space]) && line[(space + 1)..].StartsWith("refs/heads/", StringComparison.Ordinal))
                    branches.Add(new MirrorBranch(line[(space + 1 + "refs/heads/".Length)..], line[..space]));
            }

            var head = await GitAsync(["symbolic-ref", "-q", "HEAD"], null, ct);
            var headBranch = head.Success && head.StdOut.Trim() is var target && target.StartsWith("refs/heads/", StringComparison.Ordinal)
                && branches.Any(b => b.Name == target["refs/heads/".Length..])
                ? target["refs/heads/".Length..]
                : null;

            var haves = await HeldCommitsAsync(runHas, ct);
            if (branches.Count == 0 || branches.All(b => haves.Contains(b.Sha)))
                return (new MirrorBundle(branches, headBranch, null, false), null);

            string? error = null;
            foreach (var narrow in haves.Count > 0 ? new[] { true, false } : [false])
            {
                var file = System.IO.Path.Combine(AgentIsolation.CreatePrivateDirectory("git-bundles"), $"{Guid.NewGuid():N}.bundle");
                try
                {
                    await using var excluded = narrow
                        ? new MemoryStream(Encoding.ASCII.GetBytes(string.Concat(haves.Select(h => $"^{h}\n"))))
                        : null;
                    var made = await GitAsync(["bundle", "create", "--quiet", file, .. headBranch is not null ? new[] { "HEAD" } : [], "--branches", .. narrow ? new[] { "--stdin" } : []], null, ct, excluded);
                    if (made.Success)
                    {
                        if (!OperatingSystem.IsWindows())
                            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                        return (new MirrorBundle(branches, headBranch, stream, narrow), null);
                    }
                    error = RepositoryManager.RedactUserInfo(made.StdErr);
                }
                catch
                {
                    File.Delete(file);
                    throw;
                }
                File.Delete(file);
            }

            return (null, error);
        }

        /// <summary>Those of <paramref name="candidates"/> that are well-formed commit ids the mirror holds as commits.</summary>
        private async Task<HashSet<string>> HeldCommitsAsync(IReadOnlyCollection<string> candidates, CancellationToken ct)
        {
            var wanted = candidates.Where(RepositoryManager.IsCommitSha).Distinct().ToList();
            var held = new HashSet<string>(StringComparer.Ordinal);
            if (wanted.Count == 0)
                return held;

            await using var input = new MemoryStream(Encoding.ASCII.GetBytes(string.Concat(wanted.Select(w => $"{w}\n"))));
            var check = await GitAsync(["cat-file", "--batch-check=%(objectname) %(objecttype)"], null, ct, input);
            if (!check.Success)
                return held;
            foreach (var line in check.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var space = line.IndexOf(' ');
                if (space > 0 && line[(space + 1)..].TrimEnd() == "commit")
                    held.Add(line[..space]);
            }
            return held;
        }

        public void Dispose() => Release(Path, _gate);
    }
}

/// <summary>A branch of a mirror.</summary>
internal sealed record MirrorBranch(string Name, string Sha);

/// <summary>
/// What a mirror holds for a run: its branches and the one HEAD names, and a bundle with the objects the run lacks
/// (null when it lacks none). The bundle is a file of the orchestrator's, removed when this is disposed.
/// </summary>
internal sealed class MirrorBundle(IReadOnlyList<MirrorBranch> branches, string? headBranch, Stream? bundle, bool narrow) : IAsyncDisposable
{
    public IReadOnlyList<MirrorBranch> Branches { get; } = branches;
    public string? HeadBranch { get; } = headBranch;
    public Stream? Bundle { get; } = bundle;
    /// <summary>Whether the bundle leaves out what the run was said to have.</summary>
    public bool Narrow { get; } = narrow;

    public ValueTask DisposeAsync() => Bundle?.DisposeAsync() ?? ValueTask.CompletedTask;
}
