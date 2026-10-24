using ILD.Core.Services.Interfaces;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <inheritdoc cref="IFinishedRunCleaner"/>
public sealed class FinishedRunCleaner : IFinishedRunCleaner
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<FinishedRunCleaner> _log;
    private readonly object _gate = new();
    // Guarded by _gate.
    private readonly Dictionary<Guid, Request> _inFlight = new();

    private readonly ClaudeProjectFolders _claudeProjects;

    public FinishedRunCleaner(IServiceScopeFactory scopes, ILogger<FinishedRunCleaner> log, ClaudeProjectFolders claudeProjects)
    {
        _scopes = scopes;
        _log = log;
        _claudeProjects = claudeProjects;
    }

    /// <summary>
    /// The removal running for one run. A call that arrives meanwhile may know more
    /// than the running one did when it read the run — that the run has ended, or
    /// that the engine let it go — so it asks for another attempt instead of being
    /// dropped.
    /// </summary>
    private sealed class Request
    {
        public bool Again;
        public bool AllowFailed;
    }

    public async Task<bool> RemoveLocalStateAsync(Guid runId, bool allowFailed = false)
    {
        Request mine;
        lock (_gate)
        {
            if (_inFlight.TryGetValue(runId, out var running))
            {
                running.Again = true;
                running.AllowFailed |= allowFailed;
                return false;
            }
            _inFlight[runId] = mine = new Request { AllowFailed = allowFailed };
        }

        while (true)
        {
            var removed = await RemoveOnceAsync(runId, allowFailed);
            lock (_gate)
            {
                if (removed || !mine.Again)
                {
                    _inFlight.Remove(runId);
                    return removed;
                }
                mine.Again = false;
                allowFailed = mine.AllowFailed;
            }
        }
    }

    private async Task<bool> RemoveOnceAsync(Guid runId, bool allowFailed)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var sp = scope.ServiceProvider;

            if (await RunCleanup.IsDrivenByEngineAsync(sp, runId))
                return false;

            var runs = sp.GetRequiredService<ILoopRunStore>();
            var run = await runs.GetByIdAsync(runId);
            if (run is null || run.Retain) return false;
            if (!RunCleanup.HasEnded(run) && !(allowFailed && run.Status == LoopRunStatus.Failed))
                return false;

            var outcome = await RunCleanup.CleanUpAsync(
                run, sp.GetRequiredService<IRunReclaimer>(), runs, sp.GetRequiredService<IAdapterSessionSnapshotStore>(), _claudeProjects);
            if (outcome == RunCleanupOutcome.ReclaimFailed)
                _log.LogWarning("Run {RunId} ended but its worktree or branch could not be removed; the retention sweeper retries", runId);
            return outcome == RunCleanupOutcome.Cleaned;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Removing the local state of ended run {RunId} failed; the retention sweeper retries", runId);
            return false;
        }
    }
}
