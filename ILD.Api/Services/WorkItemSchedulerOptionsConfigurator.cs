using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Options;

namespace ILD.Api.Services;

/// <summary>
/// Configures <see cref="WorkItemSchedulerOptions"/> from the global WorkItem
/// server settings stored in the AppSettings table (<c>workItemServer.*</c>).
/// User-tunable scheduler knobs (max concurrent runs, paused) are not mirrored
/// here — the scheduler reads them live from the AppSettings table.
/// </summary>
public sealed class WorkItemSchedulerOptionsConfigurator : IConfigureOptions<WorkItemSchedulerOptions>
{
    private readonly IServiceProvider _sp;

    public WorkItemSchedulerOptionsConfigurator(IServiceProvider sp) => _sp = sp;

    public void Configure(WorkItemSchedulerOptions options)
    {
        using var scope = _sp.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAppSettingStore>();

        var url = store.GetByKeyAsync(AppSettingKeys.WorkItemServerUrl).GetAwaiter().GetResult()?.Value;
        if (string.IsNullOrEmpty(url))
        {
            options.Enabled = false;
            return;
        }

        var apiKey = store.GetByKeyAsync(AppSettingKeys.WorkItemServerApiKey).GetAwaiter().GetResult()?.Value;
        var pollRaw = store.GetByKeyAsync(AppSettingKeys.WorkItemServerPollIntervalSeconds).GetAwaiter().GetResult()?.Value;
        var graceRaw = store.GetByKeyAsync(AppSettingKeys.WorkItemServerGraceIntervalSeconds).GetAwaiter().GetResult()?.Value;

        var poll = int.TryParse(pollRaw, out var p) ? p : AppSettingKeys.DefaultPollIntervalSeconds;
        var grace = int.TryParse(graceRaw, out var g) ? g : AppSettingKeys.DefaultGraceIntervalSeconds;

        options.Enabled = true;
        options.BaseUrl = url;
        options.ApiKey = apiKey ?? string.Empty;
        options.PollInterval = TimeSpan.FromSeconds(Math.Max(1, poll));
        options.GracePollInterval = TimeSpan.FromSeconds(Math.Max(1, grace));
    }
}
