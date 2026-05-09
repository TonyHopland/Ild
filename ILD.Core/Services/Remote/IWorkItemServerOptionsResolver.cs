using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ILD.Core.Services.Remote;

/// <summary>
/// Resolves the <see cref="WorkItemServerOptions"/> (URL + API key) that
/// should be used when talking to the remote WorkItemServer for a given
/// repository / work item. Per-call resolution because a single ILD instance
/// can in principle talk to multiple remote providers (one per repo).
/// </summary>
public interface IWorkItemServerOptionsResolver
{
    Task<WorkItemServerOptions> ResolveForRepositoryAsync(Guid? repositoryId, CancellationToken ct = default);
    Task<WorkItemServerOptions> ResolveForWorkItemAsync(Guid workItemId, CancellationToken ct = default);
}

/// <summary>
/// Production resolver that looks up the <see cref="RemoteProvider"/>
/// associated with the repository (or the work item's repository) and
/// extracts <c>WorkItemServerUrl</c> + <c>WorkItemApiKey</c>.
/// Throws when no provider is configured — per the PRD's hard-break
/// contract, ILD requires a remote WorkItemServer.
/// </summary>
public sealed class DbWorkItemServerOptionsResolver : IWorkItemServerOptionsResolver
{
    private readonly AppDbContext _db;

    public DbWorkItemServerOptionsResolver(AppDbContext db) => _db = db;

    public async Task<WorkItemServerOptions> ResolveForRepositoryAsync(Guid? repositoryId, CancellationToken ct = default)
    {
        RemoteProvider? provider = null;
        if (repositoryId.HasValue && repositoryId.Value != Guid.Empty)
        {
            var repo = await _db.Repositories
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == repositoryId.Value, ct);
            if (repo != null)
            {
                provider = await _db.RemoteProviders
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == repo.RemoteProviderId, ct);
            }
        }

        provider ??= await _db.RemoteProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(p => !string.IsNullOrEmpty(p.WorkItemServerUrl), ct);

        if (provider == null || string.IsNullOrEmpty(provider.WorkItemServerUrl))
            throw new InvalidOperationException(
                "No RemoteProvider with a WorkItemServerUrl is configured. " +
                "ILD requires a remote WorkItemServer for work-item operations.");

        return new WorkItemServerOptions
        {
            BaseUrl = provider.WorkItemServerUrl!,
            ApiKey = provider.WorkItemApiKey ?? string.Empty,
        };
    }

    public async Task<WorkItemServerOptions> ResolveForWorkItemAsync(Guid workItemId, CancellationToken ct = default)
    {
        var repoId = await _db.LoopRuns
            .AsNoTracking()
            .Where(r => r.WorkItemId == workItemId)
            .Select(r => (Guid?)r.RepositoryId)
            .FirstOrDefaultAsync(ct);
        return await ResolveForRepositoryAsync(repoId, ct);
    }
}
