using ILD.Api.Filters;
using ILD.Data.Backup;
using ILD.Core.Services.Backup;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace ILD.Api.Controllers;

/// <summary>
/// Settings &gt; Storage &gt; Backup and restore. No authorization attribute on purpose: the user-only
/// fallback policy applies, so the agent token is refused here as everywhere it has not been admitted.
/// Backup and restore are the one thing that exposes or replaces all of the data.
/// </summary>
[ApiController]
[Route("api/v1/storage/backups")]
public class BackupController(
    BackupService backups,
    BackupDirectory directory,
    IHostApplicationLifetime lifetime) : ControllerBase
{
    private const int MaxFieldBytes = 4096;

    public sealed record CreateBackupRequest(string? Passphrase);

    [HttpGet]
    public async Task<BackupStatus> GetStatus() => await backups.GetStatusAsync(HttpContext.RequestAborted);

    [HttpPost("{database}")]
    public async Task<IActionResult> Create(string database, [FromBody] CreateBackupRequest request)
    {
        if (!Known(database)) return NotFound();
        try
        {
            await backups.CreateAsync(database, request.Passphrase, Username, fileName =>
            {
                Response.ContentType = "application/octet-stream";
                Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = fileName }.ToString();
                return Response.Body;
            }, HttpContext.RequestAborted);
            return new EmptyResult();
        }
        catch (BackupException ex) when (!Response.HasStarted)
        {
            return Refusal(ex);
        }
        catch when (Response.HasStarted)
        {
            // The file has begun to download and cannot be taken back: cutting the connection is the
            // only way the browser learns it is incomplete, and a restore of it would refuse it anyway.
            HttpContext.Abort();
            throw;
        }
    }

    /// <summary>
    /// The backup arrives as multipart, file first, then passphrase and confirmation. The file is
    /// spooled to a private file as it streams in, since it cannot be read until the passphrase has
    /// arrived behind it; however the request ends, the spool is removed.
    /// </summary>
    [HttpPost("{database}/restore")]
    [DisableFormValueModelBinding]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> Restore(string database)
    {
        if (!Known(database)) return NotFound();
        var ct = HttpContext.RequestAborted;
        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(contentType.Boundary) is not { Length: > 0 } boundary)
            return BadRequest(new { error = "Send the backup as multipart/form-data." });

        await using var spool = directory.CreatePartial("upload");
        string? passphrase = null, confirmation = null;
        var hasFile = false;
        try
        {
            var reader = new MultipartReader(boundary.ToString(), Request.Body);
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)) continue;
                switch (HeaderUtilities.RemoveQuotes(disposition.Name).Value)
                {
                    case "file":
                        if (hasFile) throw new BackupException(BackupFailure.Invalid, "Send one backup file.");
                        await section.Body.CopyToAsync(spool.Stream, ct);
                        hasFile = true;
                        break;
                    case "passphrase":
                        passphrase = await ReadFieldAsync(section.Body, ct);
                        break;
                    case "confirmation":
                        confirmation = await ReadFieldAsync(section.Body, ct);
                        break;
                }
            }

            await backups.RestoreAsync(database, hasFile ? spool.Stream : null, passphrase, confirmation, Username, ct);
        }
        catch (BackupException ex)
        {
            return Refusal(ex);
        }
        catch (InvalidDataException)
        {
            return BadRequest(new { error = "The upload is not a well-formed multipart request." });
        }

        // ILD's own data was replaced: it starts again, which migrates an older backup forward and
        // runs the data migrators. The restart is after this response, which says so.
        var restarting = database == ArchiveDatabase.Core;
        if (restarting)
            Response.OnCompleted(() =>
            {
                lifetime.StopApplication();
                return Task.CompletedTask;
            });
        return Ok(new { restarting });
    }

    [HttpGet("{database}/pre-restore")]
    public IActionResult DownloadPreRestore(string database)
    {
        if (!Known(database)) return NotFound();
        var file = backups.OpenPreRestore(database, Username);
        return file is null
            ? NotFound(new { error = "No restore has stored a backup of this database yet." })
            : File(file, "application/octet-stream", $"ild-{database}-before-restore.ildbackup");
    }

    private string Username => User.Identity?.Name ?? throw new InvalidOperationException("A signed-in user has a name.");

    private static bool Known(string database) => BackupService.Databases.Contains(database);

    private ObjectResult Refusal(BackupException ex) => StatusCode(ex.Failure switch
    {
        BackupFailure.Invalid => StatusCodes.Status400BadRequest,
        BackupFailure.Unavailable or BackupFailure.Refused => StatusCodes.Status422UnprocessableEntity,
        BackupFailure.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError,
    }, new { error = ex.Message });

    /// <summary>A short text field, read whole; anything longer than a passphrase or a confirmation is not one.</summary>
    private static async Task<string> ReadFieldAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[512];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxFieldBytes)
                throw new BackupException(BackupFailure.Invalid, "A form field is too long.");
            buffer.Write(chunk, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
