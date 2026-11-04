using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Keeps the installed managed coding agents up to date. It always runs: the only way to stop a
/// version is to roll back to the one before (which holds newer ones back) or to update by hand.
/// It wakes every hour and does two things:
/// <list type="bullet">
///   <item><b>A version check</b>, when one is due: each installed agent gets the newest version
///   published at least <see cref="AgentUpdateOptions.CooldownDays"/> days ago (and newer than any
///   version a rollback holds back), switched in at once whether or not the agent is in use, since
///   a running process keeps the install it was launched from and only new launches follow the
///   switch. A check is due every hour, or, with a cooldown above one day, every cooldown/7 days
///   between one and 24 hours, since a longer cooldown means a new release can only become
///   eligible that much later. A wake that is not due makes no registry call.</item>
///   <item><b>An idle cleanup</b>, on every wake: installs that are neither the active one nor the
///   rollback target are deleted for each agent that nothing is using. What was kept because the
///   agent was busy is removed at the first wake that finds it idle.</item>
/// </list>
/// Agents that are not installed are never installed here. A failing agent is left as it is, and
/// tried again at the next check (or, for the cleanup, the next wake).
/// </summary>
public sealed class ManagedAgentAutoUpdater : BackgroundService
{
    private static readonly TimeSpan WakeInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MinCheckInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxCheckInterval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopes;
    private readonly AgentUpdateOptions _options;
    private readonly AutoInstallForecast _forecast;
    private readonly ILogger<ManagedAgentAutoUpdater> _log;

    // When the last version check finished; written only by the single wake loop.
    private DateTimeOffset? _lastChecked;

    public ManagedAgentAutoUpdater(
        IServiceScopeFactory scopes,
        AgentUpdateOptions options,
        AutoInstallForecast forecast,
        ILogger<ManagedAgentAutoUpdater> log)
    {
        _scopes = scopes;
        _options = options;
        _forecast = forecast;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = InitialDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { return; }
            delay = WakeInterval;

            try
            {
                await RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Managed agent update pass failed");
            }
        }
    }

    /// <summary>How long after a check the next one is due.</summary>
    internal static TimeSpan CheckInterval(int cooldownDays)
    {
        if (cooldownDays <= 1) return MinCheckInterval;
        var interval = TimeSpan.FromDays(cooldownDays / 7.0);
        return TimeSpan.FromTicks(Math.Clamp(interval.Ticks, MinCheckInterval.Ticks, MaxCheckInterval.Ticks));
    }

    private async Task UpdateAsync(IManagedAgentService agents, ManagedAgent agent, TimeSpan cooldown, DateTimeOffset now, CancellationToken ct)
    {
        var installed = (await agents.GetStatusAsync(agent, ct)).InstalledVersion;
        if (installed is null) return;

        var plan = await agents.GetAutoUpdatePlanAsync(agent.Key, cooldown, now, ct);
        if (plan is null) return;

        if (plan.Next is { } next && plan.NextAfter is { } after) _forecast.Set(agent.Key, next, after);
        else _forecast.Clear(agent.Key);

        if (plan.Target is { } target && ManagedAgentService.IsNewer(target, installed)
            && await agents.AutoUpdateAsync(agent.Key, target, ct) == AutoUpdateResult.Installed)
        {
            _log.LogInformation("Updated managed agent {Agent} from {Installed} to {Target}", agent.Key, installed, target);
        }
    }

    /// <summary>Runs one step for one agent; a failure is logged and the next step and agent go on.</summary>
    private async Task GuardedAsync(ManagedAgent agent, string step, Func<Task> work, CancellationToken ct)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not {Step} managed agent {Agent}; it is retried later", step, agent.Key);
        }
    }

    internal async Task RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var agents = scope.ServiceProvider.GetRequiredService<IManagedAgentService>();

        var due = _lastChecked is not { } last || now - last >= CheckInterval(_options.CooldownDays);
        var cooldown = TimeSpan.FromDays(_options.CooldownDays);

        foreach (var agent in agents.Agents)
        {
            if (due)
            {
                await GuardedAsync(agent, "update", () => UpdateAsync(agents, agent, cooldown, now, ct), ct);
            }
            // Its own step: an update that keeps failing must not keep old installs on disk.
            await GuardedAsync(agent, "clean up", () => agents.RemoveOldVersionsWhenIdleAsync(agent.Key, ct), ct);
        }

        if (due) _lastChecked = now;
    }
}
