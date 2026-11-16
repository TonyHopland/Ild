using ILD.Core.Services.Interfaces;
using System.Collections.Concurrent;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.RunHosts;
using ILD.Data.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// What every worktree's <see cref="WorktreePreviewLifecycle"/> shares: the collaborators they work through and the
/// registry of the lifecycles themselves. Starts and stops of every worktree take the one <see cref="Gate"/>, so a
/// preview that has to stop another worktree's (see <see cref="ClaimPreviewDataAsync"/>) does so under it.
/// </summary>
internal sealed class PreviewLifecycleContext : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, WorktreePreviewLifecycle> _lifecycles = new(StringComparer.Ordinal);
    private int _disposed;

    /// <param name="host">What the preview runs through; the other collaborators default to what a plain preview uses.</param>
    /// <param name="probe">How a launched service is waited on until it is healthy.</param>
    /// <param name="claims">Which previews hold each repository's preview data: the host's, which its reset consults too, or a private one.</param>
    /// <param name="egressPolicy">Decides whether a launch is proxied: not in network mode Off; with no policy every launch is proxied.</param>
    public PreviewLifecycleContext(
        IRunWorkspace host,
        IPreviewHealthProbe probe,
        PreviewLauncher? launcher = null,
        PreviewPorts? ports = null,
        PreviewResponses? responses = null,
        PreviewDataClaims? claims = null,
        PreviewNotifier? notifier = null,
        IEgressPolicy? egressPolicy = null,
        IConfiguration? configuration = null,
        PreviewTimings? timings = null,
        ILogger? logger = null)
    {
        Host = host;
        Probe = probe;
        Logger = logger ?? NullLogger.Instance;
        Timings = timings ?? new PreviewTimings();
        Notifier = notifier ?? new PreviewNotifier(null, Logger);
        Launcher = launcher ?? new PreviewLauncher(host, null, null, ProcessEnvironment.Current, Notifier, Timings, Logger);
        Ports = ports ?? new PreviewPorts();
        Responses = responses ?? new PreviewResponses(PreviewProxyBase.Disabled);
        Claims = claims ?? new PreviewDataClaims();
        EgressPolicy = egressPolicy;
        Config = new PreviewConfigLoader(host);
        _configuration = configuration ?? new ConfigurationBuilder().Build();
    }

    public IRunWorkspace Host { get; }
    public IPreviewHealthProbe Probe { get; }
    public PreviewLauncher Launcher { get; }
    public PreviewPorts Ports { get; }
    public PreviewResponses Responses { get; }
    public PreviewDataClaims Claims { get; }
    public PreviewNotifier Notifier { get; }
    public PreviewTimings Timings { get; }
    public PreviewConfigLoader Config { get; }
    public IEgressPolicy? EgressPolicy { get; }
    public ILogger Logger { get; }

    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Cancelled when the previews are being shut down: every start tears down what it launched and ends.</summary>
    public CancellationTokenSource Shutdown { get; } = new();

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public IEnumerable<WorktreePreviewLifecycle> Lifecycles => _lifecycles.Values;

    public WorktreePreviewLifecycle For(string normalizedWorktreePath)
        => _lifecycles.GetOrAdd(normalizedWorktreePath, path => new WorktreePreviewLifecycle(path, this));

    public WorktreePreviewLifecycle? Find(string normalizedWorktreePath)
        => _lifecycles.GetValueOrDefault(normalizedWorktreePath);

    public ValueTask<NetworkMode> LaunchModeAsync(CancellationToken cancellationToken) => EgressPolicy.LaunchModeAsync(cancellationToken);

    public string ResolvePublicHost(string? requestedHost)
        => string.IsNullOrWhiteSpace(requestedHost)
            ? (_configuration["ILD_PREVIEW_PUBLIC_HOST"] ?? "127.0.0.1")
            : requestedHost.Trim();

    /// <summary>
    /// Takes the hold on the repository's preview data a start or install of
    /// <paramref name="normalized"/> needs, or throws naming what holds it; null when
    /// the caller named no repository. The claim itself is atomic, so a refusal by
    /// another work item (or a reset) is final and is thrown at once.
    /// <para>
    /// A refusal by the same work item's preview on another worktree — an earlier
    /// run's, still registered — stops that preview and claims again. Every preview
    /// surface addresses a work item's current worktree, so nothing else could ever
    /// stop it, and the work item's own previews would stay locked out until a restart.
    /// Stopping it takes <see cref="Gate"/>: starts already hold it
    /// (<paramref name="holdsGate"/>); an install takes it only here, so it never waits
    /// behind a start just to be refused.
    /// </para>
    /// </summary>
    public async Task<PreviewDataClaims.Lease?> ClaimPreviewDataAsync(
        PreviewVolumeScope? scope, string normalized, bool holdsGate, CancellationToken cancellationToken)
    {
        if (scope == null)
            return null;

        if (Claims.TryAcquire(scope, normalized, out var lease, out var refusal))
            return lease;

        if (refusal.Holder is not { } holder || !string.Equals(holder.WorkItemId, scope.WorkItemId, StringComparison.Ordinal))
            throw new InvalidOperationException(refusal.Message);

        if (!holdsGate)
            await Gate.WaitAsync(cancellationToken);
        try
        {
            if (Find(holder.WorktreePath)?.TakeRuntime() is { } earlier)
                await RetireRuntimeAsync(earlier, CancellationToken.None);
            if (Claims.TryAcquire(scope, normalized, out lease, out refusal))
                return lease;
            throw new InvalidOperationException(refusal.Message);
        }
        finally
        {
            if (!holdsGate)
                Gate.Release();
        }
    }

    /// <summary>Stops a runtime that has left its lifecycle, and lets go of its hold on the repository's preview data.</summary>
    public async Task RetireRuntimeAsync(PreviewRuntime runtime, CancellationToken cancellationToken)
    {
        try
        {
            await StopRuntimeAsync(runtime, cancellationToken);
        }
        finally
        {
            runtime.PreviewDataLease?.Dispose();
        }
    }

    public async Task StopRuntimeAsync(PreviewRuntime runtime, CancellationToken cancellationToken)
    {
        foreach (var process in runtime.Processes)
        {
            await Launcher.StopAsync(process, cancellationToken);
        }

        runtime.ClearProcesses();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Shutdown.Cancel();

        // Each start tears down what it launched as it observes the cancellation, and
        // each lifecycle waits for that before it retires what is left.
        foreach (var lifecycle in Lifecycles)
            lifecycle.Dispose();

        Gate.Dispose();
    }
}
