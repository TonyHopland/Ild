using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// "Waiting for ILD": the agent ended its turn while processes it started were still running. The node is not finished and its
/// session and worktree stay; <see cref="ChildWaitService"/> waits for the processes and wakes the agent, and a person can
/// Restart the node (stopping them) or Resume it (leaving them running) meanwhile.
/// </summary>
public sealed partial class LoopEngine
{
    /// <summary>The line the event log and the park reason give for the processes a node is waiting for: pid and command line of each.</summary>
    internal static string DescribeProcesses(IReadOnlyList<RunChildProcess> processes)
        => string.Join("; ", processes.Select(p => $"pid {p.Pid}: {p.CommandLine}"));

    /// <summary>
    /// The node's turn ended with <paramref name="children"/> still running. Parks the run <c>WaitingHuman</c> + <c>IsHalted</c> stamped
    /// <see cref="HaltReason.WaitingForChildren"/>, so the halt-resume machinery serves it, with the work item Waiting for ILD rather than
    /// asking a person for anything. Past <c>children.maxCycles</c> in a row it is an ordinary halt instead, for a person.
    /// </summary>
    private async Task ParkForChildrenAsync(
        LoopRun run, LoopNode node, IReadOnlyList<RunChildProcess> children, Guid? runNodeId,
        ILoopRunStore store, IServiceProvider sp, IWorkItemManager workItems, IEventLogService? eventLog)
    {
        var settings = sp.GetService<ISchedulerSettingsService>();
        var maxCycles = settings is null ? AppSettingKeys.DefaultChildrenMaxCycles : await settings.GetChildrenMaxCyclesAsync();
        var listing = DescribeProcesses(children);
        run.ChildWaitJson = new ChildWaitState(DateTime.UtcNow, children).ToJson();

        if (run.ChildWaitCycleCount >= maxCycles)
        {
            _logger.LogWarning("Run {RunId}: node {NodeLabel} ended its turn with processes running {Max} times in a row; parked for a person", run.Id, node.Label, maxCycles);
            await ParkForHumanAsync(run, node.Id, node.Label, HaltReason.Human, HumanFeedbackReasons.RunHalted,
                $"The agent ended its turn with processes still running {maxCycles} times in a row, so ILD stopped waiting for them. Still running: {listing}. Restart the node (stops them) or resume it (leaves them running).",
                store, workItems, eventLog, runNodeId);
            return;
        }

        run.ChildWaitCycleCount++;
        var old = run.Status;
        run.Status = LoopRunStatus.WaitingHuman;
        run.IsHalted = true;
        run.HaltReason = HaltReason.WaitingForChildren;
        run.HumanFeedbackReason = HumanFeedbackReasons.WaitingForChildren;
        await store.UpdateRunAsync(run);
        _logger.LogInformation("Run {RunId}: node {NodeLabel} ended its turn with {Count} process(es) still running; waiting for ILD ({Processes})", run.Id, node.Label, children.Count, listing);
        if (eventLog is not null)
            await TrySafe(run.Id, () => eventLog.AppendAsync(run.Id, EventType.RunParked, $"Waiting for ILD: the agent ended its turn with {children.Count} process(es) still running: {listing}", node.Id, runNodeId));
        await _notifier.RunStateChangedAsync(run.Id, old, LoopRunStatus.WaitingHuman);
        await _notifier.HaltedAsync(run.Id);
        await workItems.TransitionAsync(RunActor(run.Id, run.WorkItemId, node.Label), run.WorkItemId, RemoteWorkItemStatus.WaitingForIld,
            reason: "Waiting for ILD: the agent's processes are still running", humanFeedbackReason: HumanFeedbackReasons.WaitingForChildren, currentLoopRunId: run.Id);
    }

    /// <summary>The attempt of the run's current node that is waiting for its children, the last one.</summary>
    private static async Task<LoopRunNode?> WaitingAttemptAsync(ILoopRunStore store, LoopRun run)
        => (await store.GetRunNodesAsync(run.Id)).LastOrDefault(rn => rn.LoopNodeId == run.CurrentNodeId && rn.Status == LoopRunNodeStatus.WaitingIld);

    /// <summary>Marks the waiting attempt of the run's node <paramref name="status"/>: it is no longer waiting once a person or the wake-up acts.</summary>
    private async Task SettleWaitingAttemptAsync(ILoopRunStore store, LoopRun run, LoopRunNodeStatus status, string? error)
    {
        if (await WaitingAttemptAsync(store, run) is not { } attempt) return;
        attempt.Status = status;
        attempt.Error = error;
        await store.UpdateRunNodeAsync(attempt);
        await _notifier.NodeStateChangedAsync(run.Id, attempt.LoopNodeId, LoopRunNodeStatus.WaitingIld, status);
    }

    /// <summary>The run, when it is parked with children it left running (waiting for them, or parked for a person after the cycle cap); otherwise refused.</summary>
    private static async Task<LoopRun> RequireChildWaitAsync(ILoopRunStore store, Guid runId)
    {
        var run = await store.GetByIdAsync(runId) ?? throw new InvalidOperationException("Run not found");
        if (run.Status != LoopRunStatus.WaitingHuman || !run.IsHalted || ChildWaitState.TryParse(run.ChildWaitJson) is null)
            throw new InvalidOperationException("The run is not waiting for the processes its agent left running");
        return run;
    }

    public async Task ResumeWaitingForChildrenAsync(Guid runId, string? note, string user)
    {
        using (var scope = _sp.CreateScope())
        {
            var run = await RequireChildWaitAsync(scope.ServiceProvider.GetRequiredService<ILoopRunStore>(), runId);
            await LogChildWaitActionAsync(scope.ServiceProvider, run, user, "Resume", "the processes keep running", note, recordNote: false);
        }
        await ResumeFromHaltAsync(runId, note);
    }

    public async Task RestartWaitingForChildrenAsync(Guid runId, string? note, string user)
    {
        Guid attemptId;
        using (var scope = _sp.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var store = sp.GetRequiredService<ILoopRunStore>();
            var run = await RequireChildWaitAsync(store, runId);
            var attempt = await WaitingAttemptAsync(store, run)
                ?? (await store.GetRunNodesAsync(runId)).LastOrDefault(rn => rn.LoopNodeId == run.CurrentNodeId)
                ?? throw new InvalidOperationException("The node that was waiting was not found");
            attemptId = attempt.Id;
            var processes = ChildWaitState.TryParse(run.ChildWaitJson)!.Processes;
            await LogChildWaitActionAsync(sp, run, user, "Restart", $"stops {processes.Count} process(es)", note, recordNote: true);
        }
        await RetryCoreAsync(runId, attemptId, resumeSession: false, note);
    }

    private async Task LogChildWaitActionAsync(IServiceProvider sp, LoopRun run, string user, string action, string effect, string? note, bool recordNote)
    {
        if (sp.GetService<IEventLogService>() is not { } eventLog) return;
        var gaveInput = !string.IsNullOrWhiteSpace(note);
        await TrySafe(run.Id, () => eventLog.AppendAsync(run.Id, EventType.ChildWaitActionTaken,
            $"{user} chose {action} for the node waiting for ILD ({effect}); input given: {(gaveInput ? "yes" : "no")}", run.CurrentNodeId));
        if (gaveInput && recordNote)
            await TrySafe(run.Id, () => eventLog.AppendAsync(run.Id, EventType.HumanFeedbackReceived, note!));
    }

    public async Task ReleaseChildWaitToHumanAsync(Guid runId, string reason)
    {
        using var scope = _sp.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<ILoopRunStore>();
        var run = await store.GetByIdAsync(runId);
        if (run is null || run.Status != LoopRunStatus.WaitingHuman || !run.IsHalted || run.HaltReason != HaltReason.WaitingForChildren) return;
        var nodes = await sp.GetRequiredService<ILoopTemplateStore>().GetNodesForVersionAsync(run.LoopTemplateVersionId);
        var node = nodes.FirstOrDefault(n => n.Id == run.CurrentNodeId);
        await ParkForHumanAsync(run, node?.Id, node?.Label, HaltReason.Human, HumanFeedbackReasons.RunHalted, reason,
            store, sp.GetRequiredService<IWorkItemManager>(), sp.GetService<IEventLogService>(), (await WaitingAttemptAsync(store, run))?.Id);
    }
}
