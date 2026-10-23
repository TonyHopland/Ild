namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Settings &gt; Storage: how much disk ILD's data takes. Sizes are allocated bytes, each file
/// counted once; null means not measured (the platform cannot report it, or the
/// path is not ILD's alone), never an estimate. No server path is ever returned.
/// </summary>
public interface IStorageService
{
    Task<StorageOverview> GetOverviewAsync(CancellationToken cancellationToken);
}

public sealed record StorageOverview(IReadOnlyList<StorageCategory> Categories);

public sealed record StorageCategory(string Key, string Label, long? Bytes, bool Incomplete, IReadOnlyList<StorageEntry> Entries);

public sealed record StorageEntry(string Key, string Label, Guid? RepositoryId, string? RepositoryName, long? Bytes, bool Incomplete);
