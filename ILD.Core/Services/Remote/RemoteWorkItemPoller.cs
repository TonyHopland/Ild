using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ILD.Core.Services.Remote;

/// <summary>
/// Configuration block for <see cref="RemoteWorkItemPoller"/>. Read from
/// the <c>WorkItemServer</c> configuration section. The grace interval is
/// the faster cadence used while at least one local work item is parked in
/// HumanFeedback so human responses propagate quickly.
/// </summary>
public sealed class RemoteWorkItemPollerOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan GracePollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxConcurrent { get; set; } = 5;
    public bool Enabled { get; set; }
}

public sealed class RemoteWorkItemPoller : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<RemoteWorkItemPollerOptions> _options;
    private readonly ILogger<RemoteWorkItemPoller> _log;
    private readonly TimeProvider _time;

    public RemoteWorkItemPoller(
        IServiceScopeFactory scopes,
        IOptionsMonitor<RemoteWorkItemPollerOptions> options,
        ILogger<RemoteWorkItemPoller> log,
        TimeProvider time)
    {
        _scopes = scopes;
        _options = options;
        _log = log;
        _time = time;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _options.CurrentValue;
            var delay = opts.PollInterval;

            if (!opts.Enabled || string.IsNullOrWhiteSpace(opts.BaseUrl))
            {
                await Task.Delay(opts.PollInterval, _time, stoppingToken);
                continue;
            }

            try
            {
                using var scope = _scopes.CreateScope();
                var coord = scope.ServiceProvider.GetRequiredService<IRemoteWorkItemCoordinator>();
                var serverOpts = new WorkItemServerOptions { BaseUrl = opts.BaseUrl, ApiKey = opts.ApiKey };
                var result = await coord.RunPollCycleAsync(serverOpts, opts.MaxConcurrent, stoppingToken);
                if (result.HasActiveHumanFeedback) delay = opts.GracePollInterval;
                if (result.Claimed.Count > 0 || result.Resumed.Count > 0 || result.EscalatedToHumanFeedback.Count > 0)
                {
                    _log.LogInformation(
                        "Remote poll: claimed {Claimed}, resumed {Resumed}, escalated {Escalated}",
                        result.Claimed.Count, result.Resumed.Count, result.EscalatedToHumanFeedback.Count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Remote work item poll failed; will retry in {Delay}", delay);
            }

            try
            {
                await Task.Delay(delay, _time, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }
}
