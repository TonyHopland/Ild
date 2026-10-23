namespace ILD.Core.Services.Remote;

using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
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
    /// feedback — runs as normal. Humans can still start a Ready item by hand
    /// with <see cref="StartIgnoringRepositoryLimitAsync"/>.
    /// </param>
    Task<PollCycleResult> RunPollCycleAsync(WorkItemServerOptions opts, int maxConcurrent, bool claimReadyItems = true, CancellationToken ct = default);

    /// <summary>
    /// A human starts one Ready item now, the way a pass claims and starts it,
    /// but without checking its repository's parallelism limit. The instance's
    /// concurrency cap still applies, and a scheduler pause does not. Once in,
    /// the start finishes even if <paramref name="ct"/> is cancelled, so a claim
    /// is never left half done.
    /// </summary>
    Task<HandStartResult> StartIgnoringRepositoryLimitAsync(WorkItemServerOptions opts, string workItemId, int maxConcurrent, WorkItemActor actor, CancellationToken ct = default);
}

public enum HandStartResult
{
    /// <summary>The item left Ready: it is Running, or in HumanFeedback because no single loop matched or its run failed to start.</summary>
    Started,
    NotFound,
    NotReady,
    AtConcurrencyLimit,
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
    /// ascending purely so the log line reads the same way twice — no consumer
    /// depends on the order (<c>CapStallReporter</c> compares these as a set,
    /// deliberately).
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

    /// <summary>
    /// Ready items left in Ready because their repository had reached its
    /// parallelism limit. Never counted towards <see cref="BlockedByCap"/>.
    /// </summary>
    public IReadOnlyList<RemoteWorkItem> HeldByRepositoryLimit { get; init; } = Array.Empty<RemoteWorkItem>();
}

public sealed class RemoteWorkItemCoordinator : IRemoteWorkItemCoordinator
{
    private readonly IWorkItemServerClient _client;
    private readonly ILoopTemplateResolver _resolver;
    private readonly ILoopEngine _engine;
    private readonly ILoopRunStore _loopRunStore;
    private readonly WorkItemActor _scheduler;
    private readonly IProviderStore? _providerStore;
    private readonly IAiProviderConcurrencyTracker? _aiTracker;
    private readonly IWorkItemNotifier _workItemNotifier;
    private readonly ILogger<RemoteWorkItemCoordinator>? _logger;
    private readonly RepositoryParallelismHolds _repositoryHolds;
    private readonly ReadyClaimGate _claimGate;

    public RemoteWorkItemCoordinator(
        IWorkItemServerClient client,
        ILoopTemplateResolver resolver,
        ILoopEngine engine,
        ILoopRunStore loopRunStore,
        BootstrapCredentials instance,
        IProviderStore? providerStore = null,
        IAiProviderConcurrencyTracker? aiTracker = null,
        IWorkItemNotifier? workItemNotifier = null,
        ILogger<RemoteWorkItemCoordinator>? logger = null,
        RepositoryParallelismHolds? repositoryHolds = null,
        ReadyClaimGate? claimGate = null)
    {
        _client = client;
        _resolver = resolver;
        _engine = engine;
        _loopRunStore = loopRunStore;
        _scheduler = WorkItemActor.System(instance.Username, "scheduler");
        _providerStore = providerStore;
        _aiTracker = aiTracker;
        _workItemNotifier = workItemNotifier ?? new NoopWorkItemNotifier();
        _logger = logger;
        _repositoryHolds = repositoryHolds ?? new RepositoryParallelismHolds();
        _claimGate = claimGate ?? new ReadyClaimGate();
    }

    public async Task<PollCycleResult> RunPollCycleAsync(WorkItemServerOptions opts, int maxConcurrent, bool claimReadyItems = true, CancellationToken ct = default)
    {
        using var claimTurn = await _claimGate.EnterAsync(ct);

        // The Active Work Item Set, derived fresh from the runs still alive
        // locally. Deliberately local rather than the server's Running status:
        // the cap is this instance's own capacity, and reading it off the server
        // would silently turn it into a global cap shared by every ILD instance
        // pointed at the same board.
        var activeIds = await _loopRunStore.GetActiveWorkItemIdsAsync();
        var poll = await _client.PollAsync(opts, activeIds, _scheduler, ct);

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
        //    An item with no live run here has nothing to resume, and moving it
        //    to Running would leave it there undriven.
        foreach (var w in poll.ActiveItems.Where(w => w.Status == RemoteWorkItemStatus.WaitingForIld))
        {
            var run = await _loopRunStore.GetActiveByWorkItemAsync(w.Id);
            if (run is null) continue;
            if (!await HasProviderCapacityForResumeAsync(w, run, ct)) continue;

            var resp = await _client.TransitionAsync(opts, w.Id,
                new RemoteTransitionRequest { TargetStatus = RemoteWorkItemStatus.Running, Reason = "AI provider capacity is free again" },
                _scheduler, ct);
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
                await _engine.ResumeRecoveredRunAsync(run.Id);
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
        //    between ILD instances makes routine. StopRunAsync rather than
        //    CancelRunAsync: the item's status is the server's already, and
        //    parking it in HumanFeedback would undo the Done being reacted to.
        foreach (var w in poll.ActiveItems.Where(w => w.Status == RemoteWorkItemStatus.Done))
        {
            try
            {
                var run = await _loopRunStore.GetActiveByWorkItemAsync(w.Id);
                if (run != null)
                    await _engine.StopRunAsync(run.Id, "Work item marked Done on server");

                // Only once the run is provably over. Releasing first would let
                // a failed write hand this pass a slot whose run is still very
                // much alive, and the pass would claim past the cap on the
                // strength of it.
                slotHolders.Remove(w.Id);
            }
            catch (Exception ex)
            {
                // Slot kept: the run may still be running. The next pass
                // re-derives the set and tries again.
                _logger?.LogWarning(ex,
                    "Failed to close the local run for work item {WorkItemId} after it went Done", w.Id);
            }
        }

        // 3. Claim Ready items, room permitting.
        var hasActiveHumanFeedback = poll.ActiveItems.Any(w => w.Status == RemoteWorkItemStatus.HumanFeedback);
        var blockedByCap = false;

        // Counted even while paused: the board shows which Ready items wait on
        // their repository either way. A failure here fails the pass before any
        // claim, so a limit whose slots cannot be counted is never exceeded.
        var repositorySlots = await CountRepositorySlotsAsync(opts, poll.ReadyItems, ct);
        RepositorySlots? SlotsOf(RemoteWorkItem w)
            => w.RepositoryId is { } id ? repositorySlots.GetValueOrDefault(id) : null;
        var attempted = new HashSet<string>(StringComparer.Ordinal);

        // While paused, leave Ready items untouched: the whole claim loop is the
        // auto-promotion path, so skipping it is what "pause" means. A human can
        // still start a Ready item by hand (StartIgnoringRepositoryLimitAsync).
        foreach (var ready in claimReadyItems ? poll.ReadyItems : Enumerable.Empty<RemoteWorkItem>())
        {
            // Before the global cap and template resolution, so an item its
            // repository holds back is left exactly as it is: still Ready, with
            // no transition and no status reason. It is reported after the loop.
            if (SlotsOf(ready) is { Full: true }) continue;
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
            attempted.Add(ready.Id);

            var outcome = await ClaimAndStartAsync(opts, ready, _scheduler, "Claimed to run", ct);
            if (outcome.Running) slotHolders.Add(ready.Id);
            if (outcome.Claimed) claimed.Add(ready);
            if (outcome.Escalated) escalated.Add(ready);
            // Unlike the global slot, never handed back in this pass: a run
            // that fails to start leaves the item in Running or HumanFeedback,
            // and both hold the repository slot.
            if (outcome.HoldsRepositorySlot) SlotsOf(ready)?.Take();
        }

        // Counts only grow within a pass, so an item skipped above is still
        // held here; this also catches items the global cap or a cancellation
        // never let the loop reach, and every Ready item while paused.
        var held = poll.ReadyItems
            .Where(w => !attempted.Contains(w.Id) && SlotsOf(w) is { Full: true })
            .ToList();
        var changed = _repositoryHolds.Replace(held.ToDictionary(
            w => w.Id, w => SlotsOf(w)!.ToHold(), StringComparer.Ordinal));
        // Only items still Ready: claimed and escalated ones have had their own
        // event this pass, and one that left Ready since the last pass must not
        // be announced as Ready.
        var stillReady = poll.ReadyItems.Select(w => w.Id)
            .Except(claimed.Concat(escalated).Select(w => w.Id), StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var id in changed.Where(stillReady.Contains))
            await _workItemNotifier.WorkItemStateChangedAsync(id, RemoteWorkItemStatus.Ready, RemoteWorkItemStatus.Ready);

        return new PollCycleResult
        {
            HeldByRepositoryLimit = held,
            Claimed = claimed,
            Resumed = resumed,
            EscalatedToHumanFeedback = escalated,
            HasActiveHumanFeedback = hasActiveHumanFeedback,
            SlotHolders = slotHolders.OrderBy(id => id, StringComparer.Ordinal).ToList(),
            BlockedByCap = blockedByCap,
        };
    }

    public async Task<HandStartResult> StartIgnoringRepositoryLimitAsync(WorkItemServerOptions opts, string workItemId, int maxConcurrent, WorkItemActor actor, CancellationToken ct = default)
    {
        using var claimTurn = await _claimGate.EnterAsync(ct);

        var item = await _client.GetAsync(opts, workItemId, CancellationToken.None);
        if (item is null) return HandStartResult.NotFound;
        if (item.Status != RemoteWorkItemStatus.Ready) return HandStartResult.NotReady;
        // The same measure of the cap as a pass's slot ledger starts from.
        if ((await _loopRunStore.GetActiveWorkItemIdsAsync()).Count >= maxConcurrent)
            return HandStartResult.AtConcurrencyLimit;

        var outcome = await ClaimAndStartAsync(opts, item, actor,
            "Started by hand, ignoring the repository parallelism limit", CancellationToken.None);
        return outcome.Claimed || outcome.Escalated ? HandStartResult.Started : HandStartResult.NotReady;
    }

    /// <summary>What <see cref="ClaimAndStartAsync"/> did with one Ready item.</summary>
    /// <param name="Claimed">Moved to Running by this claimer, whether or not its run then started.</param>
    /// <param name="Running">Claimed and its run started, so it holds a concurrency slot.</param>
    /// <param name="Escalated">Moved to HumanFeedback, either straight from Ready or after its run failed to start.</param>
    /// <param name="HoldsRepositorySlot">In progress now, by this claimer or another one.</param>
    private readonly record struct ClaimOutcome(bool Claimed, bool Running, bool Escalated, bool HoldsRepositorySlot);

    /// <summary>
    /// Claim one Ready item and start its run: tag → template resolution first,
    /// so no-match / ambiguous cases never enter the Running state on the
    /// server, then the claim, then the run. Checks no limit; that is the
    /// caller's job.
    /// </summary>
    private async Task<ClaimOutcome> ClaimAndStartAsync(
        WorkItemServerOptions opts, RemoteWorkItem ready, WorkItemActor actor, string claimReason, CancellationToken ct)
    {
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
            var transition = await _client.TransitionAsync(opts, ready.Id, new RemoteTransitionRequest
            {
                TargetStatus = RemoteWorkItemStatus.HumanFeedback,
                Reason = reason,
            }, actor, ct);
            if (!transition.Success)
            {
                _logger?.LogWarning("Could not move work item {WorkItemId} to HumanFeedback: {Reason}",
                    ready.Id, transition.Reason);
                return default;
            }
            await _loopRunStore.SetWorkItemStatusReasonAsync(ready.Id, reason);
            await _workItemNotifier.WorkItemStateChangedAsync(
                ready.Id, RemoteWorkItemStatus.Ready, RemoteWorkItemStatus.HumanFeedback);
            // Needs you holds a repository slot, so this item takes one.
            return new ClaimOutcome(Claimed: false, Running: false, Escalated: true, HoldsRepositorySlot: true);
        }

        var claim = await _client.TransitionAsync(opts, ready.Id, new RemoteTransitionRequest
        {
            TargetStatus = RemoteWorkItemStatus.Running,
            Reason = claimReason,
        }, actor, ct);
        if (!claim.Success)
        {
            // Lost-claim race: another instance got there first, so the
            // item is in progress all the same and holds its repository
            // slot. The next poll will simply not see it again.
            return new ClaimOutcome(Claimed: false, Running: false, Escalated: false,
                HoldsRepositorySlot: RepositorySlotStatuses.Contains(claim.ActualStatus));
        }

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
            return new ClaimOutcome(Claimed: true, Running: true, Escalated: false, HoldsRepositorySlot: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Engine failed to start run for claimed work item {WorkItemId}", ready.Id);
            // The claim stands on the server with nothing driving it, so
            // hand it back for review. Release the slot even if the remote
            // transition fails: no live run can use this instance's capacity.
            // StartRunAsync ends any run it had already created as
            // Failed, so no orphan keeps the slot on the next pass, and
            // the reason belongs to the item rather than to that run.
            var notRunning = new ClaimOutcome(Claimed: true, Running: false, Escalated: false, HoldsRepositorySlot: true);
            try
            {
                var startFailure = $"Failed to start run: {ex.Message}";
                var transition = await _client.TransitionAsync(opts, ready.Id, new RemoteTransitionRequest
                {
                    TargetStatus = RemoteWorkItemStatus.HumanFeedback,
                    Reason = startFailure,
                }, actor, ct);
                if (!transition.Success)
                {
                    _logger?.LogWarning("Could not move work item {WorkItemId} to HumanFeedback after run start failure: {Reason}",
                        ready.Id, transition.Reason);
                    return notRunning;
                }
                await _loopRunStore.SetWorkItemStatusReasonAsync(ready.Id, startFailure);
                await _workItemNotifier.WorkItemStateChangedAsync(
                    ready.Id, RemoteWorkItemStatus.Running, RemoteWorkItemStatus.HumanFeedback);
                return notRunning with { Escalated = true };
            }
            catch (Exception unwindEx)
            {
                _logger?.LogWarning(unwindEx,
                    "Failed to hand back work item {WorkItemId} after run start failure", ready.Id);
                return notRunning;
            }
        }
    }

    private static readonly RemoteWorkItemStatus[] RepositorySlotStatuses =
    [
        RemoteWorkItemStatus.Running,
        RemoteWorkItemStatus.HumanFeedback,
        RemoteWorkItemStatus.WaitingForIld,
    ];

    /// <summary>
    /// The slot ledger of every repository with a parallelism limit that has
    /// an item in <paramref name="ready"/>. A repository's slots are taken by
    /// its items on the board in Running, HumanFeedback or WaitingForIld —
    /// read off the server rather than the local runs, because an item can
    /// sit in HumanFeedback with no run behind it. Makes no server call when
    /// no such repository exists.
    /// </summary>
    private async Task<Dictionary<Guid, RepositorySlots>> CountRepositorySlotsAsync(
        WorkItemServerOptions opts, IReadOnlyList<RemoteWorkItem> ready, CancellationToken ct)
    {
        var slots = new Dictionary<Guid, RepositorySlots>();
        if (_providerStore == null) return slots;
        foreach (var repositoryId in ready.Select(w => w.RepositoryId).OfType<Guid>().Distinct())
        {
            var repository = await _providerStore.GetRepositoryByIdAsync(repositoryId);
            if (repository is { Parallelism: > 0 })
                slots[repositoryId] = new RepositorySlots(repository.Parallelism);
        }
        if (slots.Count == 0) return slots;

        var holders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var status in RepositorySlotStatuses)
        {
            foreach (var w in await _client.ListAsync(opts, status, null, ct))
            {
                if (w.RepositoryId is { } id && slots.TryGetValue(id, out var repositorySlots) && holders.Add(w.Id))
                    repositorySlots.Take();
            }
        }
        return slots;
    }

    private sealed class RepositorySlots(int limit)
    {
        private int _inProgress;

        public bool Full => _inProgress >= limit;

        public void Take() => _inProgress++;

        public RepositoryParallelismHold ToHold() => new(_inProgress, limit);
    }

    /// <summary>
    /// True if <paramref name="item"/>'s <paramref name="run"/> is not parked on
    /// an AI node, or the provider it will actually execute against currently
    /// has spare capacity. That provider comes from
    /// <see cref="AiNodeProviderResolver"/>, the same resolution
    /// <c>AINodeExecutor</c> makes before claiming its slot. Peeking a
    /// different provider than the executor claims would strand the run (gate
    /// on a full provider the run never uses) or flap it (resume, then
    /// immediately re-park at the executor's gate).
    /// Re-evaluated each poll so changes to provider parallelism settings and
    /// tags take effect without restart.
    /// </summary>
    private async Task<bool> HasProviderCapacityForResumeAsync(RemoteWorkItem item, LoopRun run, CancellationToken ct)
    {
        if (_providerStore == null || _aiTracker == null) return true;
        try
        {
            if (run.CurrentNodeId is not { } currentNodeId) return true;

            var nodes = await _loopRunStore.GetNodesForVersionAsync(run.LoopTemplateVersionId);
            var node = nodes.FirstOrDefault(n => n.Id == currentNodeId);
            if (node == null || node.NodeType != ILD.Data.Enums.NodeType.AI) return true;

            var tag = NodeConfig.Parse<NodeConfig.Ai>(node.Config).AiProviderTag;
            var (provider, _) = await AiNodeProviderResolver.ResolveAsync(
                _providerStore, tag, item.AiProviderOverride, item.AiProviderOverrideId);
            if (provider == null) return true; // let the executor report the missing provider

            return _aiTracker.HasCapacity(provider.Id, provider.Parallelism);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Provider capacity check failed for work item {WorkItemId}", item.Id);
            return true; // be permissive on errors so we don't strand work items
        }
    }
}
