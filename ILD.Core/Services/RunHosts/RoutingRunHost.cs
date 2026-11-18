using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.RunHosts.Local;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Data.Enums;
using Porta.Pty;

namespace ILD.Core.Services.RunHosts;

/// <summary>
/// Sends each call to the host that holds what it is about. A loop run lives in its pod or container, and so does a chat when the host keeps chats; everything else is on ILD's own disk (a run or chat from before containers, a scheduled job), where the local host may read and delete
/// but never execute: a call that would start a process there is refused. What a call is about is told by the path it
/// names, which for a run or chat is below its directory, or by the run it is for.
/// </summary>
public sealed class RoutingRunHost(IRunHost local, ExecRunHost isolated) : IRunHost, IRunHostOperator, IRunSweep, ISignInTerminalHost
{
    public RunHostKind Kind => isolated.Kind;

    // A chat's folder is in the isolated host only when it keeps chats; a chat from before, or on a host that does not, has a path on this disk.
    private bool OnIsolated(string? path) => !RunHolders.FromPath(path, isolated.HostsChats).IsOwnDisk;

    public bool PredatesContainers(string? path) => !string.IsNullOrEmpty(path) && !OnIsolated(path);

    // For ILD's own base clones only, which the repository manager finds by the repository's row: no call that names a path reaches this disk for execution.
    public IRunRepositories? OwnDiskHostFor(string? path) => OnIsolated(path) ? null : local;

    public string? AttachTargetFor(string? path) => isolated.AttachTargetFor(path);

    // -- What the isolated host says of itself --------------------------------------

    public TimeSpan SweepInterval => isolated.SweepInterval;

    public Task<IReadOnlyList<ILD.Core.Services.Implementations.Network.IpRange>> IsolatedRangesAsync(CancellationToken ct = default) => isolated.IsolatedRangesAsync(ct);

    public Task<IReadOnlyList<System.Net.IPAddress>> ControlPlaneAddressesAsync(CancellationToken ct = default) => isolated.ControlPlaneAddressesAsync(ct);

    public Task<string?> WarningAsync(CancellationToken ct = default) => isolated.WarningAsync(ct);

    public Task<IReadOnlyList<DirectRoute>> DirectRoutesAsync(CancellationToken ct = default) => isolated.DirectRoutesAsync(ct);

    public Task<SignInTerminal> OpenSignInAsync(ManagedAgent? agent, string command, IReadOnlyList<string> arguments, int cols, int rows, CancellationToken ct)
        => isolated.OpenSignInAsync(agent, command, arguments, cols, rows, ct);

    public Task ShutdownAsync(CancellationToken ct) => isolated.ShutdownAsync(ct);

    public Task CheckAsync(CancellationToken ct = default) => isolated.CheckAsync(ct);

    public Task WarmAgentToolsAsync(CancellationToken ct = default) => isolated.WarmAgentToolsAsync(ct);

    public Task ReapIdleChatAsync(CancellationToken ct = default) => isolated.ReapIdleChatAsync(ct);

    // The sweep of a host with environments of its own looks at those alone; what is left on ILD's own disk is found at startup (IRunLeftovers).
    Task<IReadOnlyList<RunLeftover>> IRunSweep.ListLeftoversAsync(IReadOnlySet<Guid> liveIds, CancellationToken ct) => isolated.ListLeftoversAsync(liveIds, ct);

    Task<bool> IRunSweep.DeleteLeftoverAsync(RunLeftover leftover, CancellationToken ct) => isolated.DeleteLeftoverAsync(leftover, ct);

    private IRunHost For(string? path) => OnIsolated(path) ? isolated : local;

    // Anything that executes, or readies a file to execute: a path outside the isolated host is on this disk, where nothing of a run or chat starts.
    private IRunHost ForExecution(string? path) => OnIsolated(path) ? isolated : throw new PredatesContainersException();

    // -- Lifecycle ---------------------------------------------------------------

    public Task<RunEnvironment> CreateAsync(RunEnvironmentRequest request, CancellationToken ct = default)
        => isolated.CreateAsync(request, ct);

    // Audit of what still goes to the local host, and why that is safe: it deletes, ends, reads or only works out a path, and nothing it does makes a file that anything still launches.
    // A run's environment, or a path the run's row kept from before the host was switched, so a worktree on this disk still goes.
    public Task DeleteAsync(RunEnvironment environment, CancellationToken ct = default)
        => For(environment.WorkingDirectory).DeleteAsync(environment, ct);

    public Task<bool> CheckpointAsync(Guid runId, CancellationToken ct = default) => isolated.CheckpointAsync(runId, ct);

    public Task<bool> IsRunningAsync(RunEnvironment environment, CancellationToken ct = default)
        => For(environment.WorkingDirectory).IsRunningAsync(environment, ct);

    // -- Commands ----------------------------------------------------------------

    // A command with no working directory goes where the run or chat it is for lives; without one it would start on this machine.
    // One that must run in a run goes to that run or nowhere: never to this machine, whatever its directory says.
    public async Task<IRunProcess> StartCommandAsync(RunCommand command, CancellationToken ct = default)
    {
        var holder = await RunHolders.ForCommandAsync(command, isolated.HostsChats, routed: true, isolated.Kind.ToString().ToLowerInvariant(), isolated.HeldOwnerAsync, ct);
        if (holder.IsOwnDisk)
            throw new PredatesContainersException();
        return await isolated.StartResolvedAsync(command, holder, ct);
    }

    // Ends processes: the local host has none of a run or chat any more, and ending is never executing.
    public async Task<int> KillAllAsync(Guid ownerId, CancellationToken ct = default)
        => await local.KillAllAsync(ownerId, ct) + await isolated.KillAllAsync(ownerId, ct);

    public Task<int> KillUnownedAsync(Guid runIn, CancellationToken ct = default)
        => isolated.KillUnownedAsync(runIn, ct);

    // The children of a run's agent live where the run does: its pod or container, otherwise this machine.
    public async Task<IReadOnlyList<RunChildProcess>> ListAgentChildrenAsync(Guid ownerId, string launchId, IReadOnlyList<string> ignore, CancellationToken ct = default)
        => await (await HostOf(ownerId, ct)).ListAgentChildrenAsync(ownerId, launchId, ignore, ct);

    public async Task<IReadOnlyList<RunChildProcess>> StillRunningAsync(Guid ownerId, IReadOnlyList<RunChildProcess> processes, CancellationToken ct = default)
        => await (await HostOf(ownerId, ct)).StillRunningAsync(ownerId, processes, ct);

    public async Task StopProcessesAsync(Guid ownerId, IReadOnlyList<RunChildProcess> processes, CancellationToken ct = default)
        => await (await HostOf(ownerId, ct)).StopProcessesAsync(ownerId, processes, ct);

    public async Task<string?> ReadTailAsync(Guid ownerId, string path, int maxBytes, CancellationToken ct = default)
        => await (await HostOf(ownerId, ct)).ReadTailAsync(ownerId, path, maxBytes, ct);

    private async Task<IRunHost> HostOf(Guid ownerId, CancellationToken ct)
        => await isolated.HeldOwnerAsync(ownerId, ct) is not null ? isolated : local;

    public Task<ContainerStats?> GetStatsAsync(Guid owner, CancellationToken ct = default) => isolated.GetStatsAsync(owner, ct);

    public Task<RunTermination?> GetTerminationAsync(Guid ownerId, DateTimeOffset? since = null, CancellationToken ct = default) => isolated.GetTerminationAsync(ownerId, since, ct);

    // -- Terminal ----------------------------------------------------------------

    public Task<IPtyConnection> OpenTerminalAsync(PtyOptions options, NetworkMode mode, CancellationToken ct = default)
        => ForExecution(options.Cwd).OpenTerminalAsync(options, mode, ct);

    // -- Bundles -----------------------------------------------------------------

    public Task<RunHostResult> PushBundleAsync(string targetPath, string originUrl, Stream bundle, CancellationToken ct = default)
        => ForExecution(targetPath).PushBundleAsync(targetPath, originUrl, bundle, ct);

    public Task<RunHostResult> FetchBundleAsync(string clonePath, Stream bundle, CancellationToken ct = default)
        => ForExecution(clonePath).FetchBundleAsync(clonePath, bundle, ct);

    public Task<RunHostResult> PullBundleAsync(string clonePath, string branch, bool incremental, Stream sink, CancellationToken ct = default)
        => ForExecution(clonePath).PullBundleAsync(clonePath, branch, incremental, sink, ct);

    // -- Tools -------------------------------------------------------------------

    // Reads a path: a question about history, no execution.
    public Task<bool> IsRunCloneAsync(string path, CancellationToken ct = default)
        => For(path).IsRunCloneAsync(path, ct);

    public Task<RunHostOutput> RunToolAsync(string runPath, string fileName, IReadOnlyList<string> arguments, RunToolStreams? streams = null, CancellationToken ct = default)
        => ForExecution(runPath).RunToolAsync(runPath, fileName, arguments, streams, ct);

    // -- Files -------------------------------------------------------------------

    // Reads and writes of files on this disk: reading an old chat's or run's history, and writes no launch reads back; executing in them is refused above.
    public Task<string?> ReadTextFileAsync(string path, CancellationToken ct = default) => For(path).ReadTextFileAsync(path, ct);

    public Task<Stream?> OpenFileForReadAsync(string path, CancellationToken ct = default) => For(path).OpenFileForReadAsync(path, ct);

    public Task<Stream> OpenFileForWriteAsync(string path, CancellationToken ct = default) => For(path).OpenFileForWriteAsync(path, ct);

    public Task<long?> GetFileLengthAsync(string path, CancellationToken ct = default) => For(path).GetFileLengthAsync(path, ct);

    public Task ReadFileRangeAsync(string path, long offset, long count, Func<Stream, Task> consume)
        => For(path).ReadFileRangeAsync(path, offset, count, consume);

    public Task WriteFileAsync(string path, Stream content, CancellationToken ct = default) => For(path).WriteFileAsync(path, content, ct);

    public Task WriteFileAsync(string path, string content, CancellationToken ct = default) => For(path).WriteFileAsync(path, content, ct);

    public Task WriteReadOnlyFileAsync(string path, string content, CancellationToken ct = default) => For(path).WriteReadOnlyFileAsync(path, content, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => For(path).CreateDirectoryAsync(path, ct);

    public Task<bool> FileExistsAsync(string path, CancellationToken ct = default) => For(path).FileExistsAsync(path, ct);

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default) => For(path).DirectoryExistsAsync(path, ct);

    public Task<IReadOnlyList<(string Path, string FirstLine)>> ListFilesAsync(string directory, string namePattern, CancellationToken ct = default)
        => For(directory).ListFilesAsync(directory, namePattern, ct);

    public async Task<bool> DeleteAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var all = paths.ToList();
        var inRun = all.Where(OnIsolated).ToList();
        var onDisk = all.Where(p => !OnIsolated(p)).ToList();
        var runsGone = inRun.Count == 0 || await isolated.DeleteAsync(inRun, ct);
        var diskGone = onDisk.Count == 0 || await local.DeleteAsync(onDisk, ct);
        return runsGone && diskGone;
    }

    // Deleting an old worktree or chat folder is the way to be rid of it, so it still goes to this disk.
    // A chat's folder goes where chats are kept: the isolated host when it keeps them, this disk otherwise.
    public string ChatFolderPath(Guid chatId, string localRoot)
        => (isolated.HostsChats ? isolated : local).ChatFolderPath(chatId, localRoot);

    public Task<string> CreateChatFolderAsync(Guid chatId, string localRoot, CancellationToken ct = default)
        => (isolated.HostsChats ? isolated : local).CreateChatFolderAsync(chatId, localRoot, ct);

    public IDisposable HoldChat(string chatPath) => ForExecution(chatPath).HoldChat(chatPath);

    public Task AddChatToolsAsync(string chatPath, string basePath, CancellationToken ct = default)
        => ForExecution(chatPath).AddChatToolsAsync(chatPath, basePath, ct);

    public Task<IReadOnlyList<string>> ListDirectoriesAsync(string directory, CancellationToken ct = default)
        => For(directory).ListDirectoriesAsync(directory, ct);

    public Task<bool> EntryExistsAsync(string path, CancellationToken ct = default) => For(path).EntryExistsAsync(path, ct);

    // -- Preview and gateway -----------------------------------------------------

    // Work out a path or an address and start nothing; a preview command carries RunIn and is refused above.
    public Task<Uri> PreviewTargetAsync(string workingDirectory, int port, CancellationToken ct = default)
        => For(workingDirectory).PreviewTargetAsync(workingDirectory, port, ct);

    // The CLI's own host answers: a run or chat reaches ILD by the name on its NO_PROXY, which need not be the operator's
    // setting (compose's ild is ild-api there) and does not resolve on this machine, where ILD is the setting.
    public string GatewayApiUrl(string? configuredApiUrl, string? workingDirectory)
        => For(workingDirectory).GatewayApiUrl(configuredApiUrl, workingDirectory);

    public string? RunHomeFor(string workingDirectory) => For(workingDirectory).RunHomeFor(workingDirectory);

    public string PreviewStateDirectoryFor(string worktreePath) => For(worktreePath).PreviewStateDirectoryFor(worktreePath);

    public Task<string?> PreviewDataFolderAsync(Guid repositoryId, string worktreePath, CancellationToken ct = default)
        => For(worktreePath).PreviewDataFolderAsync(repositoryId, worktreePath, ct);

    // -- Sweep -------------------------------------------------------------------

    // The sweep lists and deletes leftovers, which is removal only.
    public async Task<IReadOnlyList<RunLeftover>> ListLeftoversAsync(IReadOnlySet<Guid> liveIds, CancellationToken ct = default)
        => [.. await local.ListLeftoversAsync(liveIds, ct), .. await isolated.ListLeftoversAsync(liveIds, ct)];

    public Task<bool> DeleteLeftoverAsync(RunLeftover leftover, CancellationToken ct = default)
        => leftover.Kind == isolated.LeftoverKind ? isolated.DeleteLeftoverAsync(leftover, ct) : local.DeleteLeftoverAsync(leftover, ct);
}
