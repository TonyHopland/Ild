using ILD.WorkItemServer.Attachments;
using ILD.WorkItemServer.Domain;
using ILD.WorkItemServer.Dtos;
using Microsoft.EntityFrameworkCore;

namespace ILD.WorkItemServer.Services;

/// <summary>
/// One file on its way in: what the request says about it, and a reader that
/// materialises the bytes. Every limit is answerable from
/// <paramref name="SizeBytes"/> alone, so a refused upload never allocates a byte
/// of what it was sent, and an accepted one allocates each file exactly once.
/// </summary>
public sealed record IncomingAttachment(
    string FileName,
    string? ContentType,
    long SizeBytes,
    Func<CancellationToken, Task<byte[]>> ReadAsync);

/// <summary>A stored file on its way out, with the content type it will be served as.</summary>
public sealed record StoredAttachment(byte[] Content, string ContentType, string FileName);

/// <summary>
/// What came of an upload. Distinguished rather than collapsed into a bool
/// because the caller has to answer differently: only <see cref="NotFound"/> is
/// a missing work item, and every refusal carries the limit it broke back to
/// whoever tried to break it.
/// </summary>
public enum AddAttachmentsOutcome
{
    Created = 0,
    NotFound = 1,
    NoFiles = 2,
    TooManyFiles = 3,
    FileTooLarge = 4,
    InvalidFileName = 5,
    TotalExceeded = 6,

    /// <summary>The file read is not the size the request said it was, so no limit answered for it.</summary>
    SizeMismatch = 7,
}

public sealed record AddAttachmentsResult(
    AddAttachmentsOutcome Outcome,
    string? Error,
    IReadOnlyList<WorkItemAttachmentDto> Created)
{
    public static AddAttachmentsResult Refused(AddAttachmentsOutcome outcome, string error)
        => new(outcome, error, Array.Empty<WorkItemAttachmentDto>());
}

public interface IWorkItemAttachmentService
{
    /// <summary>The item's attachments, metadata only. Null when there is no such work item.</summary>
    Task<IReadOnlyList<WorkItemAttachmentDto>?> ListAsync(string workItemId, CancellationToken ct = default);

    /// <summary>
    /// Store a whole request's files, or none of them: every limit is checked
    /// before anything is written, so a rejected request leaves nothing behind.
    /// </summary>
    Task<AddAttachmentsResult> AddAsync(string workItemId, IReadOnlyList<IncomingAttachment> files, CancellationToken ct = default);

    /// <summary>The bytes of one attachment of one work item. Null when either id is unknown.</summary>
    Task<StoredAttachment?> GetContentAsync(string workItemId, Guid attachmentId, CancellationToken ct = default);

    Task<bool> DeleteAsync(string workItemId, Guid attachmentId, CancellationToken ct = default);
}

public sealed class WorkItemAttachmentService : IWorkItemAttachmentService
{
    private readonly WorkItemServerDbContext _db;
    private readonly AttachmentLimits _limits;
    private readonly TimeProvider _clock;

    /// <summary>Longest file name the column holds; longer ones are refused rather than silently cut.</summary>
    private const int MaxFileNameLength = 255;

    /// <summary>Longest content type the column holds. No real media type comes close.</summary>
    private const int MaxContentTypeLength = 255;

    public WorkItemAttachmentService(WorkItemServerDbContext db, AttachmentLimits limits, TimeProvider clock)
    {
        _db = db;
        _limits = limits;
        _clock = clock;
    }

    public async Task<IReadOnlyList<WorkItemAttachmentDto>?> ListAsync(string workItemId, CancellationToken ct = default)
    {
        var key = await ResolveKeyAsync(workItemId, ct);
        if (key is null) return null;
        return (await AttachmentMetadata.ReadAsync(_db, new[] { key.Value }, ct))[key.Value].ToList();
    }

    public async Task<AddAttachmentsResult> AddAsync(string workItemId, IReadOnlyList<IncomingAttachment> files, CancellationToken ct = default)
    {
        var key = await ResolveKeyAsync(workItemId, ct);
        if (key is null)
            return AddAttachmentsResult.Refused(AddAttachmentsOutcome.NotFound, "No such work item.");

        if (files.Count == 0)
            return AddAttachmentsResult.Refused(AddAttachmentsOutcome.NoFiles, "The request carried no files.");

        if (files.Count > _limits.MaxFilesPerRequest)
            return AddAttachmentsResult.Refused(
                AddAttachmentsOutcome.TooManyFiles,
                $"One upload may carry at most {_limits.MaxFilesPerRequest} files; this one carried {files.Count}.");

        foreach (var file in files)
        {
            var name = FileNameOf(file.FileName);
            if (name.Length > MaxFileNameLength)
                return AddAttachmentsResult.Refused(
                    AddAttachmentsOutcome.InvalidFileName,
                    $"A file name may be at most {MaxFileNameLength} characters.");
            if (file.SizeBytes > _limits.MaxBytesPerFile)
                return AddAttachmentsResult.Refused(
                    AddAttachmentsOutcome.FileTooLarge,
                    $"'{name}' is larger than the {Megabytes(_limits.MaxBytesPerFile)} MB allowed per file.");
        }

        // The total is a read-then-write, so two uploads to the same work item
        // could each see room for themselves and together go over it. They are
        // serialised by claiming the owning row first: Postgres holds that row for
        // the transaction, SQLite takes its write lock, and the loser re-reads a
        // total that now includes the winner's files.
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        // Claiming nothing means the work item was deleted while this upload was
        // being read; its files would fail the foreign key a moment later.
        if (await ClaimWorkItemAsync(key.Value, ct) == 0)
            return AddAttachmentsResult.Refused(AddAttachmentsOutcome.NotFound, "No such work item.");

        var incoming = files.Sum(f => f.SizeBytes);
        var alreadyStored = await _db.WorkItemAttachments
            .Where(a => a.WorkItemId == key.Value)
            .SumAsync(a => (long?)a.SizeBytes, ct) ?? 0;
        if (alreadyStored + incoming > _limits.MaxTotalBytesPerWorkItem)
            return AddAttachmentsResult.Refused(
                AddAttachmentsOutcome.TotalExceeded,
                $"This work item's attachments would total more than the "
                + $"{Megabytes(_limits.MaxTotalBytesPerWorkItem)} MB allowed per work item.");

        var now = _clock.GetUtcNow().UtcDateTime;
        var rows = new List<WorkItemAttachment>(files.Count);
        foreach (var file in files)
        {
            var content = await file.ReadAsync(ct);

            // Every limit above answered for the size the request declared; what
            // was read is what would actually be stored. A reader that disagrees
            // with its own declaration has been measured by nothing, so it is
            // refused rather than written.
            if (content.LongLength > _limits.MaxBytesPerFile)
                return AddAttachmentsResult.Refused(
                    AddAttachmentsOutcome.FileTooLarge,
                    $"'{FileNameOf(file.FileName)}' is larger than the {Megabytes(_limits.MaxBytesPerFile)} MB allowed per file.");
            if (content.LongLength != file.SizeBytes)
                return AddAttachmentsResult.Refused(
                    AddAttachmentsOutcome.SizeMismatch,
                    $"'{FileNameOf(file.FileName)}' is {content.LongLength} bytes, "
                    + $"not the {file.SizeBytes} the request declared.");

            rows.Add(new WorkItemAttachment
            {
                Id = Guid.NewGuid(),
                WorkItemId = key.Value,
                FileName = FileNameOf(file.FileName),
                ContentType = StoredContentType(file.ContentType),
                Content = content,
                SizeBytes = content.LongLength,
                CreatedAt = now,
            });
        }

        _db.WorkItemAttachments.AddRange(rows);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new AddAttachmentsResult(
            AddAttachmentsOutcome.Created,
            null,
            rows.Select(r => new WorkItemAttachmentDto
            {
                Id = r.Id,
                FileName = r.FileName,
                ContentType = r.ContentType,
                SizeBytes = r.SizeBytes,
                CreatedAt = r.CreatedAt,
            }).ToList());
    }

    public async Task<StoredAttachment?> GetContentAsync(string workItemId, Guid attachmentId, CancellationToken ct = default)
    {
        var key = await ResolveKeyAsync(workItemId, ct);
        if (key is null) return null;

        // Projected and untracked: the file is served once, and a tracked entity
        // would leave the change tracker holding a second copy of every byte.
        var row = await _db.WorkItemAttachments
            .AsNoTracking()
            .Where(a => a.Id == attachmentId && a.WorkItemId == key.Value)
            .Select(a => new { a.Content, a.ContentType, a.FileName })
            .FirstOrDefaultAsync(ct);
        return row == null
            ? null
            // Normalised again on the way out: a row stored before this rule, or
            // written straight to the database, must not dictate the type ILD
            // serves it as.
            : new StoredAttachment(row.Content, AttachmentContentType.Normalize(row.ContentType), row.FileName);
    }

    public async Task<bool> DeleteAsync(string workItemId, Guid attachmentId, CancellationToken ct = default)
    {
        var key = await ResolveKeyAsync(workItemId, ct);
        if (key is null) return false;

        // Deleted by key, so the bytes are never read to throw them away.
        return await _db.WorkItemAttachments
            .Where(a => a.Id == attachmentId && a.WorkItemId == key.Value)
            .ExecuteDeleteAsync(ct) > 0;
    }

    /// <summary>
    /// Take the owning work item's row for the rest of the transaction, so the
    /// total check and the insert that follows it cannot interleave with another
    /// upload to the same item. Written as an update to the row's own value: it
    /// changes nothing, and it is the one lock both Postgres and SQLite take.
    /// </summary>
    private Task<int> ClaimWorkItemAsync(int workItemKey, CancellationToken ct)
        => _db.WorkItems
            .Where(w => w.InternalId == workItemKey)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.UpdatedAt, w => w.UpdatedAt), ct);

    private Task<int?> ResolveKeyAsync(string workItemId, CancellationToken ct)
        => _db.WorkItems
            .Where(w => w.Id == workItemId)
            .Select(w => (int?)w.InternalId)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The name as it is stored: the last segment only, since a caller is free
    /// to send a path and nothing here is a directory, and never blank.
    ///
    /// Both separators, not <see cref="Path.GetFileName(string?)"/>, which knows
    /// only this platform's: a browser hands over <c>C:\fakepath\photo.png</c>
    /// for a file input, and on Linux that would be stored whole, backslashes and
    /// all.
    /// </summary>
    private static string FileNameOf(string? supplied)
    {
        var trimmed = (supplied ?? string.Empty).Trim();
        var lastSeparator = trimmed.LastIndexOfAny(['/', '\\']);
        var name = lastSeparator < 0 ? trimmed : trimmed[(lastSeparator + 1)..];
        return string.IsNullOrWhiteSpace(name) ? "attachment" : name;
    }

    /// <summary>
    /// The normalised content type, falling back for one too long to store —
    /// which no real media type is.
    /// </summary>
    private static string StoredContentType(string? supplied)
    {
        var normalized = AttachmentContentType.Normalize(supplied);
        return normalized.Length > MaxContentTypeLength ? AttachmentContentType.Fallback : normalized;
    }

    private static long Megabytes(long bytes) => bytes / AttachmentLimits.Megabyte;
}

/// <summary>
/// The one read of attachment metadata: projected so the bytes are never
/// selected, and shared by the attachment routes and the work-item reads that
/// carry the same list.
/// </summary>
internal static class AttachmentMetadata
{
    public static async Task<ILookup<int, WorkItemAttachmentDto>> ReadAsync(
        WorkItemServerDbContext db, IReadOnlyList<int> workItemKeys, CancellationToken ct)
    {
        if (workItemKeys.Count == 0)
            return Array.Empty<(int Key, WorkItemAttachmentDto Dto)>().ToLookup(r => r.Key, r => r.Dto);

        var rows = await db.WorkItemAttachments
            .Where(a => workItemKeys.Contains(a.WorkItemId))
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.FileName)
            .Select(a => new { a.WorkItemId, a.Id, a.FileName, a.ContentType, a.SizeBytes, a.CreatedAt })
            .ToListAsync(ct);

        return rows.ToLookup(r => r.WorkItemId, r => new WorkItemAttachmentDto
        {
            Id = r.Id,
            FileName = r.FileName,
            ContentType = AttachmentContentType.Normalize(r.ContentType),
            SizeBytes = r.SizeBytes,
            CreatedAt = r.CreatedAt,
        });
    }
}
