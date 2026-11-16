using System.Text;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Implementations.PackageFeeds;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.DTOs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// What callers use to preview a worktree. Each worktree's preview is a <see cref="WorktreePreviewLifecycle"/>, which
/// owns its start protocol, runtime and teardown; this class finds the lifecycle, loads the config it is given, and
/// answers the questions that need neither (config, logs, install, proxy targets).
/// </summary>
public sealed class WorktreePreviewService : IWorktreePreviewService, IDisposable
{
    private readonly PreviewLifecycleContext _lifecycles;
    private readonly IRunWorkspace _host;
    private readonly ILogger<WorktreePreviewService> _logger;

    public WorktreePreviewService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        PreviewProxyBase proxyBase,
        ILogger<WorktreePreviewService> logger,
        PreviewDataClaims claims,
        IRunWorkspace host,
        IEgressPolicy? egressPolicy = null,
        IWorkItemNotifier? notifier = null,
        EgressCredentials? credentials = null)
        : this(httpClientFactory, configuration, proxyBase, logger, host,
            EgressProxyOptions.FromEnvironment(), credentials, ProcessEnvironment.Current, claims: claims,
            egressPolicy: egressPolicy, notifier: notifier)
    {
    }

    /// <summary>
    /// <paramref name="environment"/> is where our own <c>HOME</c> and <c>PATH</c>
    /// are read, and where an install puts the npm global bin on <c>PATH</c>: the
    /// process environment unless a test supplies its own.
    /// <paramref name="isPortAvailable"/> is how a port is probed before it is
    /// handed out: binding it on loopback unless a test supplies its own.
    /// <paramref name="claims"/> records which previews hold each repository's preview
    /// data: the host's, which its reset consults too, or a private one.
    /// <paramref name="egressPolicy"/> decides whether a launch is proxied: not in network mode Off
    /// ; with no policy every launch
    /// is proxied, as <paramref name="egressProxy"/> says, under a credential <paramref name="credentials"/> signs for the preview.
    /// </summary>
    public WorktreePreviewService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        PreviewProxyBase proxyBase,
        ILogger<WorktreePreviewService> logger,
        IRunWorkspace host,
        EgressProxyOptions? egressProxy = null,
        EgressCredentials? credentials = null,
        IProcessEnvironment? environment = null,
        Func<int, bool>? isPortAvailable = null,
        PreviewDataClaims? claims = null,
        IEgressPolicy? egressPolicy = null,
        IWorkItemNotifier? notifier = null)
    {
        _host = host;
        _logger = logger;
        var timings = new PreviewTimings();
        var previewNotifier = new PreviewNotifier(notifier, logger);
        _lifecycles = new PreviewLifecycleContext(
            host,
            new HttpPreviewHealthProbe(httpClientFactory, host, timings),
            new PreviewLauncher(host, egressProxy, credentials, environment ?? ProcessEnvironment.Current, previewNotifier, timings, logger),
            new PreviewPorts(isPortAvailable),
            new PreviewResponses(proxyBase),
            claims,
            previewNotifier,
            egressPolicy,
            configuration,
            timings,
            logger);
    }

    /// <summary>The waits a preview makes on its services. Tests shorten them.</summary>
    internal PreviewTimings Timings => _lifecycles.Timings;

    public async Task<WorktreePreviewResponse> GetStatusAsync(string worktreePath, CancellationToken cancellationToken = default)
    {
        var normalized = PreviewPaths.Normalize(worktreePath);
        var loaded = await _lifecycles.Config.LoadAsync(normalized, cancellationToken);
        if (!loaded.Configured)
        {
            return new WorktreePreviewResponse
            {
                Configured = false,
                State = "notConfigured",
                WorktreePath = normalized,
                Message = loaded.Message,
            };
        }

        return _lifecycles.Find(normalized) is { } lifecycle
            ? lifecycle.Status(loaded)
            : _lifecycles.Responses.BuildStoppedResponse(loaded);
    }

    public async Task<WorktreePreviewResponse> StartAsync(string worktreePath, WorktreePreviewStartOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (lifecycle, loaded) = await OpenAsync(worktreePath, cancellationToken);
        return await lifecycle.StartAsync(loaded, PreviewStartScope.Whole, options, cancellationToken);
    }

    public async Task<WorktreePreviewResponse> BeginStartAsync(string worktreePath, WorktreePreviewStartOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (lifecycle, loaded) = await OpenAsync(worktreePath, cancellationToken);
        return await lifecycle.BeginAsync(loaded, PreviewStartScope.Whole, options, cancellationToken);
    }

    public async Task<WorktreePreviewResponse> StartServiceAsync(string worktreePath, string serviceName, WorktreePreviewStartOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (lifecycle, loaded) = await OpenAsync(worktreePath, cancellationToken);
        return await lifecycle.StartAsync(loaded, new PreviewStartScope(serviceName, options?.StartDependencies ?? false), options, cancellationToken);
    }

    public Task<WorktreePreviewResponse> StopAsync(string worktreePath, CancellationToken cancellationToken = default)
        => _lifecycles.For(PreviewPaths.Normalize(worktreePath)).StopAsync(cancellationToken);

    public Task<WorktreePreviewResponse> StopServiceAsync(string worktreePath, string serviceName, CancellationToken cancellationToken = default)
        => _lifecycles.For(PreviewPaths.Normalize(worktreePath)).StopServiceAsync(serviceName, cancellationToken);

    private async Task<(WorktreePreviewLifecycle Lifecycle, LoadedPreviewConfig Loaded)> OpenAsync(string worktreePath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_lifecycles.IsDisposed, this);
        var normalized = PreviewPaths.Normalize(worktreePath);
        var loaded = await _lifecycles.Config.LoadAsync(normalized, cancellationToken);
        return (_lifecycles.For(normalized), loaded);
    }

    /// <summary>
    /// The live process list of the preview running for <paramref name="worktreePath"/>,
    /// the same object later starts and stops act on, or null when no preview runs for it.
    /// </summary>
    internal IReadOnlyList<ManagedPreviewProcess>? RunningProcesses(string worktreePath)
        => _lifecycles.Find(PreviewPaths.Normalize(worktreePath))?.Runtime?.Processes;

    public async Task<string?> GetServiceConfigAsync(string worktreePath, string serviceName, string? profileName = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
            return null;

        return await _lifecycles.Config.ReadServiceConfigAsync(PreviewPaths.Normalize(worktreePath), serviceName, profileName, cancellationToken);
    }

    public async Task UpdateServiceConfigAsync(string worktreePath, string serviceName, string serviceConfigJson, string? profileName = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
            throw new InvalidOperationException("A service name is required.");

        await _lifecycles.Config.WriteServiceConfigAsync(PreviewPaths.Normalize(worktreePath), serviceName, serviceConfigJson, profileName, cancellationToken);
    }

    public async Task<string?> GetServiceLogAsync(string worktreePath, string serviceName, int maxBytes = 64 * 1024, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
            return null;

        // The log file is named after the service inside the state directory, so a
        // name with a path separator (or "..") could escape it. A start composes the
        // path the same way; reject anything that isn't a bare file
        // name rather than reading an arbitrary file off disk.
        if (!PreviewConfigLoader.IsSafeServiceName(serviceName))
            return null;

        var normalized = PreviewPaths.Normalize(worktreePath);
        var logPath = Path.Combine(PreviewPaths.BuildLogDirectory(normalized), $"{serviceName}.log");
        if (!File.Exists(logPath))
            return null;

        await using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (maxBytes > 0 && stream.Length > maxBytes)
            stream.Seek(-maxBytes, SeekOrigin.End);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public async Task<WorktreeInstallResult> InstallAsync(
        string worktreePath,
        string? profileName = null,
        string? customEnv = null,
        IReadOnlyList<PackageFeedCredential>? packageFeeds = null,
        PreviewVolumeScope? volumes = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = PreviewPaths.Normalize(worktreePath);
        var loaded = await _lifecycles.Config.LoadAsync(normalized, cancellationToken);
        if (!loaded.Configured || loaded.Config == null)
        {
            // Best effort: most repositories ship no ild.config.json preview
            // profile, so there is nothing to install. Skip instead of failing
            // and let the caller surface the reason as a warning.
            return new WorktreeInstallResult(false, loaded.Message ?? "No ild.config.json preview profile found.");
        }

        var found = PreviewConfigLoader.Find(loaded, profileName);
        var profile = found.Config;
        PreviewConfigLoader.ValidateVolumes(found.Name, profile);

        var mode = await _lifecycles.LaunchModeAsync(cancellationToken);
        var launcher = _lifecycles.Launcher;

        // The install runs outside the gate, holding the preview data through its own lease.
        using (var lease = await _lifecycles.ClaimPreviewDataAsync(volumes, normalized, holdsGate: false, cancellationToken))
        {
            var stateDirectory = await launcher.BuildStateDirectoryAsync(normalized, cancellationToken);
            var logDirectory = PreviewPaths.BuildLogDirectory(normalized);

            // Install needs no ports or running services — build a port-less runtime so
            // the shared install runner resolves ${WORKTREE}/${STATE_DIR}/${VOLUME:name}
            // the same way the preview start path does.
            var runtime = new PreviewRuntime(
                normalized,
                loaded.ConfigPath,
                found.Name,
                stateDirectory,
                logDirectory,
                _lifecycles.ResolvePublicHost(null),
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                new List<ManagedPreviewProcess>(),
                DotEnvParser.Parse(customEnv),
                new PackageFeedFileShare(packageFeeds ?? [], launcher.FeedFilePrefix(normalized, "install"), _logger),
                await PreviewVolumes.CreateFoldersAsync(_host, _host, normalized, profile.Volumes, volumes, stateDirectory, _logger, cancellationToken));

            await launcher.RunInstallStepsAsync(profile.Install, runtime, mode, cancellationToken);
            return new WorktreeInstallResult(true);
        }
    }

    public async Task<WorktreePreviewValidationResult> ValidateConfigAsync(string worktreePath, string? profileName = null, CancellationToken cancellationToken = default)
    {
        var normalized = PreviewPaths.Normalize(worktreePath);
        var loaded = await _lifecycles.Config.LoadAsync(normalized, cancellationToken);
        if (!loaded.Configured || loaded.Config == null)
        {
            return new WorktreePreviewValidationResult(false, null, Array.Empty<string>(), loaded.Message);
        }

        // Same validation the preview-start path applies, but without allocating
        // ports or launching anything — a pure dry run over the parsed config.
        var profile = PreviewConfigLoader.Resolve(loaded, profileName);
        return new WorktreePreviewValidationResult(
            true,
            profile.Name,
            profile.Services.Select(s => s.Name).ToList(),
            null);
    }

    public bool IsPreviewRunning(string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath))
            return false;

        return _lifecycles.Find(Path.GetFullPath(worktreePath)) is { } lifecycle
               && (lifecycle.Runtime != null || lifecycle.IsStarting);
    }

    public string PreviewState(string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath))
            return "stopped";

        return _lifecycles.Find(Path.GetFullPath(worktreePath))?.State() ?? "stopped";
    }

    public bool AnyPreviewRunning() => _lifecycles.Lifecycles.Any(lifecycle => lifecycle.Runtime != null || lifecycle.IsStarting);

    public async Task<PreviewTarget> ResolvePreviewTargetAsync(string hostLabel, IWorkItemManager workItems, CancellationToken cancellationToken = default)
    {
        if (!PreviewUrls.TryParsePreviewHostLabel(hostLabel, out var workItemId, out var serviceName))
        {
            return PreviewTarget.Failed(
                PreviewTargetOutcome.NotAPreviewHost,
                $"'{hostLabel}' is not a worktree preview hostname. Previews are served from "
                + "wi-<workItemId> (the service marked \"public\": true) or wi-<workItemId>-<serviceName>.");
        }

        var workItem = await workItems.GetWorkItemAsync(workItemId);
        if (workItem == null)
        {
            return PreviewTarget.Failed(
                PreviewTargetOutcome.UnknownWorkItem,
                $"There is no work item {workItemId}.");
        }

        if (string.IsNullOrWhiteSpace(workItem.WorktreePath))
        {
            return PreviewTarget.Failed(
                PreviewTargetOutcome.NoWorktree,
                $"Work item {workItemId} has no worktree yet, so there is nothing to preview. "
                + "A worktree is created when a run reaches its Start node.");
        }

        if (_lifecycles.Find(PreviewPaths.Normalize(workItem.WorktreePath))?.Runtime is not { } runtime)
        {
            return PreviewTarget.Failed(
                PreviewTargetOutcome.PreviewNotRunning,
                $"The worktree preview for work item {workItemId} is not running. "
                + "Start it from the work item's Preview tab.");
        }

        var rows = runtime.Processes
            .Select(p => new PreviewUrls.PreviewServiceRow(
                p.Service.Name, p.Service.Public, p.Service.RewriteHost, p.IsRunning, p.Service.Port,
                runtime.Ports.TryGetValue(p.Service.Port, out var port) ? port : 0))
            .ToList();
        return PreviewUrls.SelectPreviewTarget(workItemId, serviceName, rows) with { WorktreePath = workItem.WorktreePath };
    }

    public void Dispose() => _lifecycles.Dispose();
}
