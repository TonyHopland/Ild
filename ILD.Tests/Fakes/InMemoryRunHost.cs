using System.Text;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Local;
using ILD.Core.Services.RunHosts.Tools;

namespace ILD.Tests.Fakes;

/// <summary>
/// The isolated run host on an engine that has no pod or container behind it: every run exists, a command is an
/// <see cref="InMemoryExec"/> that <see cref="OnExec"/> answers, and what the engine was asked is kept in lists. For what the
/// host does whichever engine holds its runs; what an engine does is tested against that engine's own fake.
/// </summary>
internal sealed class InMemoryRunHost() : ExecRunHost(new ProcessRunner(), null)
{
    public const string ChatName = "chat";
    public const string Address = "10.9.0.7";
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private readonly List<InMemoryExec> _execs = [];
    private bool _chatUp;

    /// <summary>What a command does once it is started. By default it reads its input to the end and exits 0.</summary>
    public Func<InMemoryExec, Task> OnExec { get; set; } = async exec =>
    {
        if (exec.WantsStdin) await exec.ReadStdinToEndAsync();
        await exec.ExitAsync(0);
    };

    /// <summary>What stops a command from starting, when it returns one.</summary>
    public Func<InMemoryExec, Exception?> RefuseExec { get; set; } = _ => null;

    public bool Revives { get; set; }
    public Exception? SignInFailure { get; set; }
    public TaskCompletionSource? MakingChat { get; set; }

    public IReadOnlyList<InMemoryExec> Execs { get { lock (_gate) return _execs.ToList(); } }
    public List<Guid> Revived { get; } = [];
    public HashSet<Guid> Ready { get; } = [];
    public List<Guid> Made { get; } = [];
    public List<Guid> Undone { get; } = [];
    public List<Guid> Removed { get; } = [];
    public List<string> SignInsStarted { get; } = [];
    public List<string> SignInsRemoved { get; } = [];
    public int ChatsMade { get; private set; }
    public int ChatsRemoved { get; private set; }

    public IEnumerable<string> Open => OpenSideEnvironments;

    public string NameOf(Guid run) => RunEnvironmentName(run);

    protected override bool ExecTakesEnvironment => true;

    protected override Task<IExecStream> OpenExecInAsync(
        string name, IReadOnlyList<string> command, IReadOnlyDictionary<string, string>? environment, bool stdin, bool tty, bool asRoot, CancellationToken ct)
    {
        var exec = new InMemoryExec(name, command, environment, stdin, tty);
        lock (_gate) _execs.Add(exec);
        if (RefuseExec(exec) is { } refusal) return Task.FromException<IExecStream>(refusal);
        _ = Task.Run(() => OnExec(exec), CancellationToken.None);
        return Task.FromResult<IExecStream>(exec);
    }

    protected override string RunEnvironmentName(Guid run) => $"run-{run:N}";

    protected override Task<bool> ReviveRunAsync(Guid run, Exception failure, CancellationToken ct)
    {
        if (Revives) Revived.Add(run);
        return Task.FromResult(Revives);
    }

    protected override Task<bool> IsRunReadyAsync(Guid run, CancellationToken ct) => Task.FromResult(Ready.Contains(run));

    protected override Task<Func<Task>> MakeRunAsync(RunEnvironmentRequest request, RunToolEnvironment? tools, CancellationToken ct)
    {
        Made.Add(request.OwnerId);
        return Task.FromResult<Func<Task>>(() =>
        {
            Undone.Add(request.OwnerId);
            return Task.CompletedTask;
        });
    }

    protected override Task RemoveRunAsync(Guid run, CancellationToken ct)
    {
        Removed.Add(run);
        return Task.CompletedTask;
    }

    protected override Task StartSignInAsync(string name, Guid id, RunToolEnvironment? tools, CancellationToken ct)
    {
        SignInsStarted.Add(name);
        return SignInFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }

    protected override Task RemoveSignInAsync(string name)
    {
        SignInsRemoved.Add(name);
        return Task.CompletedTask;
    }

    public override bool HostsChats => true;
    protected override TimeSpan ChatIdleTimeout => IdleTimeout;
    protected override string ChatEnvironmentName => ChatName;
    protected override Task<bool> ChatIsUpAsync(CancellationToken ct) => Task.FromResult(_chatUp);

    protected override async Task MakeChatAsync(CancellationToken ct)
    {
        if (MakingChat is { } making) await making.Task.WaitAsync(ct);
        ChatsMade++;
        _chatUp = true;
    }

    protected override Task RemoveChatAsync(CancellationToken ct)
    {
        ChatsRemoved++;
        _chatUp = false;
        return Task.CompletedTask;
    }

    protected override Task<bool> RunExistsAsync(ExecOwner owner, CancellationToken ct) => Task.FromResult(!owner.Chat || _chatUp);
    protected override Task<bool> IsRunningAsync(Guid owner, CancellationToken ct) => Task.FromResult(true);
    protected override Task<string> RunAddressAsync(Guid run, CancellationToken ct) => Task.FromResult(Address);

    public override RunHostKind Kind => RunHostKind.Container;
    public override string LeftoverKind => "in-memory";
    public override TimeSpan SweepInterval => TimeSpan.FromMinutes(1);
    protected override string Noun => "pod";
    protected override string IldUrl => "http://ild.example:8080";
    protected override int EgressProxyPort => 3128;
    protected override bool UsesToolStore => false;
    protected override string ToolZoneName(RunToolMount zone) => zone.Zone.ToString();
    protected override Task StartToolInstallerAsync(string name, Guid id, ToolInstaller installer, CancellationToken ct) => Task.CompletedTask;
    protected override Task RemoveToolInstallerAsync(string name) => Task.CompletedTask;
    protected override Task OpenDirectRouteAsync(ExecOwner owner, CancellationToken ct) => Task.CompletedTask;
    protected override Task CloseDirectRouteAsync(ExecOwner owner, CancellationToken ct) => Task.CompletedTask;
    protected override Task<bool> HasDirectRouteAsync(ExecOwner owner, CancellationToken ct) => Task.FromResult(false);
    public override Task<IReadOnlyList<DirectRoute>> DirectRoutesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DirectRoute>>([]);
    public override Task<IReadOnlyList<RunLeftover>> ListLeftoversAsync(IReadOnlySet<Guid> liveIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RunLeftover>>([]);
    public override Task<bool> DeleteLeftoverAsync(RunLeftover leftover, CancellationToken ct = default) => Task.FromResult(false);
}

/// <summary>One command started on the <see cref="InMemoryRunHost"/>: what it was started with, and the means for a test to answer it.</summary>
internal sealed class InMemoryExec : IExecStream
{
    private readonly ByteChannelStream _stdout = new();
    private readonly ByteChannelStream? _stderr;
    private readonly MemoryStream _stdin = new();
    private readonly TaskCompletionSource _stdinClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int?> _status = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public InMemoryExec(string name, IReadOnlyList<string> command, IReadOnlyDictionary<string, string>? environment, bool stdin, bool tty)
    {
        Name = name;
        Command = command;
        Env = (environment ?? new Dictionary<string, string>()).Select(e => $"{e.Key}={e.Value}").ToList();
        WantsStdin = stdin;
        Tty = tty;
        _stderr = tty ? null : new ByteChannelStream();
    }

    /// <summary>The pod or container it was started in.</summary>
    public string Name { get; }
    public IReadOnlyList<string> Command { get; }
    public string Joined => string.Join(' ', Command);

    /// <summary>The environment the exec was given, as <c>NAME=value</c>.</summary>
    public IReadOnlyList<string> Env { get; }
    public bool WantsStdin { get; }
    public bool Tty { get; }

    public Stream Stdout => _stdout;
    public Stream? Stderr => _stderr;
    public Task<int?> Status => _status.Task;

    public Task WriteStdinAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        lock (_stdin) _stdin.Write(data.Span);
        return Task.CompletedTask;
    }

    public Task CloseStdinAsync(CancellationToken ct = default)
    {
        _stdinClosed.TrySetResult();
        return Task.CompletedTask;
    }

    public Task ResizeAsync(int cols, int rows, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>What was written to its input, once the input was closed or the command was let go.</summary>
    public async Task<byte[]> ReadStdinToEndAsync()
    {
        if (WantsStdin) await _stdinClosed.Task;
        lock (_stdin) return _stdin.ToArray();
    }

    public async Task WriteStdoutAsync(string text) => await _stdout.AppendAsync(Encoding.UTF8.GetBytes(text));

    public async Task WriteStderrAsync(string text)
    {
        if (_stderr is not null) await _stderr.AppendAsync(Encoding.UTF8.GetBytes(text));
    }

    public Task ExitAsync(int code)
    {
        _stdout.Complete();
        _stderr?.Complete();
        _status.TrySetResult(code);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _stdinClosed.TrySetResult();
        _stdout.Complete();
        _stderr?.Complete();
        _status.TrySetResult(null);
    }
}
