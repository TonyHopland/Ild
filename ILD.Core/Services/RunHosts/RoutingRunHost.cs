using ILD.Core.Services.RunHosts.Local;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Data.Enums;
using Porta.Pty;

namespace ILD.Core.Services.RunHosts;

/// <summary>
/// Sends each call to the host that holds what it is about. A loop run lives in its pod or container, and so does a chat when the host keeps chats; everything else (a chat from before, a
/// scheduled job, the provider sign-in terminal) stays on the local host. What a call is about is told by the path it
/// names, which for a run or chat is below its directory, or by the run it is for.
/// </summary>
public sealed class RoutingRunHost(IRunHost local, ExecRunHost isolated) : IRunHost
{
    public RunHostKind Kind => isolated.Kind;

    public ExecRunHost Isolated => isolated;

    private static bool InRun(string? path) => RunPaths.OwnerOf(path) is not null;

    // A chat's folder is in the isolated host only when it keeps chats; a chat from before, or on a host that does not, has a path on this disk.
    private bool OnIsolated(string? path) => InRun(path) || isolated.HostsChats && RunPaths.ChatOf(path) is not null;

    private IRunHost For(string? path) => OnIsolated(path) ? isolated : local;

    // -- Lifecycle ---------------------------------------------------------------

    public Task<RunEnvironment> CreateAsync(RunEnvironmentRequest request, CancellationToken ct = default)
        => isolated.CreateAsync(request, ct);

    // A run's environment, or a path the run's row kept from before the host was switched, so a worktree on this disk still goes.
    public Task DeleteAsync(RunEnvironment environment, CancellationToken ct = default)
        => For(environment.WorkingDirectory).DeleteAsync(environment, ct);

    // -- Commands ----------------------------------------------------------------

    // A command with no working directory goes where the run or chat it is for lives; without one it would start on this machine.
    public async Task<IRunProcess> StartCommandAsync(RunCommand command, CancellationToken ct = default)
    {
        var directory = command.Start.WorkingDirectory;
        var onIsolated = string.IsNullOrEmpty(directory) && command.OwnerId != Guid.Empty
            ? await isolated.HeldOwnerAsync(command.OwnerId, ct) is not null
            : OnIsolated(directory);
        return await (onIsolated ? isolated : local).StartCommandAsync(command, ct);
    }

    public async Task<int> KillAllAsync(Guid ownerId, CancellationToken ct = default)
        => await local.KillAllAsync(ownerId, ct) + await isolated.KillAllAsync(ownerId, ct);

    // -- Terminal ----------------------------------------------------------------

    public Task<IPtyConnection> OpenTerminalAsync(PtyOptions options, CancellationToken ct = default)
        => For(options.Cwd).OpenTerminalAsync(options, ct);

    public AgentIsolation.AgentCommand RouteTerminalCommand(string fileName, IReadOnlyList<string> arguments, Guid? providerId, NetworkMode mode)
        => local.RouteTerminalCommand(fileName, arguments, providerId, mode);

    // -- Bundles -----------------------------------------------------------------

    public Task<RunHostResult> PushBundleAsync(string targetPath, string originUrl, Stream bundle, CancellationToken ct = default)
        => For(targetPath).PushBundleAsync(targetPath, originUrl, bundle, ct);

    public Task<RunHostResult> FetchBundleAsync(string clonePath, Stream bundle, CancellationToken ct = default)
        => For(clonePath).FetchBundleAsync(clonePath, bundle, ct);

    public Task<RunHostResult> PullBundleAsync(string clonePath, string branch, bool incremental, Stream sink, CancellationToken ct = default)
        => For(clonePath).PullBundleAsync(clonePath, branch, incremental, sink, ct);

    // -- Tools -------------------------------------------------------------------

    public Task<bool> IsRunCloneAsync(string path, CancellationToken ct = default)
        => For(path).IsRunCloneAsync(path, ct);

    public Task<RunHostOutput> RunToolAsync(string runPath, string fileName, IReadOnlyList<string> arguments, RunToolStreams? streams = null, CancellationToken ct = default)
        => For(runPath).RunToolAsync(runPath, fileName, arguments, streams, ct);

    // -- Files -------------------------------------------------------------------

    public Task<string?> ReadTextFileAsync(string path, CancellationToken ct = default) => For(path).ReadTextFileAsync(path, ct);

    public Task<Stream?> OpenFileForReadAsync(string path, CancellationToken ct = default) => For(path).OpenFileForReadAsync(path, ct);

    public Task<Stream> OpenFileForWriteAsync(string path, CancellationToken ct = default) => For(path).OpenFileForWriteAsync(path, ct);

    public Task<long?> GetFileLengthAsync(string path, CancellationToken ct = default) => For(path).GetFileLengthAsync(path, ct);

    public Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default) => For(path).OpenReadAsync(path, ct);

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

    // A chat's folder goes where chats are kept: the isolated host when it keeps them, this disk otherwise.
    public Task<string> CreateChatFolderAsync(Guid chatId, string localRoot, CancellationToken ct = default)
        => (isolated.HostsChats ? isolated : local).CreateChatFolderAsync(chatId, localRoot, ct);

    public Task AddChatToolsAsync(string chatPath, string basePath, CancellationToken ct = default)
        => For(chatPath).AddChatToolsAsync(chatPath, basePath, ct);

    public Task<IReadOnlyList<string>> ListDirectoriesAsync(string directory, CancellationToken ct = default)
        => For(directory).ListDirectoriesAsync(directory, ct);

    public Task<bool> EntryExistsAsync(string path, CancellationToken ct = default) => For(path).EntryExistsAsync(path, ct);

    // -- Preview and gateway -----------------------------------------------------

    public Task<Uri> PreviewTargetAsync(string workingDirectory, int port, CancellationToken ct = default)
        => For(workingDirectory).PreviewTargetAsync(workingDirectory, port, ct);

    // The local host answers with the operator's setting, which for this backend is ILD's Service address too.
    public string GatewayApiUrl(string? configuredApiUrl) => local.GatewayApiUrl(configuredApiUrl);

    public string? RunHomeFor(string workingDirectory) => For(workingDirectory).RunHomeFor(workingDirectory);

    // -- Sweep -------------------------------------------------------------------

    public async Task<IReadOnlyList<RunLeftover>> ListLeftoversAsync(IReadOnlySet<Guid> liveIds, CancellationToken ct = default)
        => [.. await local.ListLeftoversAsync(liveIds, ct), .. await isolated.ListLeftoversAsync(liveIds, ct)];

    public Task<bool> DeleteLeftoverAsync(RunLeftover leftover, CancellationToken ct = default)
        => leftover.Kind == isolated.LeftoverKind ? isolated.DeleteLeftoverAsync(leftover, ct) : local.DeleteLeftoverAsync(leftover, ct);
}
