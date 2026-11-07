using System.Diagnostics;
using ILD.Core.Services.Implementations;
using ILD.Data.Enums;
using Porta.Pty;

namespace ILD.Core.Services.RunHosts;

/// <summary>
/// Where a run's processes and files live. Everything the orchestrator does that
/// depends on that — starting the agent, running a Cmd node, opening a terminal, moving
/// code in and out, reading the transcripts a CLI wrote, finding a preview's port — goes
/// through this interface, so a backend that puts the run somewhere else (a container,
/// a Kubernetes pod) plugs in beside <c>local</c>, today's uid split, without the
/// callers changing. Paths in it are the host's own: the orchestrator never assumes it
/// can open one itself.
/// </summary>
public interface IRunHost
{
    /// <summary>The setting value that selects this host.</summary>
    RunHostKind Kind { get; }

    // -- Lifecycle ---------------------------------------------------------------

    /// <summary>Make the run's working directory from the base repository's checkout, or take it as it is when it is already healthy.</summary>
    Task<RunEnvironment> CreateAsync(RunEnvironmentRequest request, CancellationToken ct = default);

    /// <summary>Remove what <see cref="CreateAsync"/> made. Never throws for what it finds there; the exception is a host that cannot be reached.</summary>
    Task DeleteAsync(RunEnvironment environment, CancellationToken ct = default);

    // -- Commands ----------------------------------------------------------------

    /// <summary>Start a command in the run and hand back its streams. The command ends with <see cref="IRunProcess.WaitForExitAsync"/> or <see cref="IRunProcess.TryKill"/>.</summary>
    Task<IRunProcess> StartCommandAsync(RunCommand command, CancellationToken ct = default);

    /// <summary>
    /// Kill every command still running for <paramref name="ownerId"/> (a run or a chat) and
    /// wait for them to end. Called before a node is retried, so the attempt that was cut off
    /// is not still writing while the next one starts. Returns how many it killed.
    /// </summary>
    Task<int> KillAllAsync(Guid ownerId, CancellationToken ct = default);

    // -- Terminal ----------------------------------------------------------------

    /// <summary>Open an interactive terminal (a PTY) running <paramref name="options"/>.</summary>
    Task<IPtyConnection> OpenTerminalAsync(PtyOptions options, CancellationToken ct = default);

    /// <summary>The command line that runs <paramref name="fileName"/> as the agent would, for a terminal to start (the provider sign-in terminal), with the egress rules of <paramref name="providerId"/> under <paramref name="mode"/>.</summary>
    AgentIsolation.AgentCommand RouteTerminalCommand(string fileName, IReadOnlyList<string> arguments, Guid? providerId, NetworkMode mode);

    // -- Bundles -----------------------------------------------------------------

    /// <summary>Clone the repository from <paramref name="bundle"/> (empty for a remote with no branches yet) into <paramref name="targetPath"/>, with <paramref name="originUrl"/> as its origin.</summary>
    Task<RunHostResult> PushBundleAsync(string targetPath, string originUrl, Stream bundle, CancellationToken ct = default);

    /// <summary>Fetch <paramref name="bundle"/> into the clone at <paramref name="clonePath"/>, under <c>refs/ild-sync</c>.</summary>
    Task<RunHostResult> FetchBundleAsync(string clonePath, Stream bundle, CancellationToken ct = default);

    /// <summary>Write the bundle of <paramref name="branch"/> from the clone at <paramref name="clonePath"/> to <paramref name="sink"/>; <paramref name="incremental"/> leaves out what the remote already has.</summary>
    Task<RunHostResult> PullBundleAsync(string clonePath, string branch, bool incremental, Stream sink, CancellationToken ct = default);

    // -- Files -------------------------------------------------------------------

    /// <summary>The length of the regular file at <paramref name="path"/>; null when there is none.</summary>
    Task<long?> GetFileLengthAsync(string path, CancellationToken ct = default);

    /// <summary>The file at <paramref name="path"/> opened for reading, as the orchestrator would open it; null when there is no such file. For files the orchestrator wrote or owns, such as a worktree's <c>ild.config.json</c>.</summary>
    Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default);

    /// <summary>Stream <paramref name="count"/> bytes of the file from <paramref name="offset"/> to <paramref name="consume"/>.</summary>
    Task ReadFileRangeAsync(string path, long offset, long count, Func<Stream, Task> consume);

    /// <summary>Write <paramref name="content"/> to the file, replacing it, owned by whoever runs the agent.</summary>
    Task WriteFileAsync(string path, Stream content, CancellationToken ct = default);

    /// <inheritdoc cref="WriteFileAsync(string, Stream, CancellationToken)"/>
    Task WriteFileAsync(string path, string content, CancellationToken ct = default);

    /// <summary>As <see cref="WriteFileAsync(string, string, CancellationToken)"/>, but the agent can read the file and not change it.</summary>
    Task WriteReadOnlyFileAsync(string path, string content, CancellationToken ct = default);

    Task CreateDirectoryAsync(string path, CancellationToken ct = default);

    /// <summary>Whether a regular file, not a link, is at <paramref name="path"/>.</summary>
    Task<bool> FileExistsAsync(string path, CancellationToken ct = default);

    /// <summary>Whether a directory is at <paramref name="path"/>.</summary>
    Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default);

    /// <summary>Every regular file below <paramref name="directory"/> matching <paramref name="namePattern"/>, with its first line.</summary>
    Task<IReadOnlyList<(string Path, string FirstLine)>> ListFilesAsync(string directory, string namePattern, CancellationToken ct = default);

    /// <summary>Delete each path, whatever it is. Returns whether every one is gone.</summary>
    Task<bool> DeleteAsync(IEnumerable<string> paths, CancellationToken ct = default);

    // -- Preview and gateway -----------------------------------------------------

    /// <summary>Where the preview proxy forwards to reach a service listening on <paramref name="port"/> in the run.</summary>
    Uri PreviewTarget(int port);

    /// <summary>The base address of ILD that the run's CLI is given for the model gateway; <paramref name="configuredApiUrl"/> is the operator's setting, null when unset.</summary>
    string GatewayApiUrl(string? configuredApiUrl);

    // -- Sweep -------------------------------------------------------------------

    /// <summary>What runs and chats that are finished, or no longer exist, left behind. Anything of <paramref name="liveIds"/> is kept.</summary>
    Task<IReadOnlyList<RunLeftover>> ListLeftoversAsync(IReadOnlySet<Guid> liveIds, CancellationToken ct = default);

    /// <summary>Delete one leftover. Returns whether it is gone.</summary>
    Task<bool> DeleteLeftoverAsync(RunLeftover leftover, CancellationToken ct = default);
}

/// <summary>Which backend holds the runs; the <c>ILD_RUN_HOST</c> setting.</summary>
public enum RunHostKind
{
    Local,
    Container,
    Kubernetes,
}

/// <summary>The handle of a run's environment: whose it is and its working directory, in the host's own paths.</summary>
public sealed record RunEnvironment(Guid OwnerId, string WorkingDirectory);

/// <summary>What <see cref="IRunHost.CreateAsync"/> makes an environment from: the base checkout and the run's branch.</summary>
public sealed record RunEnvironmentRequest(Guid OwnerId, string BasePath, string BranchName);

/// <summary>
/// A command to start. <see cref="Start"/> carries the program, arguments, working directory,
/// environment and which streams are redirected. An agent CLI launch (<see cref="Agent"/>) is crossed
/// to the agent by the host, with the provider's egress rules under <see cref="NetworkMode"/> and
/// <see cref="Environment"/> set over what <see cref="Start"/> carries; any other command is started as
/// the caller built it. <see cref="OwnerId"/> is the run or chat it belongs to, for <see cref="IRunHost.KillAllAsync"/>.
/// </summary>
public sealed record RunCommand
{
    private RunCommand(ProcessStartInfo start, Guid ownerId, bool asAgent, Guid? providerId, NetworkMode networkMode, IReadOnlyDictionary<string, string>? environment)
    {
        Start = start;
        OwnerId = ownerId;
        AsAgent = asAgent;
        ProviderId = providerId;
        NetworkMode = networkMode;
        Environment = environment;
    }

    public ProcessStartInfo Start { get; }
    public Guid OwnerId { get; }
    public bool AsAgent { get; }
    public Guid? ProviderId { get; }
    public NetworkMode NetworkMode { get; }
    public IReadOnlyDictionary<string, string>? Environment { get; }

    /// <summary>An agent CLI launch: the host crosses it to the agent.</summary>
    public static RunCommand Agent(
        ProcessStartInfo start, Guid ownerId, Guid? providerId, NetworkMode networkMode, IReadOnlyDictionary<string, string>? environment)
        => new(start, ownerId, asAgent: true, providerId, networkMode, environment);

    /// <summary>
    /// A command the caller has already crossed to the agent with <c>AgentIsolation.Route</c> (a Cmd node, a preview, a git
    /// command in a chat clone). The host refuses to start one that is not, when agent isolation is on (ADR-0014).
    /// </summary>
    public static RunCommand Routed(ProcessStartInfo routed, Guid ownerId = default)
        => new(routed, ownerId, asAgent: false, providerId: null, NetworkMode.Off, environment: null);
}

/// <summary>The outcome of a command that moved code.</summary>
public sealed record RunHostResult(int ExitCode, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Something a run left in a host, named so it can be shown and deleted.</summary>
public sealed record RunLeftover(string Kind, string Location);

/// <summary>A command running in a host.</summary>
public interface IRunProcess : IDisposable
{
    int Id { get; }
    StreamReader StandardOutput { get; }
    StreamReader StandardError { get; }
    StreamWriter StandardInput { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken ct = default);

    /// <summary>
    /// Kill the command and everything it started. Never throws; unlike a blind kill it says whether
    /// it worked, because an agent that could not be killed goes on writing the worktree
    /// (under uid isolation, <c>kill(2)</c> fails with <c>EPERM</c> without <c>CAP_KILL</c>).
    /// True when the command is gone, killed or already ended.
    /// </summary>
    bool TryKill();

    /// <summary>As <see cref="TryKill"/>, but the reason when it fails.</summary>
    bool TryKill(out string? failure);
}
