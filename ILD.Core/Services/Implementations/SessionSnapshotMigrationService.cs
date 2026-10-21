using ILD.Data.Entities;
using ILD.Data.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Converts every session snapshot still holding its transcript in the legacy column
/// into chunks (see <see cref="SessionSnapshotContentMigrator"/>), once per start, in the
/// background so startup does not wait for it. Rows go one at a time, each in its own
/// scope; one that fails is logged and left for the next start while the rest go on.
/// Legacy (remove with migration B): this whole service and its registration.
/// </summary>
public sealed class SessionSnapshotMigrationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SessionSnapshotMigrationService> _log;

    public SessionSnapshotMigrationService(IServiceScopeFactory scopes, ILogger<SessionSnapshotMigrationService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failed = new HashSet<Guid>();
        var converted = 0;
        try
        {
            while (await NextAsync(failed, stoppingToken) is { } snapshotId)
            {
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    if (!await SessionSnapshotContentMigrator.ConvertRowAsync(db, snapshotId, stoppingToken)) continue;
                    converted++;
                    _log.LogInformation("Converted session snapshot {SnapshotId} to chunks ({Converted} so far)", snapshotId, converted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    failed.Add(snapshotId);
                    _log.LogWarning(ex, "Could not convert session snapshot {SnapshotId}; it is left for the next start: {Reason}", snapshotId, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _log.LogInformation("Session snapshot conversion stopped with the host after {Converted} snapshot(s); the rest convert on the next start", converted);
            return;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Session snapshot conversion could not look for snapshots to convert; it runs again on the next start");
            return;
        }

        if (failed.Count == 0)
            _log.LogInformation("Session snapshot conversion finished: {Converted} snapshot(s) converted, none left", converted);
        else
            _log.LogWarning("Session snapshot conversion finished with {Failed} snapshot(s) left unconverted for the next start ({Converted} converted)", failed.Count, converted);
    }

    private async Task<Guid?> NextAsync(IReadOnlyCollection<Guid> skip, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        return await SessionSnapshotContentMigrator.FindNextAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), skip, ct);
    }
}
