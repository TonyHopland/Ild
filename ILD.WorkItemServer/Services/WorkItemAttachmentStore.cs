using System.Text;
using Microsoft.Extensions.Logging;

namespace ILD.WorkItemServer.Services;

/// <summary>
/// The bytes half of a work item's attachments: one directory per work item
/// under the server's data volume, one file per attachment named by its id.
///
/// <para>
/// Deriving the on-disk name from the server-assigned id rather than the
/// client's file name is the whole safety argument — no traversal, no
/// collisions, no reserved names — which leaves
/// <see cref="SanitizeFileName"/> responsible only for what humans and agents
/// see. That sanitizer is a near-copy of ILD.Core's; this server takes no
/// project references by design (ADR-0001), and duplicating a dozen lines of
/// pure string handling is the cheaper side of that boundary.
/// </para>
/// </summary>
public interface IWorkItemAttachmentStore
{
    /// <summary>Write <paramref name="content"/> as a new attachment and return its size.</summary>
    Task<long> SaveAsync(string workItemId, string attachmentId, Stream content, CancellationToken ct = default);

    /// <summary>Open an attachment for reading, or null when it is not on disk.</summary>
    Stream? Open(string workItemId, string attachmentId);

    /// <summary>Remove one attachment's bytes. Silent when it is already gone.</summary>
    void Delete(string workItemId, string attachmentId);

    /// <summary>Remove every attachment of a work item, for when the item itself goes.</summary>
    void DeleteAll(string workItemId);
}

public sealed class WorkItemAttachmentStore : IWorkItemAttachmentStore
{
    /// <summary>
    /// Per-file ceiling. This server takes no project references (ADR-0001), so
    /// it declares its own rather than sharing ILD's <c>AttachmentIntake</c> —
    /// one declaration per boundary, and this is the boundary's. It has to be
    /// enforced here and not only on the ILD leg, since an API key reaches this
    /// server directly.
    /// </summary>
    public const long MaxBytesPerFile = 25L * 1024 * 1024;

    /// <summary>
    /// Headroom for the multipart framing a file arrives wrapped in, so a file of
    /// exactly <see cref="MaxBytesPerFile"/> reaches the check that names it
    /// rather than being cut off by ASP.NET's generic "Request body too large".
    /// </summary>
    private const long MultipartOverheadAllowance = 1L * 1024 * 1024;

    /// <summary>What one upload request may weigh, framing included.</summary>
    public const long MaxRequestBytes = MaxBytesPerFile + MultipartOverheadAllowance;

    private const int MaxFileNameLength = 120;
    private const string FallbackFileName = "attachment";

    private readonly string _root;

    /// <summary>
    /// Absent in every construction but the server's own, so every use of it has
    /// to tolerate null: these are best-effort catch blocks whose contract is to
    /// stay silent, and a logging call that throws would invert exactly that.
    /// </summary>
    private readonly ILogger<WorkItemAttachmentStore>? _log;

    public WorkItemAttachmentStore(string dataPath, ILogger<WorkItemAttachmentStore>? log = null)
    {
        _root = Path.Combine(dataPath, "attachments");
        _log = log;
    }

    /// <summary>
    /// The display name an attachment is stored under: one path segment, no
    /// separators, no traversal, no control characters. It never reaches a
    /// filesystem call here, but it does become a real file name once an ILD
    /// instance materializes the attachment for a run.
    /// </summary>
    public static string SanitizeFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return FallbackFileName;

        var lastSeparator = raw.LastIndexOfAny(['/', '\\']);
        var name = lastSeparator >= 0 ? raw[(lastSeparator + 1)..] : raw;

        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or ' ' or '(' or ')' or '[' or ']')
                sb.Append(c);
            else
                sb.Append('_');
        }

        var cleaned = sb.ToString().Trim();
        if (cleaned.Length == 0 || cleaned.Trim('.').Length == 0) return FallbackFileName;
        if (cleaned.Length <= MaxFileNameLength) return cleaned;

        var ext = Path.GetExtension(cleaned);
        return ext.Length >= MaxFileNameLength
            ? cleaned[..MaxFileNameLength]
            : string.Concat(cleaned.AsSpan(0, MaxFileNameLength - ext.Length), ext);
    }

    public async Task<long> SaveAsync(string workItemId, string attachmentId, Stream content, CancellationToken ct = default)
    {
        var path = PathFor(workItemId, attachmentId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            await using (var target = File.Create(path))
            {
                await content.CopyToAsync(target, ct);
            }
        }
        catch
        {
            // Metadata is only written once this returns, so a half-written file
            // would be referenced by nothing and never cleaned up.
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }

        return new FileInfo(path).Length;
    }

    public Stream? Open(string workItemId, string attachmentId)
    {
        if (!IsSafeSegment(workItemId) || !IsSafeSegment(attachmentId)) return null;

        // Opened directly rather than checked first: a delete commits the metadata
        // removal before unlinking the bytes, so a download racing it would pass an
        // existence check and then throw — a 500 where the caller documents null.
        try
        {
            return File.OpenRead(PathFor(workItemId, attachmentId));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public void Delete(string workItemId, string attachmentId)
    {
        if (!IsSafeSegment(workItemId) || !IsSafeSegment(attachmentId)) return;
        try { File.Delete(PathFor(workItemId, attachmentId)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex,
                "Could not delete attachment {AttachmentId} of work item {WorkItemId}; its bytes are orphaned",
                attachmentId, workItemId);
        }
    }

    public void DeleteAll(string workItemId)
    {
        if (!IsSafeSegment(workItemId)) return;
        try
        {
            var directory = DirectoryFor(workItemId);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The work item row is already gone, so nothing will ever ask for
            // these again and one failure strands every attachment it had.
            // Swallowing it silently is what makes that undiagnosable.
            _log?.LogWarning(ex,
                "Could not remove the attachment directory for work item {WorkItemId}; its bytes are orphaned",
                workItemId);
        }
    }

    // Both ids are server-assigned, but they arrive back on the URL, so they are
    // checked rather than trusted: anything that is not a plain identifier
    // cannot address a file.
    private string PathFor(string workItemId, string attachmentId)
    {
        if (!IsSafeSegment(attachmentId))
            throw new ArgumentException($"'{attachmentId}' is not a valid attachment id.", nameof(attachmentId));
        return Path.Combine(DirectoryFor(workItemId), attachmentId);
    }

    private string DirectoryFor(string workItemId)
    {
        if (!IsSafeSegment(workItemId))
            throw new ArgumentException($"'{workItemId}' is not a valid work item id.", nameof(workItemId));
        return Path.Combine(_root, workItemId);
    }

    public static bool IsSafeSegment(string? value)
        => !string.IsNullOrEmpty(value)
           && value.Length <= 64
           && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
}
