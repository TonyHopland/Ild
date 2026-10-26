using System.Security.Cryptography;
using System.Text;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>Runs git as the orchestrator in <paramref name="directory"/>, with a clean configuration and environment.</summary>
internal delegate Task<ProcessResult> OrchestratorGit(
    string directory, IReadOnlyList<string> args, GitAuthOptions? auth, CancellationToken ct);

/// <summary>
/// The per-remote bare repositories the orchestrator talks to remotes through
/// (ADR-0014). The orchestrator runs git only in a mirror, never in a clone or
/// worktree the agent can write, so nothing the agent plants in those can run with
/// the repository token in its environment. Objects reach the remote only by way of
/// the mirror: fetched from it, or handed to it as a bundle the agent wrote.
///
/// <para>
/// A mirror belongs to the orchestrator: nothing in it is writable by the agent, and
/// the agent can enter it only while a <see cref="Session"/> says so, so a file git
/// has just written is never reachable before its mode is made safe. It keeps no
/// remote and no credential, and a mirror that is lost costs a refetch.
/// </para>
/// </summary>
internal sealed class GitMirror
{
    private const UnixFileMode Closed = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode Traversable = Closed | UnixFileMode.GroupExecute;
    private const UnixFileMode Readable = Closed | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
    private const UnixFileMode FileCeiling = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
    private const string StagingRef = "refs/ild-push/staged";

    private static readonly SharedEntries<string, SemaphoreSlim> Locks = new(() => new SemaphoreSlim(1, 1));

    private readonly string root;
    private readonly AgentIdentity? agent;
    private readonly IProcessRunner runner;
    private readonly OrchestratorGit git;
    private readonly ILogger? logger;

    public GitMirror(string root, AgentIdentity? agent, IProcessRunner runner, OrchestratorGit git, ILogger? logger = null)
    {
        this.root = root;
        this.agent = agent;
        this.runner = runner;
        this.git = git;
        this.logger = logger;
    }

    /// <summary>Where the mirror of <paramref name="cloneUrl"/> lives, keyed by the URL without credentials.</summary>
    public string PathFor(string cloneUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(WithoutUserInfo(cloneUrl)));
        return Path.Combine(root, Convert.ToHexStringLower(hash));
    }

    private static string WithoutUserInfo(string cloneUrl)
        => Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0
            ? new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.AbsoluteUri
            : cloneUrl;

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

        try
        {
            if (await EnsureAsync(path, ct) is { } error)
            {
                gate.Release();
                Locks.Leave(path);
                return (null, error);
            }
        }
        catch
        {
            gate.Release();
            Locks.Leave(path);
            throw;
        }

        return (new Session(this, path, gate), null);
    }

    private async Task<string?> EnsureAsync(string path, CancellationToken ct)
    {
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
            if (agent is not null)
                await ChangeGroupAsync(root, recursive: false, ct);
            File.SetUnixFileMode(root, agent is null ? Closed : Traversable);
        }

        if (File.Exists(Path.Combine(path, "HEAD")))
        {
            File.SetUnixFileMode(path, Closed);
            return null;
        }

        // Built beside its place and renamed into it, so a mirror that exists is a
        // whole one however init is interrupted.
        var building = $"{path}.building-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(building, Closed);
            var init = await git(root, ["init", "--quiet", "--bare", "--template=", "--initial-branch=main", building], null, ct);
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

    private async Task ChangeGroupAsync(string path, bool recursive, CancellationToken ct)
    {
        string[] args = recursive ? ["-R", agent!.Group, path] : [agent!.Group, path];
        var result = await runner.RunAsync("chgrp", args, ct: ct);
        if (!result.Success)
            logger?.LogWarning("Could not give {Path} to group {Group}; the agent may not be able to read it: {Error}", path, agent.Group, result.StdErr.Trim());
    }

    /// <summary>Strip the mode of everything git wrote to what the agent may read, never write.</summary>
    private static void Sanitize(string path)
    {
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if (entry.LinkTarget is not null) continue;
            var mode = File.GetUnixFileMode(entry.FullName);
            var allowed = entry is DirectoryInfo ? Readable : FileCeiling;
            if ((mode & ~allowed) != 0)
                File.SetUnixFileMode(entry.FullName, mode & allowed);
        }
    }

    internal sealed class Session(GitMirror owner, string path, SemaphoreSlim gate) : IDisposable
    {
        private bool _dirty = true;

        public string Path { get; } = path;

        public async Task<ProcessResult> GitAsync(IReadOnlyList<string> args, GitAuthOptions? auth, CancellationToken ct)
        {
            _dirty = true;
            return await owner.git(Path, args, auth, ct);
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
        /// branch. A commit the mirror does not have yet is fetched from the bundle
        /// <paramref name="createBundle"/> writes (first only what the agent's remote-tracking refs lack, then
        /// everything).
        /// </summary>
        public async Task<(bool Ok, string? Error)> PushAsync(
            GitAuthOptions auth, string branch, string sha, Func<bool, Stream, Task<ProcessResult>> createBundle, CancellationToken ct)
        {
            if (!await HasCommitAsync(sha, ct))
            {
                var imported = await ImportAsync(branch, true, createBundle, ct);
                if (!imported.Ok)
                    imported = await ImportAsync(branch, false, createBundle, ct);
                if (!imported.Ok)
                    return (false, imported.Error);
                sha = imported.Sha!;
            }

            var push = await GitAsync(["push", "--quiet", "--", auth.RemoteUrl, $"{sha}:refs/heads/{branch}"], auth, ct);
            if (!push.Success)
                return (false, push.StdErr);

            var track = await GitAsync(["update-ref", $"refs/heads/{branch}", sha], null, ct);
            if (!track.Success)
                owner.logger?.LogWarning("The mirror could not record {Branch} at {Sha}: {Error}", branch, sha, track.StdErr.Trim());
            return (true, null);
        }

        private async Task<bool> HasCommitAsync(string sha, CancellationToken ct)
            => (await GitAsync(["cat-file", "-e", $"{sha}^{{commit}}"], null, ct)).Success;

        /// <summary>
        /// Fetch the branch out of the bundle the agent writes. The bundle is the agent's output, so it lives only
        /// in a file of ours the agent cannot reach, is checked object by object, and is gone before anything else
        /// is done with what came out of it.
        /// </summary>
        private async Task<(bool Ok, string? Error, string? Sha)> ImportAsync(
            string branch, bool incremental, Func<bool, Stream, Task<ProcessResult>> createBundle, CancellationToken ct)
        {
            var file = System.IO.Path.Combine(AgentIsolation.CreatePrivateDirectory("git-bundles"), $"{Guid.NewGuid():N}.bundle");
            try
            {
                ProcessResult written;
                await using (var sink = new FileStream(file, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                }))
                {
                    written = await createBundle(incremental, sink);
                }
                if (!written.Success)
                    return (false, written.StdErr, null);

                var fetch = await GitAsync(
                    ["-c", "transfer.fsckObjects=true", "fetch", "--quiet", "--no-write-fetch-head", "--", file, $"+refs/heads/{branch}:{StagingRef}"], null, ct);
                if (!fetch.Success)
                    return (false, fetch.StdErr, null);
            }
            finally
            {
                File.Delete(file);
            }

            var staged = await GitAsync(["rev-parse", "--verify", $"{StagingRef}^{{commit}}"], null, ct);
            return staged.Success ? (true, null, staged.StdOut.Trim()) : (false, staged.StdErr, null);
        }

        /// <summary>
        /// Run <paramref name="body"/> with the mirror open to the agent for reading. Whatever git has written since
        /// the agent last could look is first made unwritable and handed to the agent's group, and the mirror is
        /// closed to it again afterwards.
        /// </summary>
        public async Task<T> ReadableByAgentAsync<T>(Func<string, Task<T>> body, CancellationToken ct)
        {
            if (_dirty)
            {
                Sanitize(Path);
                if (owner.agent is not null)
                    await owner.ChangeGroupAsync(Path, recursive: true, ct);
                _dirty = false;
            }

            File.SetUnixFileMode(Path, owner.agent is null ? Closed : Readable);
            try
            {
                return await body(Path);
            }
            finally
            {
                File.SetUnixFileMode(Path, Closed);
            }
        }

        public void Dispose()
        {
            if (_dirty)
                Sanitize(Path);
            gate.Release();
            Locks.Leave(Path);
        }
    }
}
