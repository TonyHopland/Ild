using ILD.Core.Services.RunHosts.Local;
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
    private sealed class NoHold : IDisposable
    {
        public static readonly NoHold Instance = new();

        public void Dispose() { }
    }

    /// <summary>The setting value that selects this host.</summary>
    RunHostKind Kind { get; }

    // -- Lifecycle ---------------------------------------------------------------

    /// <summary>Make the run's working directory from the base repository's checkout, or take it as it is when it is already healthy.</summary>
    Task<RunEnvironment> CreateAsync(RunEnvironmentRequest request, CancellationToken ct = default);

    /// <summary>
    /// Whether the run of <paramref name="environment"/> is still running as it was made, found without starting anything: its pod
    /// or container running, or on this machine, a run this ILD process made. A run that crashed, or outlived a restart of ILD, is not;
    /// what it kept between runs is never put back.
    /// </summary>
    Task<bool> IsRunningAsync(RunEnvironment environment, CancellationToken ct = default);

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

    /// <summary>
    /// Open an interactive terminal (a PTY) running <paramref name="options"/>. In a run's container or pod it reaches the network as a
    /// command of the run launched under <paramref name="mode"/> does; on ILD's own machine the caller routes the command itself.
    /// </summary>
    Task<IPtyConnection> OpenTerminalAsync(PtyOptions options, NetworkMode mode, CancellationToken ct = default);

    /// <summary>The command line that runs <paramref name="fileName"/> as an agent launch would, for a terminal to start (the provider sign-in terminal), with the egress rules of <paramref name="providerId"/> under <paramref name="mode"/>.</summary>
    AgentIsolation.AgentCommand RouteTerminalCommand(string fileName, IReadOnlyList<string> arguments, Guid? providerId, NetworkMode mode);

    // -- Bundles -----------------------------------------------------------------

    /// <summary>Clone the repository from <paramref name="bundle"/> (empty for a remote with no branches yet) into <paramref name="targetPath"/>, with <paramref name="originUrl"/> as its origin.</summary>
    Task<RunHostResult> PushBundleAsync(string targetPath, string originUrl, Stream bundle, CancellationToken ct = default);

    /// <summary>Fetch <paramref name="bundle"/> into the clone at <paramref name="clonePath"/>, under <c>refs/ild-sync</c>.</summary>
    Task<RunHostResult> FetchBundleAsync(string clonePath, Stream bundle, CancellationToken ct = default);

    /// <summary>Write the bundle of <paramref name="branch"/> from the clone at <paramref name="clonePath"/> to <paramref name="sink"/>; <paramref name="incremental"/> leaves out what the remote already has.</summary>
    Task<RunHostResult> PullBundleAsync(string clonePath, string branch, bool incremental, Stream sink, CancellationToken ct = default);

    // -- Tools -------------------------------------------------------------------

    /// <summary>
    /// Whether <paramref name=path/> is a clone this host made or trusts, so git may be run in it. The orchestrator
    /// refuses to run git in a directory whose <c>.git</c> it cannot vouch for.
    /// </summary>
    Task<bool> IsRunCloneAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Run a short agent-scoped command in the run that holds <paramref name=runPath/> (a path in that run),
    /// and wait for it. <paramref name="streams"/> redirects stdin and stdout to streams; otherwise both are collected.
    /// </summary>
    Task<RunHostOutput> RunToolAsync(string runPath, string fileName, IReadOnlyList<string> arguments, RunToolStreams? streams = null, CancellationToken ct = default);

    // -- Files -------------------------------------------------------------------

    /// <summary>
    /// The text of the file, or null when there is none.
    /// </summary>
    Task<string?> ReadTextFileAsync(string path, CancellationToken ct = default);

    /// <summary>The file opened to read while the agent may still change or delete it (shared with writers); null when it is a directory or not there.</summary>
    Task<Stream?> OpenFileForReadAsync(string path, CancellationToken ct = default);

    /// <summary>The existing file opened to read and rewrite in place. Throws <see cref="IOException"/> when it is gone or is not a file.</summary>
    Task<Stream> OpenFileForWriteAsync(string path, CancellationToken ct = default);

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

    /// <summary>Delete each path, whatever it is. Returns whether every one is gone; one in a run that is not running counts as gone with that run, which is never started again for it.</summary>
    Task<bool> DeleteAsync(IEnumerable<string> paths, CancellationToken ct = default);

    /// <summary>Make the folder a chat works in and return its path: below <paramref name="localRoot"/> on this machine, or a folder of its own in the chat container when the host keeps chats there. Fine when it is already there.</summary>
    Task<string> CreateChatFolderAsync(Guid chatId, string localRoot, CancellationToken ct = default);

    /// <summary>Add the toolchains the repository checked out at <paramref name="basePath"/> (ILD's own clone) needs to those of the chat that holds <paramref name="chatPath"/>. A host that gives chats no toolchains does nothing.</summary>
    Task AddChatToolsAsync(string chatPath, string basePath, CancellationToken ct = default);

    /// <summary>Counts the chat that holds <paramref name="chatPath"/> as busy until the result is disposed, for work that is not a command in its container (a clone). A host that removes nothing for idleness does nothing.</summary>
    IDisposable HoldChat(string chatPath) => NoHold.Instance;

    /// <summary>The names of the directories directly below <paramref name="directory"/>, leaving out links and names starting with '.'; none when there is no such directory.</summary>
    Task<IReadOnlyList<string>> ListDirectoriesAsync(string directory, CancellationToken ct = default);

    /// <summary>Whether anything, a link included, is at <paramref name="path"/>.</summary>
    Task<bool> EntryExistsAsync(string path, CancellationToken ct = default);

    // -- Preview and gateway -----------------------------------------------------

    /// <summary>Where the preview proxy forwards to reach a service listening on <paramref name="port"/> in the run whose working directory is <paramref name="workingDirectory"/>.</summary>
    Task<Uri> PreviewTargetAsync(string workingDirectory, int port, CancellationToken ct = default);

    /// <summary>
    /// The base address of ILD that a CLI launched in <paramref name="workingDirectory"/> is given for the model gateway and its MCP
    /// server: the name ILD has where that CLI runs, which is on its <c>NO_PROXY</c>. <paramref name="configuredApiUrl"/> is the
    /// operator's setting, null when unset.
    /// </summary>
    string GatewayApiUrl(string? configuredApiUrl, string? workingDirectory);

    /// <summary>The <c>HOME</c> the CLIs of the run that holds <paramref name="workingDirectory"/> run with, when it is not the agent home of this machine; null when it is.</summary>
    string? RunHomeFor(string workingDirectory);

    /// <summary>
    /// The <c>${STATE_DIR}</c> of a preview of the worktree at <paramref name="worktreePath"/>: a folder of the run that holds the
    /// worktree, which the preview's steps can write and see where they run. Not created.
    /// </summary>
    string PreviewStateDirectoryFor(string worktreePath);

    /// <summary>
    /// The run's own copy of the preview data <paramref name="repositoryId"/> keeps between runs, a volume per subfolder, as the run that
    /// holds <paramref name="worktreePath"/> sees it. Its host filled it when the run was made with <see cref="RunEnvironmentRequest.KeepPreviewData"/>
    /// and puts it back when the run ends cleanly. Null when the run has none: a chat, or a run made without it.
    /// </summary>
    Task<string?> PreviewDataFolderAsync(Guid repositoryId, string worktreePath, CancellationToken ct = default);

    // -- Sweep -------------------------------------------------------------------

    /// <summary>What runs and chats that are finished, or no longer exist, left behind. Anything of <paramref name="liveIds"/> is kept.</summary>
    Task<IReadOnlyList<RunLeftover>> ListLeftoversAsync(IReadOnlySet<Guid> liveIds, CancellationToken ct = default);

    /// <summary>Delete one leftover. Returns whether it is gone.</summary>
    Task<bool> DeleteLeftoverAsync(RunLeftover leftover, CancellationToken ct = default);
}

/// <summary>Which backend holds the runs (<see cref="RunHostOptions.Detect"/>).</summary>
public enum RunHostKind
{
    Local,
    Container,
    Kubernetes,
}

/// <summary>
/// The handle of a run's environment: whose it is and its working directory, in the host's own paths. Deleting it with
/// <see cref="KeptPreviewDataOf"/> puts the run's copy of the preview data back in that repository's store first, when the run is still running.
/// </summary>
public sealed record RunEnvironment(Guid OwnerId, string WorkingDirectory, Guid? KeptPreviewDataOf = null);

/// <summary>What <see cref="IRunHost.CreateAsync"/> makes an environment from: the base checkout and the run's branch. <see cref="KeepPreviewData"/> gives the run a copy of the preview data its repository keeps between runs.</summary>
public sealed record RunEnvironmentRequest(Guid OwnerId, string BasePath, string BranchName, Guid RepositoryId, bool Dns = false, bool KeepPreviewData = false);

/// <summary>
/// A command to start. <see cref="Start"/> carries the program, arguments, working directory and which streams are
/// redirected; its <see cref="ProcessStartInfo.Environment"/> is ignored. The command's whole environment is
/// <see cref="Environment"/>, the variables the caller means to set, merged by the host (<see cref="CommandEnvironment"/>).
/// An agent CLI launch (<see cref="Agent"/>) is also given the provider's egress rules under <see cref="NetworkMode"/>.
/// <see cref="OwnerId"/> is the run or chat it belongs to, for <see cref="IRunHost.KillAllAsync"/>.
/// </summary>
public sealed record RunCommand
{
    private RunCommand(ProcessStartInfo start, Guid ownerId, bool asAgent, Guid? providerId, NetworkMode networkMode, IReadOnlyDictionary<string, string?>? environment, IReadOnlyList<string>? files = null)
    {
        Start = start;
        OwnerId = ownerId;
        AsAgent = asAgent;
        ProviderId = providerId;
        NetworkMode = networkMode;
        Environment = environment;
        Files = files ?? [];
    }

    public ProcessStartInfo Start { get; }
    public Guid OwnerId { get; }
    public bool AsAgent { get; }
    public Guid? ProviderId { get; }
    public NetworkMode NetworkMode { get; }

    /// <summary>The variables the caller sets, and with a null value unsets; nothing else of the caller's reaches the command.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; }

    /// <summary>The proxy URL the local host points a prepared command at, as its host-owned proxy variables; an isolated host chooses its own. Null for none.</summary>
    public string? EgressProxy { get; init; }

    /// <summary>
    /// The run or chat the command must run in, for a command the agent can shape (a Cmd node, a preview step). An isolated host refuses it
    /// unless it holds that run and the working directory is in it; it is never started on ILD's own machine instead. Unlike
    /// <see cref="OwnerId"/> it does not make <see cref="IRunHost.KillAllAsync"/> end the command, so a preview outlives a retry.
    /// </summary>
    public Guid? RunIn { get; init; }

    /// <summary>
    /// The files in the agent read root that the caller wrote for this command and its arguments or environment point at.
    /// A host that cannot share ILD's disk with the command copies exactly these in; text is never searched for paths.
    /// </summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>An agent CLI launch: the host gives it the provider's egress rules and environment.</summary>
    public static RunCommand Agent(
        ProcessStartInfo start, Guid ownerId, Guid? providerId, NetworkMode networkMode, IReadOnlyDictionary<string, string?>? environment, IReadOnlyList<string>? files = null)
        => new(start, ownerId, asAgent: true, providerId, networkMode, environment, files);

    /// <summary>
    /// A command that is not an agent CLI (a Cmd node, a preview, a git command in a chat clone). <paramref name="networkMode"/> is the mode it
    /// is launched in: a host that runs it in an environment of its own gives it a proxy in every mode but Off, which gets a direct route (a command
    /// ILD starts for itself is proxied, which Monitor says). <paramref name="environment"/> is what the caller sets.
    /// </summary>
    public static RunCommand Prepared(
        ProcessStartInfo prepared, Guid ownerId = default, IReadOnlyDictionary<string, string?>? environment = null, IReadOnlyList<string>? files = null, NetworkMode networkMode = NetworkMode.Monitor)
        => new(prepared, ownerId, asAgent: false, providerId: null, networkMode, environment, files);
}

/// <summary>The outcome of a command that moved code.</summary>
public sealed record RunHostResult(int ExitCode, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>What a short command printed and how it ended.</summary>
public sealed record RunHostOutput(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Streams a short command reads its stdin from and writes its stdout to instead of the collected text.</summary>
public sealed record RunToolStreams(Stream? StandardInputFrom = null, Stream? StandardOutputTo = null);

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
    /// it worked, because an agent that could not be killed goes on writing the worktree.
    /// True when the command is gone, killed or already ended.
    /// </summary>
    bool TryKill();

    /// <summary>As <see cref="TryKill"/>, but the reason when it fails.</summary>
    bool TryKill(out string? failure);
}
