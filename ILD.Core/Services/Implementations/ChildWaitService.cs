using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// ILD's half of "Waiting for ILD": a node whose agent ended its turn with processes of its own still running is parked
/// (<see cref="Data.Enums.HaltReason.WaitingForChildren"/>) with its session and worktree kept, and this service watches those processes. When
/// they have all ended it wakes the agent with the same resume a person's Resume gives (<see cref="ILoopEngine.WakeFromChildWaitAsync"/>), carrying how
/// they ended and the tail of their output, so the agent can go on (report the test results). After <c>children.waitMinutes</c>
/// it stops them and wakes the agent anyway, saying so; if waking fails the run stays parked for a person, who can still Restart or Resume the node.
///
/// <para>It is the same idea as the throttle sweeper (<see cref="ThrottledRunResumeSweeper"/>), and like it only ever resumes the one park that is its own.</para>
/// </summary>
public sealed class ChildWaitService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILoopEngine _engine;
    private readonly ILogger<ChildWaitService> _log;
    private readonly IMaintenanceState _maintenance;

    public ChildWaitService(IServiceScopeFactory scopes, ILoopEngine engine, ILogger<ChildWaitService> log, IMaintenanceState? maintenance = null)
    {
        _scopes = scopes;
        _engine = engine;
        _log = log;
        _maintenance = maintenance ?? MaintenanceState.Never;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }

            try { await SweepOnceAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "Child-process wait sweep failed"); }
        }
    }

    internal async Task SweepOnceAsync(CancellationToken ct)
    {
        using var work = _maintenance.TryBeginWork();
        if (work is null) return;

        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var waiting = await sp.GetRequiredService<ILoopRunStore>().GetChildWaitingRunsAsync();
        if (waiting.Count == 0) return;

        var timeout = TimeSpan.FromMinutes(await sp.GetRequiredService<ISchedulerSettingsService>().GetChildrenWaitMinutesAsync(ct));
        foreach (var run in waiting)
        {
            if (ct.IsCancellationRequested) return;
            try { await CheckAsync(sp, run, timeout, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Failed to check the processes run {RunId} is waiting for", run.Id);
            }
        }
    }

    private static readonly Func<LoopRun, bool> IsWaiting = LoopRun.WaitingForChildren.Compile();

    /// <summary>Whether the run is still in the wait <paramref name="seen"/> was read in: a person may have acted on it since.</summary>
    private static async Task<bool> StillWaitingAsync(IServiceProvider sp, LoopRun seen)
        => await sp.GetRequiredService<ILoopRunStore>().GetByIdAsync(seen.Id) is { } now && IsWaiting(now) && now.ChildWaitJson == seen.ChildWaitJson;

    private async Task CheckAsync(IServiceProvider sp, LoopRun run, TimeSpan timeout, CancellationToken ct)
    {
        var state = ChildWaitState.TryParse(run.ChildWaitJson);
        var host = sp.GetRunHost();
        var waited = state is null ? timeout : DateTime.UtcNow - state.StartedAt;
        IReadOnlyList<RunChildProcess> running;
        try
        {
            running = state is null ? [] : await host.StillRunningAsync(run.Id, state.Processes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (waited < timeout)
            {
                _log.LogWarning(ex, "Could not check the processes run {RunId} is waiting for; trying again on the next sweep", run.Id);
                return;
            }
            _log.LogError(ex, "Could not check the processes run {RunId} is waiting for, and the wait is over; leaving it parked for a person", run.Id);
            await _engine.ReleaseChildWaitToHumanAsync(run.Id, $"ILD could not check the processes the agent left running: {ex.Message}");
            return;
        }
        if (running.Count > 0 && waited < timeout) return;
        if (!await StillWaitingAsync(sp, run)) return;

        var timedOut = running.Count > 0;
        if (timedOut)
        {
            _log.LogWarning("Run {RunId}: {Count} process(es) its agent left running did not finish within {Timeout}; stopping them and waking the agent", run.Id, running.Count, timeout);
            await host.StopProcessesAsync(run.Id, running, ct);
        }

        var outcomes = new List<ChildOutcome>();
        foreach (var process in state?.Processes ?? [])
        {
            var tail = process.OutputPath is null ? null : await host.ReadTailAsync(run.Id, process.OutputPath, ChildWaitMessages.TailBytes, ct);
            outcomes.Add(new ChildOutcome(process, StoppedByIld: running.Any(r => r.Pid == process.Pid), tail));
        }
        var note = ChildWaitMessages.Build(outcomes, waited, timedOut);

        _log.LogInformation("Waking the agent of run {RunId}: its processes {How}", run.Id, timedOut ? "were stopped at the timeout" : "have finished");
        try
        {
            if (!await _engine.WakeFromChildWaitAsync(run.Id, note))
                _log.LogInformation("Run {RunId} was no longer waiting for ILD when its processes ended; left as it is", run.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Could not wake the agent of run {RunId}; leaving it parked for a person", run.Id);
            await _engine.ReleaseChildWaitToHumanAsync(run.Id, $"ILD could not wake the agent after its processes ended: {ex.Message}");
        }
    }
}
