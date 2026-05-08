using ILD.Core.Services.Remote;
using Microsoft.Extensions.Options;

namespace ILD.Api.Services;

/// <summary>
/// Configures <see cref="RemoteWorkItemPollerOptions"/> from the first
/// configured <see cref="ILD.Data.Entities.RemoteProvider"/> that carries a
/// non-empty <c>WorkItemServerUrl</c>. Resolved at startup; rebooting ILD
/// picks up provider changes.
/// </summary>
public sealed class RemoteWorkItemPollerOptionsConfigurator : IConfigureOptions<RemoteWorkItemPollerOptions>
{
    private readonly IServiceProvider _sp;

    public RemoteWorkItemPollerOptionsConfigurator(IServiceProvider sp) => _sp = sp;

    public void Configure(RemoteWorkItemPollerOptions options)
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
        options.MaxConcurrent = Math.Max(1, provider.MaxConcurrentWorkItems);
    }
}
