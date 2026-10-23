using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    public FinishedRunCleaner(IServiceScopeFactory scopes, ILogger<FinishedRunCleaner> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public async Task<bool> RemoveLocalStateAsync(Guid runId, bool allowFailed = false)
    {
        if (!_inFlight.TryAdd(runId, 0)) return false;
        try
        {
            using var scope = _scopes.CreateScope();
            var sp = scope.ServiceProvider;

            // The engine is a singleton that ends runs through here, so it is looked
            // up per call rather than taken in the constructor.
            if (sp.GetService<ILoopEngine>() is { } engine && (await engine.GetActiveRunIdsAsync()).Contains(runId))
                return false;

            var runs = sp.GetRequiredService<ILoopRunStore>();
            var run = await runs.GetByIdAsync(runId);
            if (run is null || run.Retain) return false;
            if (run.Status is not (LoopRunStatus.Completed or LoopRunStatus.Cancelled)
                && !(allowFailed && run.Status == LoopRunStatus.Failed))
                return false;

            var outcome = await RunCleanup.CleanUpAsync(
                run, sp.GetRequiredService<IRunReclaimer>(), runs, sp.GetRequiredService<IAdapterSessionSnapshotStore>());
            if (outcome == RunCleanupOutcome.ReclaimFailed)
                _log.LogWarning("Run {RunId} ended but its worktree or branch could not be removed; the retention sweeper retries", runId);
            return outcome == RunCleanupOutcome.Cleaned;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Removing the local state of ended run {RunId} failed; the retention sweeper retries", runId);
            return false;
        }
        finally
        {
            _inFlight.TryRemove(runId, out _);
        }
    }
}
