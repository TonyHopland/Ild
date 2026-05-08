namespace ILD.Core.Services.Remote;

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

    public RemoteWorkItemCoordinator(
        IWorkItemServerClient client,
        IActiveWorkItemTracker tracker,
        ILoopTemplateResolver resolver)
    {
        _client = client;
        _tracker = tracker;
        _resolver = resolver;
    }

    public async Task<PollCycleResult> RunPollCycleAsync(WorkItemServerOptions opts, int maxConcurrent, CancellationToken ct = default)
    {
        var poll = await _client.PollAsync(opts, _tracker.Snapshot(), ct);

        var claimed = new List<RemoteWorkItem>();
        var resumed = new List<RemoteWorkItem>();
        var escalated = new List<RemoteWorkItem>();

        // 1. Resume anything the server has flipped to WaitingForIld — it
        //    means a human responded and the engine should pick the run back
        //    up. Server transitions are permissive, so a Running re-claim
        //    succeeds without a dependency check.
        foreach (var w in poll.ActiveItems.Where(w => w.Status == RemoteWorkItemStatus.WaitingForIld))
        {
            var resp = await _client.TransitionAsync(opts, w.Id,
                new RemoteTransitionRequest { TargetStatus = RemoteWorkItemStatus.Running }, ct);
            if (resp.Success) resumed.Add(w);
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
}
