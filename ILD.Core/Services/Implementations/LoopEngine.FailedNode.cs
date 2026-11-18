using ILD.Core.Services.Interfaces;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations;

/// <summary>Retry and resume of a node that failed and parked the run for a person.</summary>
public sealed partial class LoopEngine
{
    private const string NotFailedForRetry = "The run is not parked on a failed node";

    public Task RetryAfterFailureAsync(Guid runId, string? note, string user)
        => ActAfterFailureAsync(runId, note, user, resumeSession: false);

    public Task ResumeAfterFailureAsync(Guid runId, string? note, string user)
        => ActAfterFailureAsync(runId, note, user, resumeSession: true);

    private async Task ActAfterFailureAsync(Guid runId, string? note, string user, bool resumeSession)
    {
        Guid attemptId;
        Guid? nodeId;
        using (var scope = _sp.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
            var run = await store.GetByIdAsync(runId) ?? throw new InvalidOperationException("Run not found");
            if (run.Status != LoopRunStatus.WaitingHuman || run.IsHalted
                || run.HumanFeedbackReason is not (HumanFeedbackReasons.OutOfMemory or HumanFeedbackReasons.NodeFailed))
                throw new InvalidOperationException(NotFailedForRetry);
            var attempt = (await store.GetRunNodesAsync(runId)).LastOrDefault(rn => rn.LoopNodeId == run.CurrentNodeId && rn.Status == LoopRunNodeStatus.Failed)
                ?? throw new InvalidOperationException(NotFailedForRetry);
            attemptId = attempt.Id;
            nodeId = run.CurrentNodeId;
        }
        var action = resumeSession ? "Resume" : "Retry";
        await RetryCoreAsync(runId, attemptId, resumeSession, note, onAccepted: sp => LogFailedNodeActionAsync(sp, runId, nodeId, user, action, note));
    }

    private async Task LogFailedNodeActionAsync(IServiceProvider sp, Guid runId, Guid? nodeId, string user, string action, string? note)
    {
        if (sp.GetService<IEventLogService>() is not { } eventLog) return;
        var gaveInput = !string.IsNullOrWhiteSpace(note);
        await TrySafe(runId, () => eventLog.AppendAsync(runId, EventType.FailedNodeActionTaken,
            $"{user} chose {action} for the failed node; input given: {(gaveInput ? "yes" : "no")}", nodeId));
        if (gaveInput)
            await TrySafe(runId, () => eventLog.AppendAsync(runId, EventType.HumanFeedbackReceived, note!));
    }
}
