using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations.PackageFeeds;

/// <summary>
/// The feeds a repository selected that still exist, with their PATs, and the
/// names of those that no longer do.
/// </summary>
/// <param name="CredentialProviderMissing">
/// True when there are feeds to hand out but NuGet in the run's processes will not
/// find the credential provider that reads them (see <see cref="NuGetCredentialProvider"/>).
/// </param>
public sealed record ResolvedPackageFeeds(
    IReadOnlyList<PackageFeedCredential> Feeds, IReadOnlyList<string> Missing, bool CredentialProviderMissing = false)
{
    public static readonly ResolvedPackageFeeds None = new([], []);
}

public interface IPackageFeedResolver
{
    /// <summary>
    /// What <paramref name="repositoryId"/> selected; nothing for no repository. A
    /// selected feed that has since been deleted is skipped, never an error, and
    /// logged as a warning.
    /// </summary>
    Task<ResolvedPackageFeeds> ResolveAsync(Guid? repositoryId, CancellationToken ct = default);
}

public sealed class PackageFeedResolver : IPackageFeedResolver
{
    private readonly IPackageFeedStore _store;
    private readonly ILogger<PackageFeedResolver> _logger;

    public PackageFeedResolver(IPackageFeedStore store, ILogger<PackageFeedResolver> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<ResolvedPackageFeeds> ResolveAsync(Guid? repositoryId, CancellationToken ct = default)
    {
        if (repositoryId is not { } id)
            return ResolvedPackageFeeds.None;

        var feeds = new List<PackageFeedCredential>();
        var missing = new List<string>();
        foreach (var selection in await _store.GetSelectionAsync(id, ct))
        {
            if (selection.Feed is null)
            {
                _logger.LogWarning(
                    "Repository {RepositoryId} selects package feed '{Feed}', which no longer exists; its processes run without it",
                    id, selection.Name);
                missing.Add(selection.Name);
            }
            else if (!AzureFeedUrl.TryParse(selection.Feed.FeedUrl, out var url, out var problem))
            {
                // Saving refuses such a URL, so only a row edited outside ILD gets here.
                _logger.LogWarning(
                    "Package feed '{Feed}' has an unusable URL and is skipped: {Problem}", selection.Feed.Name, problem);
                missing.Add(selection.Name);
            }
            else
            {
                feeds.Add(new PackageFeedCredential(selection.Feed.Name, url!, selection.Feed.Pat));
            }
        }
        var providerMissing = feeds.Count > 0 && NuGetCredentialProvider.IsMissing(ProcessEnvironment.Current);
        if (providerMissing)
            _logger.LogWarning("Repository {RepositoryId} selects package feeds, but {Problem}", id, NuGetCredentialProvider.MissingWarning);
        return new ResolvedPackageFeeds(feeds, missing, providerMissing);
    }
}
