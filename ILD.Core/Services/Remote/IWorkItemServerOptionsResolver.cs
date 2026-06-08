using ILD.Data.Entities;
using ILD.Core.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

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
/// AppSettings table. Throws when no URL is configured — per the PRD's
/// hard-break contract, ILD requires a remote WorkItemServer.
/// </summary>
public sealed class DbWorkItemServerOptionsResolver : IWorkItemServerOptionsResolver
{
    private readonly AppDbContext _db;

    public DbWorkItemServerOptionsResolver(AppDbContext db) => _db = db;

    public async Task<WorkItemServerOptions> ResolveForRepositoryAsync(Guid? repositoryId, CancellationToken ct = default)
    {
        var settings = await _db.AppSettings
            .AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.WorkItemServerUrl || s.Key == AppSettingKeys.WorkItemServerApiKey)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        settings.TryGetValue(AppSettingKeys.WorkItemServerUrl, out var url);

        if (string.IsNullOrEmpty(url))
            throw new InvalidOperationException(
                "No WorkItem server URL is configured. " +
                "ILD requires a remote WorkItemServer for work-item operations.");

        settings.TryGetValue(AppSettingKeys.WorkItemServerApiKey, out var apiKey);

        return new WorkItemServerOptions
        {
            BaseUrl = url,
            ApiKey = apiKey ?? string.Empty,
        };
    }

    public Task<WorkItemServerOptions> ResolveForWorkItemAsync(string workItemId, CancellationToken ct = default)
        => ResolveForRepositoryAsync(null, ct);
}
