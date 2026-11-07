using ILD.Core.Services.Implementations;
using ILD.Core.Services.RunHosts.Kubernetes;
using ILD.Data.Enums;
using Porta.Pty;

namespace ILD.Core.Services.RunHosts;

/// <summary>
/// Sends each call to the host that holds what it is about. A loop run lives in its pod and everything else (a chat, a
/// scheduled job, the provider sign-in terminal) stays on the local host. What a call is about is told by the path it
/// names, which for a pod is below that run's directory, or by the run it is for.
/// </summary>
public sealed class RoutingRunHost(IRunHost local, KubernetesRunHost kubernetes) : IRunHost
{
    public RunHostKind Kind => RunHostKind.Kubernetes;

    public KubernetesRunHost Kubernetes => kubernetes;

    private static bool InPod(string? path) => KubernetesNames.OwnerOf(path) is not null;

    private IRunHost For(string? path) => InPod(path) ? kubernetes : local;

    // -- Lifecycle ---------------------------------------------------------------

    public Task<RunEnvironment> CreateAsync(RunEnvironmentRequest request, CancellationToken ct = default)
        => kubernetes.CreateAsync(request, ct);

    // A run's environment, or a path the run's row kept from before the host was switched, so a worktree on this disk still goes.
    public Task DeleteAsync(RunEnvironment environment, CancellationToken ct = default)
        => For(environment.WorkingDirectory).DeleteAsync(environment, ct);

    // -- Commands ----------------------------------------------------------------

    public Task<IRunProcess> StartCommandAsync(RunCommand command, CancellationToken ct = default)
        => (InPod(command.Start.WorkingDirectory) ? kubernetes : local).StartCommandAsync(command, ct);

    public async Task<int> KillAllAsync(Guid ownerId, CancellationToken ct = default)
        => await local.KillAllAsync(ownerId, ct) + await kubernetes.KillAllAsync(ownerId, ct);

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

    public string? ReadTextFile(string path) => For(path).ReadTextFile(path);

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
        var inPod = all.Where(InPod).ToList();
        var onDisk = all.Where(p => !InPod(p)).ToList();
        var podsGone = inPod.Count == 0 || await kubernetes.DeleteAsync(inPod, ct);
        var diskGone = onDisk.Count == 0 || await local.DeleteAsync(onDisk, ct);
        return podsGone && diskGone;
    }

    // -- Preview and gateway -----------------------------------------------------

    public Task<Uri> PreviewTargetAsync(string workingDirectory, int port, CancellationToken ct = default)
        => For(workingDirectory).PreviewTargetAsync(workingDirectory, port, ct);

    // The local host answers with the operator's setting, which for this backend is ILD's Service address too.
    public string GatewayApiUrl(string? configuredApiUrl) => local.GatewayApiUrl(configuredApiUrl);

    // -- Sweep -------------------------------------------------------------------

    public async Task<IReadOnlyList<RunLeftover>> ListLeftoversAsync(IReadOnlySet<Guid> liveIds, CancellationToken ct = default)
        => [.. await local.ListLeftoversAsync(liveIds, ct), .. await kubernetes.ListLeftoversAsync(liveIds, ct)];

    public Task<bool> DeleteLeftoverAsync(RunLeftover leftover, CancellationToken ct = default)
        => leftover.Kind == KubernetesRunHost.RunLeftoverKind ? kubernetes.DeleteLeftoverAsync(leftover, ct) : local.DeleteLeftoverAsync(leftover, ct);
}
