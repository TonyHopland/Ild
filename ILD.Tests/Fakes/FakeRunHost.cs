using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.RunHosts;
using Porta.Pty;

namespace ILD.Tests.Fakes;

/// <summary>
/// A run host that lives in memory: files and directories in a dictionary, commands that follow a script,
/// and a record of what was started and killed. Executor tests use it to say what a command prints and how
/// it ends without a shell, and to look at what the executor asked the host for.
/// </summary>
internal sealed class FakeRunHost : IRunHost
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private readonly List<FakeRunProcess> _running = [];

    /// <summary>What the next command does; every command ends at once with code 0 and no output until this is set.</summary>
    public Func<RunCommand, FakeCommandScript> Script { get; set; } = _ => FakeCommandScript.Exits(0);

    public List<RunCommand> Started { get; } = [];
    public List<FakeRunProcess> Killed { get; } = [];
    public List<Guid> KillAllCalls { get; } = [];

    /// <summary>Runs as <c>KillAllAsync</c> is entered, for a test that checks what had happened by then.</summary>
    public Action<Guid>? OnKillAll { get; set; }

    /// <summary>Thrown by <c>KillAllAsync</c> when set: a host that cannot be reached.</summary>
    public Exception? KillAllFailure { get; set; }
    public List<RunEnvironmentRequest> Created { get; } = [];
    public List<RunEnvironment> Deleted { get; } = [];
    public Func<RunEnvironmentRequest, string> WorkingDirectoryFor { get; set; } = request => Path.Combine("/fake-worktrees", request.BranchName);

    private readonly ILD.Core.Services.Interfaces.IRepositoryManager? _repositories;
    private readonly bool _seesDisk;

    public FakeRunHost()
    {
    }

    /// <summary>
    /// A fake whose environments are made by <paramref name="repositories"/> (a mock the test already sets up and verifies) and
    /// whose directories include those that really exist, for tests that stage a worktree on disk.
    /// </summary>
    public FakeRunHost(ILD.Core.Services.Interfaces.IRepositoryManager repositories)
    {
        _repositories = repositories;
        _seesDisk = true;
    }

    public RunHostKind Kind => RunHostKind.Local;

    public FakeRunHost WithDirectory(string path)
    {
        lock (_gate) _directories.Add(path);
        return this;
    }

    public FakeRunHost WithFile(string path, string content)
    {
        lock (_gate) _files[path] = Encoding.UTF8.GetBytes(content);
        return this;
    }

    public string? ReadText(string path)
    {
        lock (_gate) return _files.TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
    }

    public async Task<RunEnvironment> CreateAsync(RunEnvironmentRequest request, CancellationToken ct = default)
    {
        var path = _repositories is null ? WorkingDirectoryFor(request) : await _repositories.CreateWorktreeAsync(request.BasePath, request.BranchName);
        lock (_gate)
        {
            Created.Add(request);
            _directories.Add(path);
        }
        return new RunEnvironment(request.OwnerId, path);
    }

    public async Task DeleteAsync(RunEnvironment environment, CancellationToken ct = default)
    {
        await KillAllAsync(environment.OwnerId, ct);
        lock (_gate)
        {
            Deleted.Add(environment);
            _directories.Remove(environment.WorkingDirectory);
        }
    }

    public Task<IRunProcess> StartCommandAsync(RunCommand command, CancellationToken ct = default)
    {
        var process = new FakeRunProcess(command, Script(command));
        lock (_gate)
        {
            Started.Add(command);
            _running.Add(process);
        }
        return Task.FromResult<IRunProcess>(process);
    }

    public async Task<int> KillAllAsync(Guid ownerId, CancellationToken ct = default)
    {
        List<FakeRunProcess> mine;
        lock (_gate)
        {
            KillAllCalls.Add(ownerId);
            OnKillAll?.Invoke(ownerId);
            if (KillAllFailure is not null) throw KillAllFailure;
            mine = _running.Where(p => p.Command.OwnerId == ownerId && !p.HasExited).ToList();
        }
        foreach (var process in mine)
        {
            process.TryKill();
            lock (_gate) Killed.Add(process);
            await process.WaitForExitAsync(ct);
        }
        return mine.Count;
    }

    public Task<IPtyConnection> OpenTerminalAsync(PtyOptions options, CancellationToken ct = default)
        => throw new NotSupportedException("the fake run host has no terminal");

    public AgentIsolation.AgentCommand RouteTerminalCommand(string fileName, IReadOnlyList<string> arguments, Guid? providerId, ILD.Data.Enums.NetworkMode mode)
        => new(fileName, arguments, new Dictionary<string, string>());

    public Task<RunHostResult> PushBundleAsync(string targetPath, string originUrl, Stream bundle, CancellationToken ct = default)
    {
        lock (_gate) _directories.Add(targetPath);
        return Task.FromResult(new RunHostResult(0, string.Empty));
    }

    public Task<RunHostResult> FetchBundleAsync(string clonePath, Stream bundle, CancellationToken ct = default)
        => Task.FromResult(new RunHostResult(0, string.Empty));

    public Task<RunHostResult> PullBundleAsync(string clonePath, string branch, bool incremental, Stream sink, CancellationToken ct = default)
        => Task.FromResult(new RunHostResult(0, string.Empty));

    public Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<Stream?>(_files.TryGetValue(path, out var bytes) ? new MemoryStream(bytes, writable: false) : null);
    }

    public string? ReadTextFile(string path) => ReadText(path);

    public Task<Stream?> OpenFileForReadAsync(string path, CancellationToken ct = default) => OpenReadAsync(path, ct);

    public Task<Stream> OpenFileForWriteAsync(string path, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_files.TryGetValue(path, out var bytes)) throw new FileNotFoundException(path);
            var stream = new WriteBackStream(bytes, content => { lock (_gate) _files[path] = content; });
            return Task.FromResult<Stream>(stream);
        }
    }

    public Task<long?> GetFileLengthAsync(string path, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<long?>(_files.TryGetValue(path, out var bytes) ? bytes.Length : null);
    }

    public async Task ReadFileRangeAsync(string path, long offset, long count, Func<Stream, Task> consume)
    {
        byte[] bytes;
        lock (_gate) bytes = _files.TryGetValue(path, out var found) ? found : throw new FileNotFoundException(path);
        await consume(new MemoryStream(bytes, (int)offset, (int)count, writable: false));
    }

    public async Task WriteFileAsync(string path, Stream content, CancellationToken ct = default)
    {
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, ct);
        lock (_gate) _files[path] = copy.ToArray();
    }

    public Task WriteFileAsync(string path, string content, CancellationToken ct = default)
    {
        WithFile(path, content);
        return Task.CompletedTask;
    }

    public Task WriteReadOnlyFileAsync(string path, string content, CancellationToken ct = default) => WriteFileAsync(path, content, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        WithDirectory(path);
        return Task.CompletedTask;
    }

    public Task<bool> FileExistsAsync(string path, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_files.ContainsKey(path));
    }

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_directories.Contains(path) || (_seesDisk && Directory.Exists(path)));
    }

    public Task<IReadOnlyList<(string Path, string FirstLine)>> ListFilesAsync(string directory, string namePattern, CancellationToken ct = default)
    {
        var glob = new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(namePattern).Replace("\\*", ".*") + "$");
        lock (_gate)
        {
            IReadOnlyList<(string, string)> found = _files
                .Where(f => f.Key.StartsWith(directory.TrimEnd('/') + "/", StringComparison.Ordinal) && glob.IsMatch(Path.GetFileName(f.Key)))
                .Select(f => (f.Key, Encoding.UTF8.GetString(f.Value).Split('\n')[0]))
                .ToList();
            return Task.FromResult(found);
        }
    }

    public Task<bool> DeleteAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var path in paths)
            {
                _files.Remove(path);
                _directories.Remove(path);
            }
        }
        return Task.FromResult(true);
    }

    public Task<Uri> PreviewTargetAsync(string workingDirectory, int port, CancellationToken ct = default)
        => Task.FromResult(new Uri($"http://fake-run-host:{port}"));

    public Task<bool> IsRunCloneAsync(string path, CancellationToken ct = default)
        => Task.FromResult(ILD.Core.Services.Implementations.LocalWorktrees.TrustedRepositoryRoot(path) is not null);

    public Task<RunHostOutput> RunToolAsync(string runPath, string fileName, IReadOnlyList<string> arguments, RunToolStreams? streams = null, CancellationToken ct = default)
        => throw new NotSupportedException("the fake host runs no tools");

    public string GatewayApiUrl(string? configuredApiUrl) => configuredApiUrl ?? "http://fake-ild";

    public Task<IReadOnlyList<RunLeftover>> ListLeftoversAsync(IReadOnlySet<Guid> liveIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RunLeftover>>([]);

    public Task<bool> DeleteLeftoverAsync(RunLeftover leftover, CancellationToken ct = default) => Task.FromResult(true);
}

/// <summary>What a fake command prints and how it ends.</summary>
internal sealed record FakeCommandScript(string Stdout, string Stderr, int? ExitCode)
{
    /// <summary>Prints nothing and ends with <paramref name="code"/>.</summary>
    public static FakeCommandScript Exits(int code) => new(string.Empty, string.Empty, code);

    /// <summary>Prints <paramref name="stdout"/> and <paramref name="stderr"/>, then ends with <paramref name="code"/>.</summary>
    public static FakeCommandScript Prints(string stdout, string stderr = "", int code = 0) => new(stdout, stderr, code);

    /// <summary>Never ends by itself; only a kill ends it, with code 137.</summary>
    public static FakeCommandScript Hangs() => new(string.Empty, string.Empty, null);
}

internal sealed class FakeRunProcess : IRunProcess
{
    private static int _nextId = 1000;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _exitCode;

    public FakeRunProcess(RunCommand command, FakeCommandScript script)
    {
        Command = command;
        Id = Interlocked.Increment(ref _nextId);
        StandardOutput = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(script.Stdout)));
        StandardError = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(script.Stderr)));
        StandardInput = new StreamWriter(new MemoryStream());
        if (script.ExitCode is { } code)
        {
            _exitCode = code;
            _exited.TrySetResult();
        }
    }

    public RunCommand Command { get; }
    public bool WasKilled { get; private set; }
    public int Id { get; }
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }
    public StreamWriter StandardInput { get; }
    public bool HasExited => _exited.Task.IsCompleted;
    public int ExitCode => HasExited ? _exitCode : throw new InvalidOperationException("the fake command has not exited");

    public async Task WaitForExitAsync(CancellationToken ct = default) => await _exited.Task.WaitAsync(ct);

    public bool TryKill() => TryKill(out _);

    public bool TryKill(out string? failure)
    {
        failure = null;
        if (HasExited) return true;
        WasKilled = true;
        _exitCode = 137;
        _exited.TrySetResult();
        return true;
    }

    public void Dispose()
    {
    }
}

/// <summary>A readable, writable in-memory file that stores what it holds back on dispose.</summary>
internal sealed class WriteBackStream : MemoryStream
{
    private readonly Action<byte[]> _store;

    public WriteBackStream(byte[] initial, Action<byte[]> store)
    {
        _store = store;
        Write(initial);
        Position = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _store(ToArray());
        base.Dispose(disposing);
    }
}
