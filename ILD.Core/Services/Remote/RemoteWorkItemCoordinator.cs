namespace ILD.Core.Services.Remote;

using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// One pass of the remote-server poll loop: heartbeat the active set, drain
/// items the server has flipped to <see cref="RemoteWorkItemStatus.WaitingForIld"/>
/// (resuming them locally), and — while under the concurrency cap — claim
/// fresh Ready items and start them. Pure orchestration, no timing — that is
/// the background service's job, which keeps this layer trivially testable.
/// </summary>
public interface IRemoteWorkItemCoordinator
{
    Task<PollCycleResult> RunPollCycleAsync(WorkItemServerOptions opts, int maxConcurrent, CancellationToken ct = default);
}

public sealed class PollCycleResult
{
    public IReadOnlyList<RemoteWorkItem> Claimed { get; init; } = Array.Empty<RemoteWorkItem>();
    public IReadOnlyList<RemoteWorkItem> Resumed { get; init; } = Array.Empty<RemoteWorkItem>();
    public IReadOnlyList<RemoteWorkItem> EscalatedToHumanFeedback { get; init; } = Array.Empty<RemoteWorkItem>();
    public bool HasActiveHumanFeedback { get; init; }
}

public sealed class RemoteWorkItemCoordinator : IRemoteWorkItemCoordinator
{
    private readonly IWorkItemServerClient _client;
    private readonly IActiveWorkItemTracker _tracker;
    private readonly ILoopTemplateResolver _resolver;
    private readonly ILoopEngine _engine;
    private readonly ILoopRunStore? _loopRunStore;
    private readonly IProviderStore? _providerStore;
    private readonly IAiProviderConcurrencyTracker? _aiTracker;
    private readonly IWorkItemNotifier _workItemNotifier;
    private readonly ILogger<RemoteWorkItemCoordinator>? _logger;

    public RemoteWorkItemCoordinator(
        IWorkItemServerClient client,
        IActiveWorkItemTracker tracker,
        ILoopTemplateResolver resolver,
        ILoopEngine engine,
        ILoopRunStore? loopRunStore = null,
        IProviderStore? providerStore = null,
        IAiProviderConcurrencyTracker? aiTracker = null,
        IWorkItemNotifier? workItemNotifier = null,
        ILogger<RemoteWorkItemCoordinator>? logger = null)
    {
        _client = client;
        _tracker = tracker;
        _resolver = resolver;
        _engine = engine;
        _loopRunStore = loopRunStore;
        _providerStore = providerStore;
        _aiTracker = aiTracker;
        _workItemNotifier = workItemNotifier ?? new NoopWorkItemNotifier();
        _logger = logger;
    }

    public async Task<PollCycleResult> RunPollCycleAsync(WorkItemServerOptions opts, int maxConcurrent, CancellationToken ct = default)
    {
        var poll = await _client.PollAsync(opts, _tracker.Snapshot(), ct);

        var claimed = new List<RemoteWorkItem>();
        var resumed = new List<RemoteWorkItem>();
        var escalated = new List<RemoteWorkItem>();

        // 1. Resume anything in WaitingForIld — but only if the run's current
        //    AI node has provider capacity (parallelism gate). The blocking
        //    provider is re-evaluated dynamically each pass: settings can
        //    change and a once-blocked item may now be unblocked.
        foreach (var w in poll.ActiveItems.Where(w => w.Status == RemoteWorkItemStatus.WaitingForIld))
        {
            if (!await HasProviderCapacityForResumeAsync(w.Id, ct)) continue;

            var resp = await _client.TransitionAsync(opts, w.Id,
                new RemoteTransitionRequest { TargetStatus = RemoteWorkItemStatus.Running }, ct);
            if (!resp.Success) continue;
            resumed.Add(w);

            // The raw client call above bypasses WorkItemManager, so the
            // SignalR notifier never fires for this transition. Emit it
            // explicitly so the taskboard reflects the move without a
            // page refresh.
            await _workItemNotifier.WorkItemStateChangedAsync(
                w.Id, RemoteWorkItemStatus.WaitingForIld, RemoteWorkItemStatus.Running);

            // Kick the local engine to pick the run back up. Reuse the
            // recovery entry point — it's exactly the "resume a parked run
            // from its current node" semantics we need here.
            try
            {
                if (_loopRunStore != null)
                {
                    var run = await _loopRunStore.GetCurrentByWorkItemAsync(w.Id);
                    if (run != null) await _engine.ResumeRecoveredRunAsync(run.Id);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to resume run for work item {WorkItemId}", w.Id);
            }
        }

        // 2. Drop any active item the server now reports as Done so the
        //    heartbeat list shrinks — keeps poll payloads bounded.
        foreach (var w in poll.ActiveItems.Where(w => w.Status == RemoteWorkItemStatus.Done))
            _tracker.Remove(w.Id);

        // 3. Claim Ready items, room permitting. Tag → template resolution
        //    happens up-front so no-match / ambiguous cases never enter the
        //    Running state on the server.
        var hasActiveHumanFeedback = poll.ActiveItems.Any(w => w.Status == RemoteWorkItemStatus.HumanFeedback);

        foreach (var ready in poll.ReadyItems)
        {
            if (_tracker.Count >= maxConcurrent) break;
            if (ct.IsCancellationRequested) break;

            var resolution = _resolver.Resolve(ready.Tags);
            if (resolution.Kind != LoopTemplateResolutionKind.Single)
            {
                var reason = resolution.Kind switch
                {
                    LoopTemplateResolutionKind.None => "No loop found for existing tags",
                    LoopTemplateResolutionKind.Ambiguous =>
                        $"Multiple loop templates match tags: {string.Join(", ", resolution.MatchingTemplateNames)}",
                    _ => "Unable to resolve template",
                };
                await _client.TransitionAsync(opts, ready.Id, new RemoteTransitionRequest
                {
                    TargetStatus = RemoteWorkItemStatus.HumanFeedback,
                    Reason = reason,
                }, ct);
                escalated.Add(ready);
                continue;
            }

            var claim = await _client.TransitionAsync(opts, ready.Id, new RemoteTransitionRequest
            {
                TargetStatus = RemoteWorkItemStatus.Running,
            }, ct);
            if (claim.Success)
            {
                _tracker.Add(ready.Id);
                claimed.Add(ready);

                // The raw client claim above bypasses WorkItemManager, and
                // the engine's subsequent transition is a no-op (prev ==
                // actual == Running) so its notifier is suppressed. Emit
                // the SignalR event here so the taskboard sees the move
                // out of Ready without a page refresh.
                await _workItemNotifier.WorkItemStateChangedAsync(
                    ready.Id, RemoteWorkItemStatus.Ready, RemoteWorkItemStatus.Running);

                // Per PRD §3.2 step 4: a successful claim must "create a local
                // LoopRun, kick off LoopEngine". The engine resolves the
                // template from the work item's tags and creates the run; if
                // that fails it transitions the item back to HumanFeedback so
                // the server reflects reality. Failures here are logged but
                // never abort the poll cycle — the next pass can retry.
                try
                {
                    await _engine.StartRunAsync(ready.Id, ct);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "Engine failed to start run for claimed work item {WorkItemId}", ready.Id);
                }
            }
            // Lost-claim race (another instance got there first) is silently
            // skipped — the next poll will simply not see the item again.
        }

        return new PollCycleResult
        {
            Claimed = claimed,
            Resumed = resumed,
            EscalatedToHumanFeedback = escalated,
            HasActiveHumanFeedback = hasActiveHumanFeedback,
        };
    }

    /// <summary>
    /// True if the run associated with <paramref name="workItemId"/> is not
    /// parked on an AI node, or its AI provider currently has spare capacity.
    /// Re-evaluated each poll so changes to provider parallelism settings
    /// take effect without restart.
    /// </summary>
    private async Task<bool> HasProviderCapacityForResumeAsync(string workItemId, CancellationToken ct)
    {
        if (_loopRunStore == null || _providerStore == null || _aiTracker == null) return true;
        try
        {
            var run = await _loopRunStore.GetCurrentByWorkItemAsync(workItemId);
            if (run?.CurrentNodeId is not { } currentNodeId) return true;

            var nodes = await _loopRunStore.GetNodesForVersionAsync(run.LoopTemplateVersionId);
            var node = nodes.FirstOrDefault(n => n.Id == currentNodeId);
            if (node == null || node.NodeType != ILD.Data.Enums.NodeType.AI) return true;

            var providerId = TryReadAiProviderId(node.Config);
            if (providerId == null) return true; // no provider configured \u2192 default; let the engine decide

            var provider = await _providerStore.GetAiProviderByIdAsync(providerId.Value);
            if (provider == null) return true;

            return _aiTracker.HasCapacity(provider.Id, provider.Parallelism);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Provider capacity check failed for work item {WorkItemId}", workItemId);
            return true; // be permissive on errors so we don't strand work items
        }
    }

    private static Guid? TryReadAiProviderId(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(prop.Name, "aiProviderId", StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(prop.Value.GetString(), out var g)) return g;
            }
        }
        catch { /* malformed config \u2192 treat as no provider */ }
        return null;
    }
}
