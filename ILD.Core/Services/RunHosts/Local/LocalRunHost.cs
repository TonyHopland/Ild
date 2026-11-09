using ILD.Core.Services.Implementations.Files;
using System.Collections.Concurrent;
using System.Diagnostics;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.PackageFeeds;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Porta.Pty;

namespace ILD.Core.Services.RunHosts.Local;

/// <summary>
/// The run host for a run that lives on the machine ILD runs on, as when a developer runs ILD with
/// <c>dotnet run</c>: the agent's processes are children of ILD, and its files sit in the worktree and
/// the scratch and agent-read roots of <see cref="AgentIsolation"/>.
/// </summary>
public sealed class LocalRunHost : IRunHost
{
    /// <summary>The host of a caller built without DI — unit tests, tools — that has no repository manager to make worktrees with.</summary>
    public static LocalRunHost Shared { get; } = new();

    private readonly IProcessRunner? _runner;
    private readonly IServiceProvider? _services;
    private readonly IRepositoryManager? _repositories;
    private readonly IConfiguration? _configuration;
    private readonly ProcessRunOptions _agentOptions;
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<LocalRunProcess, byte>> _running = new();
    // The runs this process made and has not deleted: after a crash or a restart of ILD a run is not among them.
    private readonly ConcurrentDictionary<Guid, byte> _made = new();

    /// <param name="runner">What bundles are moved with; the git and shell scripts of a clone run through it.</param>
    /// <param name="services">Where <see cref="CreateAsync"/> and <see cref="DeleteAsync"/> find the repository manager, which uses this host.</param>
    /// <param name="repositories">The repository manager to make worktrees with, when it is not to be found through <paramref name="services"/>.</param>
    /// <param name="configuration">Where the preview data repositories keep is found, when it is not to be found through <paramref name="services"/>.</param>
    public LocalRunHost(IProcessRunner? runner = null, IServiceProvider? services = null, IRepositoryManager? repositories = null, IConfiguration? configuration = null)
    {
        _configuration = configuration;
        _runner = runner;
        _services = services;
        _repositories = repositories;
        _agentOptions = new ProcessRunOptions(AgentScoped: true);
    }

    public RunHostKind Kind => RunHostKind.Local;

    // -- Lifecycle ---------------------------------------------------------------

    public async Task<RunEnvironment> CreateAsync(RunEnvironmentRequest request, CancellationToken ct = default)
    {
        var path = await Repositories().CreateWorktreeAsync(request.BasePath, request.BranchName);
        if (request.KeepPreviewData)
            await FillPreviewDataAsync(request.OwnerId, path, request.RepositoryId, ct);
        _made.TryAdd(request.OwnerId, 0);
        return new RunEnvironment(request.OwnerId, path);
    }

    public Task<bool> IsRunningAsync(RunEnvironment environment, CancellationToken ct = default)
        => Task.FromResult(_made.ContainsKey(environment.OwnerId));

    public async Task DeleteAsync(RunEnvironment environment, CancellationToken ct = default)
    {
        await KillAllAsync(environment.OwnerId, ct);
        await SyncPreviewDataBackAsync(environment, ct);
        _made.TryRemove(environment.OwnerId, out _);
        await Repositories().DestroyWorktreeAsync(environment.WorkingDirectory);
    }

    private IRepositoryManager Repositories()
        => _repositories
            ?? _services?.GetService<IRepositoryManager>()
            ?? throw new InvalidOperationException("this run host was built without a repository manager, so it cannot make or remove worktrees");

    // -- Commands ----------------------------------------------------------------

    public async Task<IRunProcess> StartCommandAsync(RunCommand command, CancellationToken ct = default)
    {
        var psi = command.AsAgent ? PrepareAgentLaunch(command.Start, command.ProviderId, command.NetworkMode, command.Environment) : WithEnvironment(command.Start, command.Environment);
        var process = await StartAsync(psi, ct);
        var tracked = new LocalRunProcess(process, Forget(command.OwnerId));
        if (command.OwnerId != Guid.Empty)
            _running.GetOrAdd(command.OwnerId, _ => new()).TryAdd(tracked, 0);
        return tracked;
    }

    // Linux refuses to run a file that some process still has open for writing, and a child forked by another thread
    // holds a copy of every descriptor until it execs, so a binary written a moment ago can be refused for a few
    // milliseconds whoever wrote it: a CLI just installed, a script just made.
    private const int TextFileBusy = 26;
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromMilliseconds(25);
    private const int BusyRetries = 40;

    internal static async Task<Process> StartAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == TextFileBusy && attempt < BusyRetries)
            {
                await Task.Delay(BusyRetryDelay, ct);
            }
        }
    }

    /// <summary>The launch of an agent CLI: <paramref name="psi"/> with the execution's environment and its network mode's proxy.</summary>
    internal static ProcessStartInfo PrepareAgentLaunch(
        ProcessStartInfo psi, Guid? providerId, ILD.Data.Enums.NetworkMode mode, IReadOnlyDictionary<string, string>? environment)
    {
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            psi.Environment[name] = value;
        return AgentIsolation.ApplyLaunchEnvironment(psi, providerId, mode);
    }

    private static ProcessStartInfo WithEnvironment(ProcessStartInfo psi, IReadOnlyDictionary<string, string>? environment)
    {
        WithUserTools(psi);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            psi.Environment[name] = value;
        return psi;
    }

    /// <summary>
    /// What a run container gives every command when it is made (<see cref="Tools.RunToolEnvironment.For"/>), here: a
    /// home, an npm prefix in it, so <c>npm install -g</c> needs no write access to the Node install, and its bin first
    /// on <c>PATH</c>, so what was installed there is found. The caller's own values are applied after, so they win.
    /// </summary>
    internal static void WithUserTools(ProcessStartInfo psi)
    {
        var home = psi.Environment.TryGetValue("HOME", out var inherited) && !string.IsNullOrWhiteSpace(inherited)
            ? inherited
            // Never a fixed path in world-writable /tmp: npm would read a planted ~/.npmrc.
            : AgentIsolation.CreatePrivateDirectory("home");
        var local = Path.Combine(home, ".local");
        var bin = Path.Combine(local, "bin");
        // Made up front: a tool that walks PATH (Claude Code, for one) warns about an entry that is not there.
        Directory.CreateDirectory(bin);

        psi.Environment["HOME"] = home;
        psi.Environment["NPM_CONFIG_PREFIX"] = local;
        // First even when ILD's own PATH already ends with it, so a tool the command installed beats one ILD's image ships.
        var rest = (psi.Environment.TryGetValue("PATH", out var current) ? current ?? "" : "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !string.Equals(segment, bin, StringComparison.Ordinal));
        psi.Environment["PATH"] = string.Join(Path.PathSeparator, rest.Prepend(bin));
    }

    private Action<LocalRunProcess> Forget(Guid ownerId) => process =>
    {
        if (_running.TryGetValue(ownerId, out var set))
            set.TryRemove(process, out _);
    };

    // Long enough that only a tree stuck in the kernel (an uninterruptible write to a slow mount) outlasts
    // it, short enough that a node being retried does not wait on it.
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(5);

    public async Task<int> KillAllAsync(Guid ownerId, CancellationToken ct = default)
    {
        if (!_running.TryGetValue(ownerId, out var set)) return 0;
        var killed = 0;
        foreach (var process in set.Keys.ToList())
        {
            if (process.HasExitedSafe()) continue;
            process.TryKill();
            killed++;
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
            grace.CancelAfter(KillGrace);
            try { await process.WaitForExitAsync(grace.Token); }
            catch (OperationCanceledException) { /* still alive after the grace: the next attempt sees it */ }
        }
        return killed;
    }

    // -- Terminal ----------------------------------------------------------------

    public Task<IPtyConnection> OpenTerminalAsync(PtyOptions options, CancellationToken ct = default)
        => PtyProvider.SpawnAsync(options, ct);

    public AgentIsolation.AgentCommand RouteTerminalCommand(string fileName, IReadOnlyList<string> arguments, Guid? providerId, ILD.Data.Enums.NetworkMode mode)
        => AgentIsolation.TerminalCommand(fileName, arguments, providerId, mode);

    // -- Bundles -----------------------------------------------------------------

    // The bundle arrives on stdin; git cannot read one from a pipe, so it goes to a file in a scratch
    // directory the agent makes and removes. An empty stdin is a remote with no branches yet.
    internal const string CloneScript = """
        d=$(mktemp -d) || exit 1
        trap 'rm -rf -- "$d"' EXIT
        cat > "$d/in.bundle" || exit 1
        if [ -s "$d/in.bundle" ]; then
            git clone --quiet -- "$d/in.bundle" "$1" || exit 1
            git -C "$1" config remote.origin.url "$2"
        else
            git init --quiet --initial-branch=main -- "$1" || exit 1
            git -C "$1" remote add origin "$2"
        fi
        """;

    // The bundle arrives on stdin and is written to a scratch directory of the agent's, because git
    // cannot fetch from a pipe. Into a namespace of its own: nothing the clone has is touched here.
    internal const string FetchBundleScript = """
        d=$(mktemp -d) || exit 1
        trap 'rm -rf -- "$d"' EXIT
        git -C "$1" for-each-ref --format='delete %(refname)' refs/ild-sync | git -C "$1" update-ref --stdin || exit 1
        cat > "$d/in.bundle" || exit 1
        git -C "$1" fetch --quiet --no-tags --no-write-fetch-head -- "$d/in.bundle" '+refs/heads/*:refs/ild-sync/*'
        """;

    public async Task<RunHostResult> PushBundleAsync(string targetPath, string originUrl, Stream bundle, CancellationToken ct = default)
    {
        var r = await Runner().RunAsync(
            "/bin/sh", ["-c", CloneScript, "sh", Path.GetFullPath(targetPath), originUrl], "/", ct, null,
            _agentOptions with { StandardInputFrom = bundle });
        return new RunHostResult(r.ExitCode, r.StdErr);
    }

    public async Task<RunHostResult> FetchBundleAsync(string clonePath, Stream bundle, CancellationToken ct = default)
    {
        var r = await Runner().RunAsync(
            "/bin/sh", ["-c", FetchBundleScript, "sh", Path.GetFullPath(clonePath)], "/", ct, null,
            _agentOptions with { StandardInputFrom = bundle });
        return new RunHostResult(r.ExitCode, r.StdErr);
    }

    public async Task<RunHostResult> PullBundleAsync(string clonePath, string branch, bool incremental, Stream sink, CancellationToken ct = default)
    {
        string[] bundle = ["bundle", "create", "--quiet", "-", $"refs/heads/{branch}"];
        IReadOnlyList<string> args = ["-C", Path.GetFullPath(clonePath), .. bundle, .. incremental ? new[] { "--not", "--remotes=origin" } : []];
        var r = await Runner().RunAsync("git", args, "/", ct, null, _agentOptions with { StandardOutputTo = sink });
        return new RunHostResult(r.ExitCode, r.StdErr);
    }

    private IProcessRunner Runner()
        => _runner ?? throw new InvalidOperationException("this run host was built without a process runner, so it cannot move bundles");

    // -- Tools -------------------------------------------------------------------

    public Task<bool> IsRunCloneAsync(string path, CancellationToken ct = default)
        => Task.FromResult(LocalWorktrees.TrustedRepositoryRoot(path) is not null);

    // From "/" and with the directory addressed by the caller's own arguments: the orchestrator never enters one the agent owns.
    public async Task<RunHostOutput> RunToolAsync(
        string runPath, string fileName, IReadOnlyList<string> arguments, RunToolStreams? streams = null, CancellationToken ct = default)
    {
        var options = _agentOptions with { StandardInputFrom = streams?.StandardInputFrom, StandardOutputTo = streams?.StandardOutputTo };
        var r = await Runner().RunAsync(fileName, arguments, "/", ct, null, options);
        return new RunHostOutput(r.ExitCode, r.StdOut, r.StdErr);
    }

    // -- Files -------------------------------------------------------------------

    public Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default)
        => Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);

    public async Task<string?> ReadTextFileAsync(string path, CancellationToken ct = default)
        => File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;

    public Task<Stream?> OpenFileForReadAsync(string path, CancellationToken ct = default)
    {
        if (Directory.Exists(path)) return Task.FromResult<Stream?>(null);
        try
        {
            // Opened rather than checked for first: the handle is the answer, so a file that goes between a
            // check and the open cannot surface as an exception further down.
            return Task.FromResult<Stream?>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    public Task<Stream> OpenFileForWriteAsync(string path, CancellationToken ct = default)
        => Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite));

    public Task<long?> GetFileLengthAsync(string path, CancellationToken ct = default)
        => AgentWritableFiles.GetFileLengthAsync(path, ct);

    public Task ReadFileRangeAsync(string path, long offset, long count, Func<Stream, Task> consume)
        => AgentWritableFiles.ReadFileRangeAsync(path, offset, count, consume);

    public Task WriteFileAsync(string path, Stream content, CancellationToken ct = default)
        => AgentWritableFiles.WriteFileAsync(path, content, ct);

    public Task WriteFileAsync(string path, string content, CancellationToken ct = default)
        => AgentWritableFiles.WriteFileAsync(path, content, ct);

    public Task WriteReadOnlyFileAsync(string path, string content, CancellationToken ct = default)
        => AgentWritableFiles.WriteReadOnlyFileAsync(path, content, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        => AgentWritableFiles.CreateDirectoryAsync(path, ct);

    public Task<bool> FileExistsAsync(string path, CancellationToken ct = default)
        => AgentWritableFiles.FileExistsAsync(path, ct);

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default)
        => Task.FromResult(Directory.Exists(path));

    public Task<IReadOnlyList<(string Path, string FirstLine)>> ListFilesAsync(string directory, string namePattern, CancellationToken ct = default)
        => AgentWritableFiles.ListFilesAsync(directory, namePattern, ct);

    public Task<bool> DeleteAsync(IEnumerable<string> paths, CancellationToken ct = default)
        => AgentWritableFiles.DeleteAsync(paths, ct);

    public Task<string> CreateChatFolderAsync(Guid chatId, string localRoot, CancellationToken ct = default)
    {
        var path = Path.GetFullPath(Path.Combine(localRoot, chatId.ToString("N")));
        Directory.CreateDirectory(path);
        return Task.FromResult(path);
    }

    public Task AddChatToolsAsync(string chatPath, string basePath, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<string>> ListDirectoriesAsync(string directory, CancellationToken ct = default)
        => AgentWritableFiles.ListDirectoriesAsync(directory, ct);

    public Task<bool> EntryExistsAsync(string path, CancellationToken ct = default)
        => Task.FromResult(AgentWritableFiles.EntryExists(path));

    // -- Preview and gateway -----------------------------------------------------

    public Task<Uri> PreviewTargetAsync(string workingDirectory, int port, CancellationToken ct = default)
        => Task.FromResult(new Uri($"http://127.0.0.1:{port}"));

    public string? RunHomeFor(string workingDirectory) => null;

    public string GatewayApiUrl(string? configuredApiUrl, string? workingDirectory) => configuredApiUrl ?? "http://localhost:5000";

    public string PreviewStateDirectoryFor(string worktreePath) => WorktreePreviewService.StateDirectoryOf(worktreePath);

    public Task<string?> PreviewDataFolderAsync(Guid repositoryId, string worktreePath, CancellationToken ct = default)
        => Task.FromResult(FindPreviewDataCopy(worktreePath));

    private static string PreviewDataCopiesRoot => Path.Combine(AgentIsolation.ScratchRoot, "preview-data");

    /// <summary>
    /// The run's own copy of the preview data its repository keeps: a folder in scratch named for the run, so the sweep can tell
    /// whose it is, and for its worktree, so a preview, which knows only the worktree, finds it.
    /// </summary>
    internal static string PreviewDataCopyOf(Guid ownerId, string worktreePath)
        => Path.Combine(PreviewDataCopiesRoot, $"{ownerId:N}-{WorktreePreviewService.WorktreeSlug(Path.GetFullPath(worktreePath))}");

    /// <summary>The copy of the run that holds <paramref name="worktreePath"/>; null when it has none.</summary>
    internal static string? FindPreviewDataCopy(string worktreePath)
    {
        var slug = WorktreePreviewService.WorktreeSlug(Path.GetFullPath(worktreePath));
        try
        {
            return Directory.EnumerateDirectories(PreviewDataCopiesRoot, "*-" + slug)
                .FirstOrDefault(folder => new DirectoryInfo(folder).LinkTarget is null);
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The copies of runs not in <paramref name="liveIds"/> made before <paramref name="startedUtc"/>: of a run whose row went without
    /// being reclaimed, or that ILD did not end before it stopped. This process's own copies are never taken.
    /// </summary>
    internal static IEnumerable<string> OrphanedPreviewDataCopies(string scratchRoot, IReadOnlySet<Guid> liveIds, DateTime startedUtc)
    {
        var root = Path.Combine(scratchRoot, "preview-data");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFileSystemEntries(root)
            .Where(entry => Path.GetFileName(entry) is { Length: > 32 } name && name[32] == '-'
                && Guid.TryParseExact(name[..32], "N", out var owner) && !liveIds.Contains(owner)
                && Directory.GetLastWriteTimeUtc(entry) < startedUtc);
    }

    private PreviewDataStore Store()
        => new(PreviewVolumes.Root(
                _configuration
                ?? _services?.GetService<IConfiguration>()
                ?? throw new InvalidOperationException("this run host was built without configuration, so it does not know where preview data is kept")),
            _services?.GetService<ILogger<PreviewDataStore>>());

    private async Task FillPreviewDataAsync(Guid ownerId, string worktreePath, Guid repositoryId, CancellationToken ct)
    {
        var copy = PreviewDataCopyOf(ownerId, worktreePath);
        if (AgentWritableFiles.EntryExists(copy)) return;
        using var file = PrivateTempFile.Create("ild-preview-data-");
        await using (var sink = file.OpenWrite())
            await Store().PackAsync(repositoryId, sink, ct);
        await AgentWritableFiles.CreateDirectoryAsync(copy, ct);
        await using var archive = file.OpenRead();
        await PreviewDataStore.UnpackAsync(archive, copy, ct);
    }

    // The run's commands are ended first, so the copy is not changing while it is packed. Only the copy of a run this process made
    // is put back: one that crashed or outlived a restart of ILD keeps nothing. Whatever happens, the copy goes.
    private async Task SyncPreviewDataBackAsync(RunEnvironment environment, CancellationToken ct)
    {
        var copy = PreviewDataCopyOf(environment.OwnerId, environment.WorkingDirectory);
        if (!AgentWritableFiles.EntryExists(copy)) return;
        try
        {
            if (environment.KeptPreviewDataOf is { } repositoryId && _made.ContainsKey(environment.OwnerId))
            {
                using var file = PrivateTempFile.Create("ild-preview-data-");
                await using (var sink = file.OpenWrite())
                    await PreviewDataStore.PackFolderAsync(copy, sink, ct);
                await using var archive = file.OpenRead();
                await Store().ReplaceAsync(repositoryId, archive, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services?.GetService<ILogger<LocalRunHost>>()?.LogWarning(ex, "The preview data of the run in {Worktree} is not kept: it could not be put back", environment.WorkingDirectory);
        }
        finally
        {
            await AgentWritableFiles.DeleteAsync([copy], CancellationToken.None);
        }
    }

    // -- Sweep -------------------------------------------------------------------

    internal const string McpConfigKind = "mcp-config";
    internal const string PackageFeedConfigKind = "package-feed-config";
    internal const string PiExtensionKind = "pi-extension";
    internal const string LegacyPiExtensionsKind = "legacy-pi-extensions";
    internal const string PreviewDataCopyKind = "preview-data-copy";

    public Task<IReadOnlyList<RunLeftover>> ListLeftoversAsync(IReadOnlySet<Guid> liveIds, CancellationToken ct = default)
        => Task.FromResult(ListLeftovers(
            liveIds, AgentIsolation.AgentReadRoot, AgentIsolation.ScratchRoot, Process.GetCurrentProcess().StartTime.ToUniversalTime(), null).Leftovers);

    /// <summary>
    /// The secret-bearing files a killed process left: MCP configs, package feed npm configs and pi
    /// extensions written before <paramref name="startedUtc"/> (the extensions only for runs and chats not in
    /// <paramref name="liveIds"/>), the copies of kept preview data of runs not in it made before then, and the scratch root's legacy extensions, as one leftover. A listing that
    /// fails is logged and the rest are listed; <c>ListedAll</c> is then false.
    /// </summary>
    internal static (IReadOnlyList<RunLeftover> Leftovers, bool ListedAll) ListLeftovers(
        IReadOnlySet<Guid> liveIds, string agentReadRoot, string scratchRoot, DateTime startedUtc, ILogger? logger)
    {
        var leftovers = new List<RunLeftover>();
        var listedAll = true;
        void List(string kind, Func<IEnumerable<string>> list)
        {
            try
            {
                leftovers.AddRange(list().ToList().Select(p => new RunLeftover(kind, p)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "Could not list the secret-bearing agent files a previous process left");
                listedAll = false;
            }
        }

        List(McpConfigKind, () => IldMcpServer.StaleConfigFiles(agentReadRoot, startedUtc));
        List(PackageFeedConfigKind, () => PackageFeedCredentialFiles.StaleFiles(agentReadRoot, startedUtc));
        List(PiExtensionKind, () => PiAdapter.StaleExtensions(agentReadRoot, liveIds, startedUtc));
        List(PreviewDataCopyKind, () => OrphanedPreviewDataCopies(scratchRoot, liveIds, startedUtc));
        leftovers.Add(new RunLeftover(LegacyPiExtensionsKind, scratchRoot));
        return (leftovers, listedAll);
    }

    public async Task<bool> DeleteLeftoverAsync(RunLeftover leftover, CancellationToken ct = default)
    {
        switch (leftover.Kind)
        {
            case McpConfigKind:
            case PackageFeedConfigKind:
                File.Delete(leftover.Location);
                return true;
            case PiExtensionKind:
                Directory.Delete(leftover.Location, recursive: true);
                return true;
            case PreviewDataCopyKind:
                // Never put back: a run that was not ended by ILD may have left it half-written.
                return await AgentWritableFiles.DeleteAsync([leftover.Location], ct);
            case LegacyPiExtensionsKind:
                return await PiAdapter.SweepLegacyExtensionsAsync(leftover.Location, ct);
            default:
                throw new ArgumentException($"unknown leftover kind '{leftover.Kind}'", nameof(leftover));
        }
    }
}
