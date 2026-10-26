using System.Security.Cryptography;
using System.Text;
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
/// the repository token in its environment. Objects reach the remote only by way of
/// the mirror: fetched from it, or handed to it as a bundle the agent wrote.
///
/// <para>
/// A mirror belongs to the orchestrator: nothing in it is writable by the agent. It is
/// closed to the agent while the orchestrator's git is writing to it, and opened again
/// only once what git wrote has been made unwritable, so a file is never reachable by
/// the agent with the mode git gave it. It keeps no remote and no credential, and a
/// mirror that is lost costs a refetch.
/// </para>
///
/// <para>
/// Who may read a mirror is the group of the store root: where uids are isolated the
/// container entrypoint makes it a setgid, read-only tree of the shared group, so every
/// directory below it is created in that group and the setgid bit is kept when modes
/// are tightened.
/// </para>
/// </summary>
internal sealed class GitMirror
{
    private const UnixFileMode Closed = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode Readable = Closed | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
    private const UnixFileMode FileCeiling = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
    private const string StagingRef = "refs/ild-push/staged";

    private static readonly SharedEntries<string, SemaphoreSlim> Locks = new(() => new SemaphoreSlim(1, 1));

    private readonly string _root;
    private readonly bool _sharedWithAgent;
    private readonly OrchestratorGit _git;
    private readonly ILogger? _logger;

    /// <param name="sharedWithAgent">Whether another uid, the agent, is to read the mirrors.</param>
    public GitMirror(string root, bool sharedWithAgent, OrchestratorGit git, ILogger? logger = null)
    {
        _root = root;
        _sharedWithAgent = sharedWithAgent;
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
        if (!Directory.Exists(_root))
            CreateDirectory(_root, _sharedWithAgent ? Readable : Closed);

        if (File.Exists(Path.Combine(path, "HEAD")))
        {
            SetMode(path, Closed);
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
                return init.StdErr;
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

    /// <summary>Set the permission bits of a directory without losing the setgid bit it inherited.</summary>
    private static void SetMode(string directory, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(directory, mode | (File.GetUnixFileMode(directory) & UnixFileMode.SetGroup));
    }

    /// <summary>Strip the mode of everything git wrote to what the agent may read, never write.</summary>
    private static void Sanitize(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            try
            {
                if (entry.LinkTarget is not null) continue;
                var mode = File.GetUnixFileMode(entry.FullName);
                var allowed = entry is DirectoryInfo ? Readable | UnixFileMode.SetGroup : FileCeiling;
                if ((mode & ~allowed) != 0)
                    File.SetUnixFileMode(entry.FullName, mode & allowed);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // git removed it while we looked: a temporary file of an interrupted write.
            }
        }
    }

    internal sealed class Session : IDisposable
    {
        private readonly GitMirror _owner;
        private readonly SemaphoreSlim _gate;
        private bool _dirty = true;
        private bool _open;

        public Session(GitMirror owner, string path, SemaphoreSlim gate)
        {
            _owner = owner;
            _gate = gate;
            Path = path;
        }

        public string Path { get; }

        public async Task<ProcessResult> GitAsync(IReadOnlyList<string> args, GitAuthOptions? auth, CancellationToken ct, Stream? standardInput = null)
        {
            _dirty = true;
            if (_open)
            {
                SetMode(Path, Closed);
                _open = false;
            }
            return await _owner._git(Path, args, auth, ct, standardInput);
        }

        /// <summary>Bring every branch of the remote into the mirror, and make <paramref name="defaultBranch"/>, when known, its HEAD.</summary>
        public async Task<(bool Ok, string? Error)> FetchAsync(GitAuthOptions auth, string? defaultBranch, CancellationToken ct)
        {
            var fetch = await GitAsync(
                ["fetch", "--quiet", "--no-write-fetch-head", "--prune", "--", auth.RemoteUrl, "+refs/heads/*:refs/heads/*"], auth, ct);
            if (!fetch.Success)
                return (false, fetch.StdErr);

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
                    _owner._logger?.LogWarning("The mirror could not record {Branch} at {Sha}: {Error}", branch, sha, track.StdErr.Trim());
            }
            if (imported)
                await GitAsync(["update-ref", "-d", StagingRef], null, ct);
            return push.Success ? (true, null) : (false, push.StdErr);
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
                        written = await createBundle(incremental, sink);
                    }
                    if (!written.Success)
                        return (false, written.StdErr, null);
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
                    return (false, index.StdErr, null);

                var staged = await GitAsync(["update-ref", StagingRef, tip], null, ct);
                return staged.Success ? (true, null, tip) : (false, staged.StdErr, null);
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
        /// Run <paramref name="body"/> with the mirror open to the agent for reading. Whatever git has written since
        /// the agent last could look is first made unwritable; the mirror stays open until the orchestrator's git
        /// next writes to it.
        /// </summary>
        public async Task<T> ReadableByAgentAsync<T>(Func<string, Task<T>> body, CancellationToken ct)
        {
            if (_dirty)
            {
                Sanitize(Path);
                _dirty = false;
            }

            if (_owner._sharedWithAgent)
            {
                SetMode(Path, Readable);
                _open = true;
            }
            return await body(Path);
        }

        public void Dispose()
        {
            try
            {
                if (_dirty)
                    Sanitize(Path);
                if (_owner._sharedWithAgent && !_open)
                {
                    SetMode(Path, Readable);
                    _open = true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _owner._logger?.LogWarning(ex, "Could not tighten the modes of the mirror {Mirror}; the next operation on it does", Path);
            }
            finally
            {
                Release(Path, _gate);
            }
        }
    }
}
