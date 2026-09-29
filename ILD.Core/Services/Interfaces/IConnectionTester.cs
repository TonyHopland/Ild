using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Asks a stored remote provider, repository or package feed whether ILD can
/// reach it with the credentials it has, and says why not. A failure to connect
/// is the answer, never an exception; only the caller cancelling escapes.
/// </summary>
public interface IConnectionTester
{
    Task<ConnectionTestResult> TestRemoteProviderAsync(RemoteProvider provider, CancellationToken ct);

    /// <param name="provider">
    /// The repository's remote provider, or null when it names none; the probe
    /// then runs without credentials, as fetch would.
    /// </param>
    Task<ConnectionTestResult> TestRepositoryAsync(Repository repo, RemoteProvider? provider, CancellationToken ct);

    /// <summary>
    /// One authenticated request for the feed's NuGet service index, read as whether
    /// its PAT can restore from it.
    /// </summary>
    Task<ConnectionTestResult> TestPackageFeedAsync(PackageFeed feed, CancellationToken ct);
}
