using ILD.Core.Services.Interfaces;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>What ILD does with a run an earlier version parked to wait for the processes its agent left running.</summary>
public sealed partial class LoopEngine
{
    /// <summary>The stored halt reason such a run carries. Its name is gone from <see cref="HaltReason"/> and its number is not reused.</summary>
    private const int RetiredProcessWaitHaltReason = 5;

    internal const string RetiredProcessWaitNote =
        "ILD no longer waits for the processes an agent leaves running, so this node is parked for you instead. Resume it to continue the agent where it stopped.";

    public async Task<int> ParkRunsOfTheRetiredProcessWaitAsync()
    {
        using var scope = _sp.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<ILoopRunStore>();
        var workItems = sp.GetRequiredService<IWorkItemManager>();
        var eventLog = sp.GetService<IEventLogService>();

        var converted = 0;
        foreach (var run in (await store.GetActiveRunsAsync()).Where(r => r.Status == LoopRunStatus.WaitingHuman && r.IsHalted && (int?)r.HaltReason == RetiredProcessWaitHaltReason))
        {
            try
            {
                var attempts = (await store.GetRunNodesAsync(run.Id)).Where(rn => rn.Status == LoopRunNodeStatus.WaitingIld).ToList();
                foreach (var attempt in attempts)
                {
                    attempt.Status = LoopRunNodeStatus.Interrupted;
                    attempt.Error = RetiredProcessWaitNote;
                    await store.UpdateRunNodeAsync(attempt);
                }
                await ParkForHumanAsync(run, run.CurrentNodeId, nodeLabel: null, HaltReason.Human, HumanFeedbackReasons.RunHalted,
                    RetiredProcessWaitNote, store, workItems, eventLog, attempts.LastOrDefault()?.Id);
                converted++;
                _logger.LogInformation("Run {RunId} was waiting for the processes its agent left running; parked for a person instead", run.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not convert run {RunId} from the retired wait for the agent's processes", run.Id);
            }
        }
        return converted;
    }
}
