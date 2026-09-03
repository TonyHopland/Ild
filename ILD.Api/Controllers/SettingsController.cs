using System.ComponentModel.DataAnnotations;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

/// <summary>
/// Runtime-configurable key/value settings (e.g. scheduler max concurrent,
/// paused flag). Keys are validated against an allow-list so the surface is
/// auditable and typo-resistant.
/// </summary>
[ApiController]
[Route("api/v1/settings")]
public class SettingsController : ControllerBase
{
    private readonly IAppSettingStore _store;
    private readonly IWorkItemNotifier _notifier;
    private readonly IWorkItemScheduler _scheduler;
    private readonly ISchedulerSettingsService _schedulerSettings;
    private readonly ILD.Core.Services.Remote.IPrStatusPoller _prPoller;
    private readonly IEgressPolicy _egressPolicy;
    private readonly INetworkNotifier _networkNotifier;

    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        AppSettingKeys.SchedulerMaxConcurrent,
        AppSettingKeys.SchedulerIsPaused,
        AppSettingKeys.RunRetentionDays,
        AppSettingKeys.PrHeartbeatSeconds,
        AppSettingKeys.MaxAiTraversals,
        AppSettingKeys.ThrottleAutoResume,
        AppSettingKeys.SessionIdleDays,
        AppSettingKeys.SessionMaxDays,
        AppSettingKeys.NetworkMode,
    };

    public SettingsController(
        IAppSettingStore store,
        IWorkItemNotifier notifier,
        IWorkItemScheduler scheduler,
        ISchedulerSettingsService schedulerSettings,
        ILD.Core.Services.Remote.IPrStatusPoller prPoller,
        IEgressPolicy egressPolicy,
        INetworkNotifier networkNotifier)
    {
        _store = store;
        _notifier = notifier;
        _scheduler = scheduler;
        _schedulerSettings = schedulerSettings;
        _prPoller = prPoller;
        _egressPolicy = egressPolicy;
        _networkNotifier = networkNotifier;
    }

    public sealed class UpdateSettingRequest
    {
        [Required]
        public string Value { get; set; } = string.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var all = await _store.GetAllAsync(ct);
        // Always return the canonical keys (with defaults if missing) so the
        // frontend never has to special-case absence.
        var map = all.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
        if (!map.ContainsKey(AppSettingKeys.SchedulerMaxConcurrent))
            map[AppSettingKeys.SchedulerMaxConcurrent] = AppSettingKeys.DefaultMaxConcurrent.ToString();
        if (!map.ContainsKey(AppSettingKeys.SchedulerIsPaused))
            map[AppSettingKeys.SchedulerIsPaused] = AppSettingKeys.DefaultIsPaused.ToString().ToLowerInvariant();
        if (!map.ContainsKey(AppSettingKeys.RunRetentionDays))
            map[AppSettingKeys.RunRetentionDays] = AppSettingKeys.DefaultRunRetentionDays.ToString();
        if (!map.ContainsKey(AppSettingKeys.PrHeartbeatSeconds))
            map[AppSettingKeys.PrHeartbeatSeconds] = AppSettingKeys.DefaultPrHeartbeatSeconds.ToString();
        if (!map.ContainsKey(AppSettingKeys.MaxAiTraversals))
            map[AppSettingKeys.MaxAiTraversals] = AppSettingKeys.DefaultMaxAiTraversals.ToString();
        if (!map.ContainsKey(AppSettingKeys.ThrottleAutoResume))
            map[AppSettingKeys.ThrottleAutoResume] = AppSettingKeys.DefaultThrottleAutoResume.ToString().ToLowerInvariant();
        if (!map.ContainsKey(AppSettingKeys.SessionIdleDays))
            map[AppSettingKeys.SessionIdleDays] = AppSettingKeys.DefaultSessionIdleDays.ToString();
        if (!map.ContainsKey(AppSettingKeys.SessionMaxDays))
            map[AppSettingKeys.SessionMaxDays] = AppSettingKeys.DefaultSessionMaxDays.ToString();
        if (!map.ContainsKey(AppSettingKeys.NetworkMode))
            map[AppSettingKeys.NetworkMode] = AppSettingKeys.DefaultNetworkMode;
        return Ok(map.Select(kv => new { key = kv.Key, value = kv.Value }));
    }

    [HttpGet("{key}")]
    public async Task<IActionResult> Get(string key, CancellationToken ct)
    {
        if (!KnownKeys.Contains(key)) return NotFound();
        var setting = await _store.GetByKeyAsync(key, ct);
        var value = setting?.Value ?? DefaultFor(key);
        return Ok(new { key, value });
    }

    [HttpPut("{key}")]
    public async Task<IActionResult> Put(string key, [FromBody] UpdateSettingRequest request, CancellationToken ct)
    {
        if (!KnownKeys.Contains(key)) return NotFound(new { error = $"Unknown setting key '{key}'" });
        if (!ValidateValue(key, request.Value, out var error)) return BadRequest(new { error });

        await _store.UpsertAsync(key, request.Value, ct);

        // Settings drive scheduler behaviour: wake it so the change takes
        // effect immediately and broadcast so the UI can sync.
        if (key == AppSettingKeys.SchedulerIsPaused || key == AppSettingKeys.SchedulerMaxConcurrent)
        {
            _scheduler.Pulse();
            var isPaused = await _schedulerSettings.GetIsPausedAsync(ct);
            var max = await _schedulerSettings.GetMaxConcurrentAsync(ct);
            await _notifier.SchedulerStateChangedAsync(isPaused, max);
        }
        else if (key == AppSettingKeys.PrHeartbeatSeconds)
        {
            // Wake the poller so a shortened heartbeat takes effect now rather
            // than after the previous (possibly long) interval elapses.
            _prPoller.Pulse();
        }
        else if (key == AppSettingKeys.NetworkMode)
        {
            // The proxy judges the agent's next connection by the new mode, and
            // re-judges the tunnels already open under the old one.
            _egressPolicy.Invalidate();
            await _networkNotifier.PolicyChangedAsync();
        }

        return Ok(new { key, value = request.Value });
    }

    private static string DefaultFor(string key) => key switch
    {
        AppSettingKeys.SchedulerMaxConcurrent => AppSettingKeys.DefaultMaxConcurrent.ToString(),
        AppSettingKeys.SchedulerIsPaused => AppSettingKeys.DefaultIsPaused.ToString().ToLowerInvariant(),
        AppSettingKeys.RunRetentionDays => AppSettingKeys.DefaultRunRetentionDays.ToString(),
        AppSettingKeys.PrHeartbeatSeconds => AppSettingKeys.DefaultPrHeartbeatSeconds.ToString(),
        AppSettingKeys.MaxAiTraversals => AppSettingKeys.DefaultMaxAiTraversals.ToString(),
        AppSettingKeys.ThrottleAutoResume => AppSettingKeys.DefaultThrottleAutoResume.ToString().ToLowerInvariant(),
        AppSettingKeys.SessionIdleDays => AppSettingKeys.DefaultSessionIdleDays.ToString(),
        AppSettingKeys.SessionMaxDays => AppSettingKeys.DefaultSessionMaxDays.ToString(),
        AppSettingKeys.NetworkMode => AppSettingKeys.DefaultNetworkMode,
        _ => string.Empty,
    };

    private static bool ValidateValue(string key, string value, out string error)
    {
        switch (key)
        {
            case AppSettingKeys.SchedulerMaxConcurrent:
                if (!int.TryParse(value, out var n) || n < 1 || n > 1000)
                {
                    error = "scheduler.maxConcurrent must be an integer between 1 and 1000";
                    return false;
                }
                break;
            case AppSettingKeys.SchedulerIsPaused:
                if (!bool.TryParse(value, out _))
                {
                    error = "scheduler.isPaused must be 'true' or 'false'";
                    return false;
                }
                break;
            case AppSettingKeys.RunRetentionDays:
                if (!int.TryParse(value, out var days) || days < 0 || days > 3650)
                {
                    error = "run.retentionDays must be an integer between 0 (disabled) and 3650";
                    return false;
                }
                break;
            case AppSettingKeys.PrHeartbeatSeconds:
                if (!int.TryParse(value, out var secs) || secs < 5 || secs > 3600)
                {
                    error = "pr.heartbeatSeconds must be an integer between 5 and 3600";
                    return false;
                }
                break;
            case AppSettingKeys.MaxAiTraversals:
                if (!int.TryParse(value, out var aiSteps) || aiSteps < 1 || aiSteps > 1000)
                {
                    error = "ai.maxTraversals must be an integer between 1 and 1000";
                    return false;
                }
                break;
            case AppSettingKeys.ThrottleAutoResume:
                if (!bool.TryParse(value, out _))
                {
                    error = "throttle.autoResume must be 'true' or 'false'";
                    return false;
                }
                break;
            case AppSettingKeys.SessionIdleDays:
                if (!int.TryParse(value, out var idle) || idle < 0 || idle > 3650)
                {
                    error = "session.idleDays must be an integer between 0 (never) and 3650";
                    return false;
                }
                break;
            case AppSettingKeys.SessionMaxDays:
                if (!int.TryParse(value, out var maxDays) || maxDays < 0 || maxDays > 3650)
                {
                    error = "session.maxDays must be an integer between 0 (never) and 3650";
                    return false;
                }
                break;
            case AppSettingKeys.NetworkMode:
                if (!EgressRules.TryParseMode(value, out _))
                {
                    error = "network.mode must be 'off', 'whitelist' or 'blacklist'";
                    return false;
                }
                break;
        }
        error = string.Empty;
        return true;
    }
}
