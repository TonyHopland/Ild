using System.Text.Json;
using ILD.Core.Services.Remote;
using ILD.Data.Backup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Backup;

/// <summary>What one database says about itself, or why it says nothing.</summary>
public sealed record DatabaseState(bool Available, string? Reason, ArchiveInfo? Info);

/// <summary>
/// What ILD's two databases are: their size, their migrations, and whether a backup can be taken at all.
/// Asking costs a PostgreSQL connection and a call to the WorkItem Server, and the Storage page asks
/// twice at once (the disk usage and the backup status), so callers that arrive while a lookup is in
/// progress share it. A lookup that has finished is never reused: what a restore decides on is fresh.
/// </summary>
public sealed class DatabaseCatalog(IServiceScopeFactory scopes, ILogger<DatabaseCatalog> log)
{
    private readonly object _gate = new();
    private Task<IReadOnlyDictionary<string, DatabaseState>>? _inFlight;

    /// <summary>Both databases, looked up side by side. Shared with any lookup already in progress.</summary>
    public Task<IReadOnlyDictionary<string, DatabaseState>> DescribeAllAsync(CancellationToken ct)
    {
        Task<IReadOnlyDictionary<string, DatabaseState>> lookup;
        lock (_gate) lookup = _inFlight ??= LookUpAllAsync();
        return lookup.WaitAsync(ct);
    }

    /// <summary>One database, looked up now.</summary>
    public Task<DatabaseState> DescribeAsync(string database, CancellationToken ct)
        => database == ArchiveDatabase.Core ? DescribeCoreAsync(ct) : DescribeWorkItemsAsync(ct);

    private async Task<IReadOnlyDictionary<string, DatabaseState>> LookUpAllAsync()
    {
        // Off the caller's thread first: the lookup must be recorded as in flight before it can finish.
        await Task.Yield();
        try
        {
            var core = DescribeCoreAsync(CancellationToken.None);
            var workItems = DescribeWorkItemsAsync(CancellationToken.None);
            return new Dictionary<string, DatabaseState>
            {
                [ArchiveDatabase.Core] = await core,
                [ArchiveDatabase.WorkItems] = await workItems,
            };
        }
        finally
        {
            lock (_gate) _inFlight = null;
        }
    }

    private async Task<DatabaseState> DescribeCoreAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<IDatabaseArchive>() is not { } archive)
            return new DatabaseState(false, "Backing up ILD's own data needs PostgreSQL, and this instance uses another database.", null);
        try
        {
            return new DatabaseState(true, null, await archive.DescribeAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not read the ILD database for a backup");
            return new DatabaseState(false, $"The ILD database could not be read: {ex.Message}", null);
        }
    }

    private async Task<DatabaseState> DescribeWorkItemsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        WorkItemServerOptions options;
        try
        {
            options = await services.GetRequiredService<IWorkItemServerOptionsResolver>().ResolveForRepositoryAsync(null, ct);
        }
        catch (InvalidOperationException ex)
        {
            return new DatabaseState(false, ex.Message, null);
        }

        try
        {
            return await services.GetRequiredService<IWorkItemServerBackupClient>().GetInfoAsync(options, ct) is { } info
                ? new DatabaseState(true, null, info)
                : new DatabaseState(false, "This tracker does not support backup.", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            // NotSupported and Json: it answered, with something that is not backup info.
            return new DatabaseState(false, $"The WorkItem Server could not be asked about backup: {ex.Message}", null);
        }
    }
}
