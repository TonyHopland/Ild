using ILD.Core.Services.Remote;
using Microsoft.Extensions.Options;

namespace ILD.Api.Services;

/// <summary>
/// Configures <see cref="WorkItemSchedulerOptions"/> from the first
/// configured <see cref="ILD.Data.Entities.RemoteProvider"/> that carries a
/// non-empty <c>WorkItemServerUrl</c>. User-tunable scheduler knobs (max
/// concurrent runs, paused) are no longer mirrored here \u2014 the scheduler
/// reads them live from the AppSettings table.
/// </summary>
public sealed class WorkItemSchedulerOptionsConfigurator : IConfigureOptions<WorkItemSchedulerOptions>
{
    private readonly IServiceProvider _sp;

    public WorkItemSchedulerOptionsConfigurator(IServiceProvider sp) => _sp = sp;

    public void Configure(WorkItemSchedulerOptions options)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ILD.Data.Entities.AppDbContext>();
        var provider = db.RemoteProviders
            .FirstOrDefault(p => !string.IsNullOrEmpty(p.WorkItemServerUrl));
        if (provider == null)
        {
            options.Enabled = false;
            return;
        }

        options.Enabled = true;
        options.BaseUrl = provider.WorkItemServerUrl ?? string.Empty;
        options.ApiKey = provider.WorkItemApiKey ?? string.Empty;
        options.PollInterval = TimeSpan.FromSeconds(Math.Max(1, provider.PollIntervalSeconds));
        options.GracePollInterval = TimeSpan.FromSeconds(Math.Max(1, provider.GraceIntervalSeconds));
    }
}
