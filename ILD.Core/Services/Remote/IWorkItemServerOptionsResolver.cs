using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Remote;

/// <summary>
/// Resolves the <see cref="WorkItemServerOptions"/> (URL + API key) that
/// should be used when talking to the remote WorkItemServer. The WorkItem
/// server is a single global configuration stored in the AppSettings table,
/// no longer tied to an individual remote provider. The repository/work-item
/// parameters are retained for signature compatibility but are not used.
/// </summary>
public interface IWorkItemServerOptionsResolver
{
    Task<WorkItemServerOptions> ResolveForRepositoryAsync(Guid? repositoryId, CancellationToken ct = default);
    Task<WorkItemServerOptions> ResolveForWorkItemAsync(string workItemId, CancellationToken ct = default);
}

/// <summary>
/// Production resolver that reads the global WorkItem server connection
/// (<c>workItemServer.url</c> + <c>workItemServer.apiKey</c>) from the
/// AppSettings, through the process-wide settings cache. Throws when no URL is
/// configured — per the PRD's hard-break contract, ILD requires a remote
/// WorkItemServer.
/// </summary>
public sealed class DbWorkItemServerOptionsResolver : IWorkItemServerOptionsResolver
{
    private readonly IAppSettingStore _settings;

    public DbWorkItemServerOptionsResolver(IAppSettingStore settings) => _settings = settings;

    public async Task<WorkItemServerOptions> ResolveForRepositoryAsync(Guid? repositoryId, CancellationToken ct = default)
    {
        var url = (await _settings.GetByKeyAsync(AppSettingKeys.WorkItemServerUrl, ct))?.Value;

        if (string.IsNullOrEmpty(url))
            throw new InvalidOperationException(
                "No WorkItem server URL is configured. " +
                "ILD requires a remote WorkItemServer for work-item operations.");

        var apiKey = (await _settings.GetByKeyAsync(AppSettingKeys.WorkItemServerApiKey, ct))?.Value;

        return new WorkItemServerOptions
        {
            BaseUrl = url,
            ApiKey = apiKey ?? string.Empty,
        };
    }

    public Task<WorkItemServerOptions> ResolveForWorkItemAsync(string workItemId, CancellationToken ct = default)
        => ResolveForRepositoryAsync(null, ct);
}
