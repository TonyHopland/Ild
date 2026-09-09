using System.Text;

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
    /// <summary>Per-file ceiling, matched by the ILD side's own limit.</summary>
    public const long MaxBytesPerFile = 25L * 1024 * 1024;

    /// <summary>
    /// What one upload request may weigh. Above <see cref="MaxBytesPerFile"/> by
    /// the multipart framing a file arrives wrapped in, so a file of exactly the
    /// per-file size reaches the check that names it rather than being cut off by
    /// ASP.NET's generic "Request body too large".
    /// </summary>
    public const long MaxRequestBytes = MaxBytesPerFile + 1L * 1024 * 1024;

    private const int MaxFileNameLength = 120;
    private const string FallbackFileName = "attachment";

    private readonly string _root;

    public WorkItemAttachmentStore(string dataPath)
        => _root = Path.Combine(dataPath, "attachments");

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

        await using (var target = File.Create(path))
        {
            await content.CopyToAsync(target, ct);
        }

        return new FileInfo(path).Length;
    }

    public Stream? Open(string workItemId, string attachmentId)
    {
        if (!IsSafeSegment(workItemId) || !IsSafeSegment(attachmentId)) return null;
        var path = PathFor(workItemId, attachmentId);
        return File.Exists(path) ? File.OpenRead(path) : null;
    }

    public void Delete(string workItemId, string attachmentId)
    {
        if (!IsSafeSegment(workItemId) || !IsSafeSegment(attachmentId)) return;
        try { File.Delete(PathFor(workItemId, attachmentId)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void DeleteAll(string workItemId)
    {
        if (!IsSafeSegment(workItemId)) return;
        try
        {
            var directory = DirectoryFor(workItemId);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
