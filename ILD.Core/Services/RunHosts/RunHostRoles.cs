using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Local;
using ILD.Data.Enums;
using Porta.Pty;

namespace ILD.Core.Services.RunHosts;

// The run host is one object with one backend (ADR-0032). These are the views of it a caller depends on: each names what one kind of caller does with a
// run host, so a caller sees only that and a test fakes only that. IRunHost is all of the views a run's callers share; the last few (operator, sweep,
// sign-in) are for what only some backends have, and are reached through their own registration, never by a downcast.

/// <summary>Which backend holds the runs.</summary>
public interface IRunHostIdentity
{
    /// <summary>The setting value that selects this host.</summary>
    RunHostKind Kind { get; }
}

/// <summary>Making a run's environment, asking whether it is still there, and removing it.</summary>
public interface IRunLifecycle : IRunHostIdentity
{
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

    /// <summary>
    /// Keep the run's worktree somewhere that outlives its pod or container (a commit of everything git does not ignore, as a hidden ref in the base
    /// repository, never a branch on the remote), so a run whose pod is lost is made again with its work. Best effort: false when it could not be kept, null
    /// when the host has no pod to lose or keeps no record to make one from.
    /// </summary>
    Task<bool?> CheckpointAsync(Guid runId, CancellationToken ct = default) => Task.FromResult<bool?>(null);

    /// <summary>
    /// Make the run's pod or container again when it no longer exists, on its volume if that survived and from its checkpoint if not. Returns what of
    /// the run's work came back and what did not; null when the environment is there, or the host keeps nothing to make it from (commands in the run
    /// then fail with <see cref="RunPodLostException"/>). Called before a node runs, never from inside a command: it holds the repository's lock.
    /// </summary>
    Task<string?> RecoverAsync(Guid runId, CancellationToken ct = default) => Task.FromResult<string?>(null);
}

/// <summary>Which run or chat a path or a call belongs to, answered here once, by RunHolders; callers ask the host and never read a path themselves. The defaults are the path format alone (ADR-0032, ADR-0034); only a host that routes by it answers otherwise.</summary>
public interface IRunHolders
{
    /// <summary>The id of the run or chat whose directory <paramref name="path"/> names, whatever this host keeps; null when it names neither.</summary>
    Guid? HolderOf(string? path) => RunPaths.HolderOf(path);

    /// <summary>The id of the run whose directory <paramref name="path"/> is in; null when it is not in a run's.</summary>
    Guid? RunOf(string? path) => RunHolders.Named(path) is { Kind: RunHolderKind.Run } holder ? holder.Id : null;

    /// <summary>Whether <paramref name="path"/>, of a run or chat, is on ILD's own disk under a host that runs everything in containers: it is read and deleted but never run in.</summary>
    bool PredatesContainers(string? path) => false;

    /// <summary>The role of the host for ILD's own base clone at <paramref name="path"/> when this host would refuse to reach it (a path that is in no run or chat); null when this host is the one to ask.</summary>
    IRunRepositories? OwnDiskHostFor(string? path) => null;
}

/// <summary>Running commands in a run or chat and finding out what they left running.</summary>
public interface IRunCommands
{
    /// <summary>Start a command in the run and hand back its streams. The command ends with <see cref="IRunProcess.WaitForExitAsync"/> or <see cref="IRunProcess.TryKill"/>.</summary>
    Task<IRunProcess> StartCommandAsync(RunCommand command, CancellationToken ct = default);

    /// <summary>
    /// Kill every command still running for <paramref name="ownerId"/> (a run or a chat) and
    /// wait for them to end. Called before a node is retried, so the attempt that was cut off
    /// is not still writing while the next one starts. Returns how many it killed.
    /// </summary>
    Task<int> KillAllAsync(Guid ownerId, CancellationToken ct = default);

    /// <summary>
    /// Kill every command still running in the run or chat <paramref name="runIn"/> that was started with
    /// <see cref="RunCommand.RunIn"/> and no owner (its preview), and wait for them to end: what an ILD that ended
    /// without stopping them left behind. Returns how many it killed; a host that runs nothing there kills none.
    /// </summary>
    Task<int> KillUnownedAsync(Guid runIn, CancellationToken ct = default) => Task.FromResult(0);

    /// <summary>
    /// Why the pod or container of run <paramref name="ownerId"/> ended, as its host reports it, or null when it is running or the host cannot say. An ending before <paramref name="since"/> is not reported.
    /// Read when a command ends because its container died, before the host removes or replaces it.
    /// </summary>
    Task<RunTermination?> GetTerminationAsync(Guid ownerId, DateTimeOffset? since = null, CancellationToken ct = default) => Task.FromResult<RunTermination?>(null);

    /// <summary>
    /// Run a short agent-scoped command in the run that holds <paramref name=runPath/> (a path in that run),
    /// and wait for it. <paramref name="streams"/> redirects stdin and stdout to streams; otherwise both are collected.
    /// </summary>
    Task<RunHostOutput> RunToolAsync(string runPath, string fileName, IReadOnlyList<string> arguments, RunToolStreams? streams = null, CancellationToken ct = default);
}

/// <summary>An interactive terminal (a PTY) in a run, or on ILD's own machine.</summary>
public interface IRunTerminals
{
    /// <summary>
    /// Open an interactive terminal (a PTY) running <paramref name="options"/>. In a run's container or pod it reaches the network as a
    /// command of the run launched under <paramref name="mode"/> does; on ILD's own machine the caller routes the command itself.
    /// </summary>
    Task<IPtyConnection> OpenTerminalAsync(PtyOptions options, NetworkMode mode, CancellationToken ct = default);
}

/// <summary>Moving a repository in and out of a run as a git bundle, and whether a directory is a clone the host trusts.</summary>
public interface IRunBundles
{
    /// <summary>Clone the repository from <paramref name="bundle"/> (empty for a remote with no branches yet) into <paramref name="targetPath"/>, with <paramref name="originUrl"/> as its origin.</summary>
    Task<RunHostResult> PushBundleAsync(string targetPath, string originUrl, Stream bundle, CancellationToken ct = default);

    /// <summary>Fetch <paramref name="bundle"/> into the clone at <paramref name="clonePath"/>, under <c>refs/ild-sync</c>.</summary>
    Task<RunHostResult> FetchBundleAsync(string clonePath, Stream bundle, CancellationToken ct = default);

    /// <summary>Write the bundle of <paramref name="branch"/> from the clone at <paramref name="clonePath"/> to <paramref name="sink"/>; <paramref name="incremental"/> leaves out what the remote already has.</summary>
    Task<RunHostResult> PullBundleAsync(string clonePath, string branch, bool incremental, Stream sink, CancellationToken ct = default);

    /// <summary>
    /// Whether <paramref name=path/> is a clone this host made or trusts, so git may be run in it. The orchestrator
    /// refuses to run git in a directory whose <c>.git</c> it cannot vouch for.
    /// </summary>
    Task<bool> IsRunCloneAsync(string path, CancellationToken ct = default);
}

/// <summary>Reading and writing the files of a run or chat, in the host's own paths.</summary>
public interface IRunFiles
{
    /// <summary>The text of the file, up to the host's read limit (<see cref="RunReadLimits"/>); null when there is none.</summary>
    Task<string?> ReadTextFileAsync(string path, CancellationToken ct = default);

    /// <summary>The file opened to read while the agent may still change or delete it (shared with writers); null when it is a directory or not there. Also for files the orchestrator wrote or owns, such as a worktree's <c>ild.config.json</c>.</summary>
    Task<Stream?> OpenFileForReadAsync(string path, CancellationToken ct = default);

    /// <summary>The existing file opened to read and rewrite in place. Throws <see cref="IOException"/> when it is gone or is not a file.</summary>
    Task<Stream> OpenFileForWriteAsync(string path, CancellationToken ct = default);

    /// <summary>The length of the regular file at <paramref name="path"/>; null when there is none.</summary>
    Task<long?> GetFileLengthAsync(string path, CancellationToken ct = default);

    /// <summary>Stream <paramref name="count"/> bytes of the file from <paramref name="offset"/> to <paramref name="consume"/>, for a file too big to read whole.</summary>
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

    /// <summary>The names of the directories directly below <paramref name="directory"/>, leaving out links and names starting with '.'; none when there is no such directory.</summary>
    Task<IReadOnlyList<string>> ListDirectoriesAsync(string directory, CancellationToken ct = default);

    /// <summary>Whether anything, a link included, is at <paramref name="path"/>.</summary>
    Task<bool> EntryExistsAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// <paramref name="path"/> with every link on it followed, on the machine that holds it, so a boundary check sees where it leads.
    /// Segments that are not there are carried through as written. Null when it cannot be resolved, such as a chain of links past what the kernel follows.
    /// </summary>
    Task<string?> ResolveLinksAsync(string path, CancellationToken ct = default);
}

/// <summary>Where a chat works: its folder, its toolchains, and whether a run's files are in reach of it.</summary>
public interface IChatHost
{
    private sealed class NoHold : IDisposable
    {
        public static readonly NoHold Instance = new();

        public void Dispose() { }
    }

    /// <summary>Whether a chat working in <paramref name="chatPath"/> can read the files of the run working in <paramref name="worktreePath"/>: neither is in an environment the other cannot see.</summary>
    bool ChatSeesWorktree(string worktreePath, string chatPath) => RunHolders.Named(worktreePath).Kind != RunHolderKind.Run && RunHolders.Named(chatPath).Kind != RunHolderKind.Chat;

    /// <summary>The path <see cref="CreateChatFolderAsync"/> returns for the chat, without making anything.</summary>
    string ChatFolderPath(Guid chatId, string localRoot);

    /// <summary>Make the folder a chat works in and return its path: below <paramref name="localRoot"/> on this machine, or a folder of its own in the chat container when the host keeps chats there. Fine when it is already there.</summary>
    Task<string> CreateChatFolderAsync(Guid chatId, string localRoot, CancellationToken ct = default);

    /// <summary>Add the toolchains the repository checked out at <paramref name="basePath"/> (ILD's own clone) needs to those of the chat that holds <paramref name="chatPath"/>. A host that gives chats no toolchains does nothing.</summary>
    Task AddChatToolsAsync(string chatPath, string basePath, CancellationToken ct = default);

    /// <summary>Counts the chat that holds <paramref name="chatPath"/> as busy until the result is disposed, for work that is not a command in its container (a clone). A host that removes nothing for idleness does nothing.</summary>
    IDisposable HoldChat(string chatPath) => NoHold.Instance;
}

/// <summary>What a preview and the model gateway need to know about where a run is.</summary>
public interface IPreviewHost
{
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
}

/// <summary>What finished runs and chats left behind, found and deleted. On ILD's own disk as well as in a pod or container.</summary>
public interface IRunLeftovers
{
    /// <summary>What runs and chats that are finished, or no longer exist, left behind. Anything of <paramref name="liveIds"/> is kept.</summary>
    Task<IReadOnlyList<RunLeftover>> ListLeftoversAsync(IReadOnlySet<Guid> liveIds, CancellationToken ct = default);

    /// <summary>Delete one leftover. Returns whether it is gone.</summary>
    Task<bool> DeleteLeftoverAsync(RunLeftover leftover, CancellationToken ct = default);
}

/// <summary>
/// What the timer-driven sweep does to the pods or containers of a host that has them: look for what runs left behind, keep the agents ILD installed in
/// the toolchain store, and put away chat containers that are idle. Only a host that runs runs in environments of its own has it, so it is registered
/// only then. Its leftovers are those of that host alone, never of ILD's own disk.
/// </summary>
public interface IRunSweep
{
    /// <summary>How often the sweep looks for what finished runs left behind.</summary>
    TimeSpan SweepInterval { get; }

    /// <summary>Checks how the host is set up, when ILD starts and on every sweep, logging what the operator should fix. Never throws.</summary>
    Task CheckAsync(CancellationToken ct = default);

    /// <summary>Installs the agents ILD has into the agents' zone ahead of the first run or chat that needs them. Never throws.</summary>
    Task WarmAgentToolsAsync(CancellationToken ct = default);

    /// <summary>Removes the chat container or pod when it has been idle for the timeout.</summary>
    Task ReapIdleChatAsync(CancellationToken ct = default);

    /// <summary>The leftovers of this host's pods or containers alone. Not <see cref="IRunLeftovers"/>, which a host that also serves ILD's own disk answers for both, so the sweep and the startup sweep do not share an interface.</summary>
    Task<IReadOnlyList<RunLeftover>> ListLeftoversAsync(IReadOnlySet<Guid> liveIds, CancellationToken ct = default);

    /// <summary>Delete one leftover of this host's pods or containers. Returns whether it is gone.</summary>
    Task<bool> DeleteLeftoverAsync(RunLeftover leftover, CancellationToken ct = default);
}

/// <summary>
/// What an operator sees of the run host: its kind and the engine's problems for the settings page, the networks that no run may reach, the runs
/// that have a direct route, what its container uses, and the stop of what it keeps. A host with no pods or containers answers none.
/// </summary>
public interface IRunHostOperator : IRunHostIdentity
{
    /// <summary>The address ranges the runs' networks use, which no run may reach.</summary>
    Task<IReadOnlyList<IpRange>> IsolatedRangesAsync(CancellationToken ct = default);

    /// <summary>The addresses of the cluster's control plane, which no run may reach.</summary>
    Task<IReadOnlyList<System.Net.IPAddress>> ControlPlaneAddressesAsync(CancellationToken ct = default);

    /// <summary>A problem with the engine the operator should see, or null when there is none or the engine cannot say.</summary>
    Task<string?> WarningAsync(CancellationToken ct = default);

    /// <summary>The runs and chats that have a direct route to the network now (network mode Off).</summary>
    Task<IReadOnlyList<DirectRoute>> DirectRoutesAsync(CancellationToken ct = default);

    /// <summary>What the engine says the run's container uses, or null when this host has no container to ask (only a Docker or Podman container host does).</summary>
    Task<ContainerStats?> GetStatsAsync(Guid owner, CancellationToken ct = default);

    /// <summary>The name a person attaches an editor to for the run whose directory is <paramref name="path"/>; null when this host has nothing to attach to.</summary>
    string? AttachTargetFor(string? path);

    /// <summary>
    /// Puts away what the host keeps outside ILD, when ILD stops, after the drain has parked its runs, so nothing it
    /// made keeps running while ILD is down. Never throws: a failure is logged, and ILD stops anyway.
    /// </summary>
    Task ShutdownAsync(CancellationToken ct);
}

/// <summary>A host that can run a provider's sign-in terminal in a short-lived container of its own; one that runs on ILD's own machine does not.</summary>
public interface ISignInTerminalHost
{
    /// <summary>
    /// Starts <paramref name="command"/> in a terminal in a container that no chat or run shares, with <paramref name="agent"/>
    /// (installed first if the toolchain store lacks it) on its PATH. The container is removed when the result is disposed.
    /// </summary>
    Task<SignInTerminal> OpenSignInAsync(ManagedAgent? agent, string command, IReadOnlyList<string> arguments, int cols, int rows, CancellationToken ct);
}

/// <summary>The host that runs on ILD's own machine, where a terminal is started by the caller with the command line an agent launch would have.</summary>
public interface ILocalTerminalCommands
{
    /// <summary>The command line that runs <paramref name="fileName"/> as an agent launch would, for a terminal to start (the provider sign-in terminal), with the egress rules of <paramref name="providerId"/> under <paramref name="mode"/>.</summary>
    AgentIsolation.AgentCommand RouteTerminalCommand(string fileName, IReadOnlyList<string> arguments, Guid? providerId, NetworkMode mode);
}

// Callers that use several roles together depend on one of these, so they take one parameter and a test makes one fake. Each is only roles put together; none has a member of its own.

/// <summary>What the agent adapters and the preview do with a run: run commands in it, read and write its files, find its preview and which run a path is in.</summary>
public interface IRunWorkspace : IRunCommands, IRunFiles, IRunHolders, IPreviewHost, IRunHostIdentity;

/// <summary>What the chat clone does: a chat's folder and toolchains, the git commands run in it, and its files.</summary>
public interface IRunChats : IChatHost, IRunLifecycle, IRunCommands, IRunFiles, IRunHolders;

/// <summary>What the chat service does: a chat's folder and toolchains, its files, and whether a path is in a run or chat.</summary>
public interface IRunChatFolders : IChatHost, IRunLifecycle, IRunFiles, IRunHolders;

/// <summary>What the run reclaimer does: end a run's environment and its preview state, and delete its files.</summary>
public interface IRunReclaim : IRunLifecycle, IRunFiles, IPreviewHost;

/// <summary>What the repository manager does: git in a clone the host vouches for, bundles in and out of it, and the files of a worktree.</summary>
public interface IRunRepositories : IRunBundles, IRunCommands, IRunFiles, IRunHolders, IRunHostIdentity;

/// <summary>What the terminals do: a PTY, and the files of the folder it works in.</summary>
public interface IRunShells : IRunTerminals, IRunFiles;
