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
///
/// The active set is derived from the local runs still alive, once per pass,
/// and carries no state between passes, so the implementation is safe to
/// register as scoped and resolve fresh each tick.
/// </summary>
public interface IRemoteWorkItemCoordinator
{
    /// <param name="claimReadyItems">
    /// When <c>false</c> (the scheduler is paused) the Ready-items claim loop is
    /// skipped, so nothing auto-promotes from Ready into Running. Every other
    /// step — heartbeating the active set, resuming WaitingForIld runs, closing
    /// runs behind items the server has finished, reporting active human
    /// feedback — runs as normal. Humans can still promote Ready items manually
    /// through the work-item transition API.
    /// </param>
    Task<PollCycleResult> RunPollCycleAsync(WorkItemServerOptions opts, int maxConcurrent, bool claimReadyItems = true, CancellationToken ct = default);
}

public sealed class PollCycleResult
{
    public IReadOnlyList<RemoteWorkItem> Claimed { get; init; } = Array.Empty<RemoteWorkItem>();
    public IReadOnlyList<RemoteWorkItem> Resumed { get; init; } = Array.Empty<RemoteWorkItem>();
    public IReadOnlyList<RemoteWorkItem> EscalatedToHumanFeedback { get; init; } = Array.Empty<RemoteWorkItem>();
    public bool HasActiveHumanFeedback { get; init; }

    /// <summary>
    /// The work items holding concurrency slots when the pass ended: those with
    /// a run already alive when it started, plus anything it claimed. Ordinal
    /// ascending, so one pass's holders compare cleanly against the last's.
    /// </summary>
    public IReadOnlyList<string> SlotHolders { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True when the pass had Ready work it could not claim because every slot
    /// was taken. Worth reporting because from the outside that is
    /// indistinguishable from an idle board — the failure mode that made the
    /// slot leak this coordinator used to have so expensive to find. It is a
    /// steady state rather than an event, though, so the caller owns how often
    /// to say so: it knows the cadence, and this pass does not.
    /// </summary>
    public bool BlockedByCap { get; init; }
}

public sealed class RemoteWorkItemCoordinator : IRemoteWorkItemCoordinator
{
    private readonly IWorkItemServerClient _client;
    private readonly ILoopTemplateResolver _resolver;
    private readonly ILoopEngine _engine;
    private readonly ILoopRunStore _loopRunStore;
    private readonly IProviderStore? _providerStore;
    private readonly IAiProviderConcurrencyTracker? _aiTracker;
    private readonly IWorkItemNotifier _workItemNotifier;
    private readonly ILogger<RemoteWorkItemCoordinator>? _logger;

    public RemoteWorkItemCoordinator(
        IWorkItemServerClient client,
        ILoopTemplateResolver resolver,
        ILoopEngine engine,
        ILoopRunStore loopRunStore,
        IProviderStore? providerStore = null,
        IAiProviderConcurrencyTracker? aiTracker = null,
        IWorkItemNotifier? workItemNotifier = null,
        ILogger<RemoteWorkItemCoordinator>? logger = null)
    {
        _client = client;
        _resolver = resolver;
        _engine = engine;
        _loopRunStore = loopRunStore;
        _providerStore = providerStore;
        _aiTracker = aiTracker;
        _workItemNotifier = workItemNotifier ?? new NoopWorkItemNotifier();
        _logger = logger;
    }

    public async Task<PollCycleResult> RunPollCycleAsync(WorkItemServerOptions opts, int maxConcurrent, bool claimReadyItems = true, CancellationToken ct = default)
    {
        // The Active Work Item Set, derived fresh from the runs still alive
        // locally. Deliberately local rather than the server's Running status:
        // the cap is this instance's own capacity, and reading it off the server
        // would silently turn it into a global cap shared by every ILD instance
        // pointed at the same board.
        var activeIds = await _loopRunStore.GetActiveWorkItemIdsAsync();
        var poll = await _client.PollAsync(opts, activeIds, ct);

        var claimed = new List<RemoteWorkItem>();
        var resumed = new List<RemoteWorkItem>();
        var escalated = new List<RemoteWorkItem>();

        // This pass's slot ledger: the derived set, minus what this pass
        // finishes, plus what it claims. Claims are counted here rather than by
        // re-deriving the set per item, which would cost a round trip each time
        // and — worse — would hand a slot straight back if a run started and
        // finished inside this same pass, letting one pass claim past the cap.
        var slotHolders = new HashSet<string>(activeIds, StringComparer.Ordinal);

        // 1. Resume anything in WaitingForIld — but only if the run's current
        //    AI node has provider capacity (parallelism gate). The blocking
        //    provider is re-evaluated dynamically each pass: settings can
        //    change and a once-blocked item may now be unblocked.
        foreach (var w in poll.ActiveItems.Where(w => w.Status == RemoteWorkItemStatus.WaitingForIld))
        {
            if (!await HasProviderCapacityForResumeAsync(w, ct)) continue;

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
                var run = await _loopRunStore.GetCurrentByWorkItemAsync(w.Id);
                if (run != null) await _engine.ResumeRecoveredRunAsync(run.Id);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to resume run for work item {WorkItemId}", w.Id);
            }
        }

        // 2. The server has finished with these items, so whatever local run is
        //    still open for one of them has nothing left to do. Ending it is
        //    what takes the item out of the Active Work Item Set — a run parked
        //    at a human gate when someone marks the item Done would otherwise
        //    keep its slot and its heartbeat for the lifetime of the process.
        //    A locally-driven Done already ends its own run; this covers the
        //    item finished on the server by someone else, which a board shared
        //    between ILD instances makes routine.
        foreach (var w in poll.ActiveItems.Where(w => w.Status == RemoteWorkItemStatus.Done))
        {
            slotHolders.Remove(w.Id);
            try
            {
                var run = await _loopRunStore.GetActiveByWorkItemAsync(w.Id);
                if (run != null)
                    await _loopRunStore.MarkRunCancelledAsync(run, "Work item marked Done on server");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "Failed to close the local run for work item {WorkItemId} after it went Done", w.Id);
            }
        }

        // 3. Claim Ready items, room permitting. Tag → template resolution
        //    happens up-front so no-match / ambiguous cases never enter the
        //    Running state on the server.
        var hasActiveHumanFeedback = poll.ActiveItems.Any(w => w.Status == RemoteWorkItemStatus.HumanFeedback);
        var blockedByCap = false;

        // While paused, leave Ready items untouched: the whole claim loop is the
        // auto-promotion path, so skipping it is what "pause" means. A human can
        // still promote a Ready item to Running manually via the transition API.
        foreach (var ready in claimReadyItems ? poll.ReadyItems : Enumerable.Empty<RemoteWorkItem>())
        {
            if (slotHolders.Count >= maxConcurrent)
            {
                // There is Ready work and no room for it. This pass is the only
                // place that knows both halves, so it records the fact; the
                // scheduler decides how loudly to say it, because being at the
                // cap persists across passes and only the scheduler knows how
                // often those run.
                blockedByCap = true;
                break;
            }
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
                slotHolders.Add(ready.Id);
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
                    // The claim stands on the server with nothing driving it, so
                    // hand it back for review and give up the slot it took —
                    // for the rest of this pass only. StartRunAsync commits the
                    // LoopRun row before the transition that most often throws
                    // here, so where a row did get written the derived set
                    // legitimately takes that slot back on the next pass. The
                    // item is heartbeated again from then on, and the resume
                    // path drives the orphaned run as soon as a human responds.
                    try
                    {
                        await _client.TransitionAsync(opts, ready.Id, new RemoteTransitionRequest
                        {
                            TargetStatus = RemoteWorkItemStatus.HumanFeedback,
                            Reason = $"Failed to start run: {ex.Message}",
                        }, ct);
                        slotHolders.Remove(ready.Id);
                        await _workItemNotifier.WorkItemStateChangedAsync(
                            ready.Id, RemoteWorkItemStatus.Running, RemoteWorkItemStatus.HumanFeedback);
                        escalated.Add(ready);
                    }
                    catch (Exception unwindEx)
                    {
                        _logger?.LogWarning(unwindEx,
                            "Failed to hand back work item {WorkItemId} after run start failure", ready.Id);
                    }
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
            SlotHolders = slotHolders.OrderBy(id => id, StringComparer.Ordinal).ToList(),
            BlockedByCap = blockedByCap,
        };
    }

    /// <summary>
    /// True if the run associated with <paramref name="item"/> is not parked on
    /// an AI node, or the provider it will actually execute against currently
    /// has spare capacity. That provider is the node's pinned one, or the
    /// configured default when the node pins nothing \u2014 in either case swapped
    /// for the work item's override when
    /// <see cref="AiProviderOverrideRule"/> says the override applies, exactly
    /// as <c>AINodeExecutor</c> resolves it before claiming its slot. Peeking a
    /// different provider than the executor claims would strand the run (gate
    /// on a full provider the run never uses) or flap it (resume, then
    /// immediately re-park at the executor's gate).
    /// Re-evaluated each poll so changes to provider parallelism settings
    /// take effect without restart.
    /// </summary>
    private async Task<bool> HasProviderCapacityForResumeAsync(RemoteWorkItem item, CancellationToken ct)
    {
        if (_providerStore == null || _aiTracker == null) return true;
        try
        {
            var run = await _loopRunStore.GetCurrentByWorkItemAsync(item.Id);
            if (run?.CurrentNodeId is not { } currentNodeId) return true;

            var nodes = await _loopRunStore.GetNodesForVersionAsync(run.LoopTemplateVersionId);
            var node = nodes.FirstOrDefault(n => n.Id == currentNodeId);
            if (node == null || node.NodeType != ILD.Data.Enums.NodeType.AI) return true;

            var pinnedId = TryReadAiProviderId(node.Config);
            var targetId = AiProviderOverrideRule.Applies(
                    item.AiProviderOverride, item.AiProviderOverrideId, nodePinsProvider: pinnedId != null)
                ? item.AiProviderOverrideId
                : pinnedId;

            // No pin and no override \u2192 the executor falls back to the default
            // provider, so gate on that rather than waving the resume through.
            var provider = targetId is { } id
                ? await _providerStore.GetAiProviderByIdAsync(id)
                : await _providerStore.GetDefaultAiProviderAsync();
            if (provider == null) return true; // let the executor report the missing provider

            return _aiTracker.HasCapacity(provider.Id, provider.Parallelism);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Provider capacity check failed for work item {WorkItemId}", item.Id);
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
