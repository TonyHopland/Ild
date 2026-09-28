using ILD.Data.Entities;

namespace ILD.Data.Stores.Interfaces;

/// <summary>One feed a repository selected, and the feed itself unless it has been deleted since.</summary>
public sealed record PackageFeedSelection(string Name, PackageFeed? Feed);

/// <summary>
/// The instance's package feeds and the repositories' selections of them. Every
/// feed returned carries its decrypted PAT, so nothing read here may be handed to
/// a response as-is.
/// </summary>
public interface IPackageFeedStore
{
    /// <summary>Ordered by name.</summary>
    Task<IReadOnlyList<PackageFeed>> GetFeedsAsync(CancellationToken ct = default);

    Task<PackageFeed?> GetFeedAsync(Guid id, CancellationToken ct = default);

    Task AddFeedAsync(PackageFeed feed, CancellationToken ct = default);
    Task UpdateFeedAsync(PackageFeed feed, CancellationToken ct = default);
    Task<bool> DeleteFeedAsync(Guid id, CancellationToken ct = default);

    /// <summary>The repository's selected feeds, ordered by name; a deleted feed has a null <see cref="PackageFeedSelection.Feed"/>.</summary>
    Task<IReadOnlyList<PackageFeedSelection>> GetSelectionAsync(Guid repositoryId, CancellationToken ct = default);
}
