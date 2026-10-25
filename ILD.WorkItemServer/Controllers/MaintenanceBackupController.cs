using ILD.Data.Backup;
using ILD.WorkItemServer.Domain;
using ILD.WorkItemServer.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ILD.WorkItemServer.Controllers;

/// <summary>
/// Backup and restore of this server's database for an ILD instance. Outside the work item
/// contract (ADR-0029): a tracker that is not this server has no such endpoints, and ILD
/// then reports the backup as unavailable. The archive crosses unencrypted over the API-key
/// link, which already reads every work item; ILD encrypts it for the person.
/// </summary>
[ApiController]
[Route("maintenance/backup")]
public sealed class MaintenanceBackupController(
    IDatabaseArchive archive,
    WorkItemServerDbContext db,
    ImportGate gate,
    TimeProvider clock,
    ILogger<MaintenanceBackupController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Info(CancellationToken ct)
    {
        var info = await archive.DescribeAsync(ct);
        return Ok(new { info.Version, info.Migrations, info.DatabaseBytes });
    }

    [HttpPost("export")]
    public async Task Export(CancellationToken ct)
    {
        logger.LogInformation("Work item backup exported by {Username}", Actor());
        Response.ContentType = "application/octet-stream";
        await archive.ExportAsync(Response.Body, null, ct);
    }

    /// <summary>
    /// The archive arrives as the raw body, unbounded: Kestrel's default cap of about 30 MB would
    /// refuse a backup holding attachments.
    /// </summary>
    [HttpPost("import")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> Import(CancellationToken ct)
    {
        using var held = await gate.EnterImportAsync(ct);
        try
        {
            await archive.ImportAsync(Request.Body, ct);
        }
        catch (BackupRefusedException ex)
        {
            logger.LogWarning("Work item import by {Username} refused: {Reason}", Actor(), ex.Message);
            return UnprocessableEntity(new { error = ex.Message });
        }

        // The data is replaced, and what follows is not abandoned if the caller hangs up: the import was
        // the part that could be rolled back.
        await db.Database.MigrateAsync(CancellationToken.None);

        // Heartbeats in the archive date from when it was made. Left alone, the reclaimer
        // would hand every claimed item back to Ready as soon as the gate opens, which is
        // before the ILD instance has had its chance to heartbeat them again.
        var now = clock.GetUtcNow().UtcDateTime;
        await db.WorkItems
            .Where(w => (w.Status == WorkItemStatus.Running || w.Status == WorkItemStatus.WaitingForIld) && w.LastHeartbeatAt != null)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.LastHeartbeatAt, now), CancellationToken.None);

        logger.LogInformation("Work item data imported by {Username}", Actor());
        return Ok();
    }

    private string Actor() => db.HistoryActor?.Username ?? "unknown";
}
