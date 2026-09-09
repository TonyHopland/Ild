using System.Text;
using ILD.Data.DTOs;

namespace ILD.Core.Services.Attachments;

/// <summary>
/// A file arriving from a client. Kept independent of ASP.NET's
/// <c>IFormFile</c> so the storage rules live in ILD.Core with everything else
/// that has to reason about the agent uid, and the controllers only adapt.
/// </summary>
public sealed record UploadedFile(string FileName, string? ContentType, long Length, Stream Content);

/// <summary>Raised when a client sends more, or larger, files than are accepted.</summary>
public sealed class AttachmentRejectedException : Exception
{
    public AttachmentRejectedException(string message) : base(message) { }
}

/// <summary>
/// Turns uploaded files into files on disk that the coding agent can open by
/// absolute path. Owns the three rules that make that safe and useful: the name
/// a client sends is never trusted as a path, an upload has a size ceiling, and
/// the bytes land somewhere the agent's uid can read.
///
/// <para>
/// That last one is why nothing here chmods: under ADR-0014 the orchestrator and
/// the agent are different uids sharing the <c>ild-agents</c> group, and the
/// trees attachments land in (a chat session's scratch directory, the shared
/// agent scratch root) are setgid with a default ACL, so a file created normally
/// under the container's <c>umask 002</c> is already group-readable. Creating
/// these files with a tightened mode — or writing them anywhere outside those
/// trees — is what would silently break the feature at runtime.
/// </para>
/// </summary>
public static class AttachmentIntake
{
    /// <summary>
    /// Per-file ceiling. Generous enough for a screenshot, a PDF or a log dump,
    /// small enough that a run's worth of them stays trivial next to a worktree.
    /// </summary>
    public const long MaxBytesPerFile = 25L * 1024 * 1024;

    /// <summary>How many files one request may carry.</summary>
    public const int MaxFilesPerRequest = 10;

    /// <summary>Longest stored file name, extension included.</summary>
    private const int MaxFileNameLength = 120;

    private const string FallbackFileName = "attachment";

    /// <summary>
    /// The stored form of a client-supplied file name: one path segment, no
    /// directory separators, no traversal, no control characters. Everything
    /// outside a conservative set is replaced rather than dropped so two
    /// different uploads cannot collapse onto the same name by accident.
    /// </summary>
    public static string SanitizeFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return FallbackFileName;

        // Both separators, whatever this OS thinks: a Windows client sends
        // backslashes and Path.GetFileName would keep them on Linux.
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
        // "." and ".." survive the filter above and are directory references, not
        // names; so is a name that was only separators.
        if (cleaned.Length == 0 || cleaned.Trim('.').Length == 0) return FallbackFileName;

        return cleaned.Length <= MaxFileNameLength ? cleaned : Truncate(cleaned);
    }

    private static string Truncate(string name)
    {
        var ext = Path.GetExtension(name);
        if (ext.Length >= MaxFileNameLength) return name[..MaxFileNameLength];
        return string.Concat(name.AsSpan(0, MaxFileNameLength - ext.Length), ext);
    }

    /// <summary>
    /// A name not already taken in <paramref name="directory"/>, derived from
    /// <paramref name="fileName"/> by suffixing the stem. Callers materializing
    /// files the human named share this so a second <c>screenshot.png</c> does
    /// not overwrite the first.
    /// </summary>
    public static string UniqueFileNameIn(string directory, string fileName)
    {
        if (!File.Exists(Path.Combine(directory, fileName))) return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var n = 2; n < 1000; n++)
        {
            var candidate = $"{stem}-{n}{ext}";
            if (!File.Exists(Path.Combine(directory, candidate))) return candidate;
        }
        return $"{stem}-{Guid.NewGuid():N}{ext}";
    }

    /// <summary>
    /// Write <paramref name="files"/> into <paramref name="directory"/>, creating
    /// it if needed, and return what the caller should persist beside the thing
    /// they were attached to. Throws <see cref="AttachmentRejectedException"/>
    /// when a file is too large or there are too many of them — before anything
    /// is written, so a rejected request leaves no partial upload behind.
    /// </summary>
    public static async Task<IReadOnlyList<AttachmentRef>> SaveAsync(
        string directory, IReadOnlyList<UploadedFile> files, CancellationToken ct = default)
    {
        if (files.Count == 0) return Array.Empty<AttachmentRef>();
        if (files.Count > MaxFilesPerRequest)
            throw new AttachmentRejectedException(
                $"At most {MaxFilesPerRequest} files can be attached at once.");

        foreach (var file in files)
        {
            if (file.Length > MaxBytesPerFile)
                throw new AttachmentRejectedException(
                    $"'{SanitizeFileName(file.FileName)}' is larger than the {MaxBytesPerFile / (1024 * 1024)} MB limit.");
        }

        Directory.CreateDirectory(directory);
        var root = Path.GetFullPath(directory);

        var saved = new List<AttachmentRef>(files.Count);
        foreach (var file in files)
        {
            var fileName = UniqueFileNameIn(root, SanitizeFileName(file.FileName));
            var path = Path.GetFullPath(Path.Combine(root, fileName));

            // Belt and braces over SanitizeFileName: the invariant that matters is
            // that a stored path never escapes the directory it was destined for,
            // and that is worth asserting on the resolved path rather than
            // inferring it from the filter above.
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new AttachmentRejectedException($"'{file.FileName}' is not a valid file name.");

            await using (var target = File.Create(path))
            {
                await file.Content.CopyToAsync(target, ct);
            }

            var written = new FileInfo(path).Length;
            saved.Add(new AttachmentRef(
                Guid.NewGuid().ToString("N"),
                fileName,
                path,
                NormalizeContentType(file.ContentType),
                written));
        }

        return saved;
    }

    private static string? NormalizeContentType(string? contentType)
        => string.IsNullOrWhiteSpace(contentType) ? null : contentType.Trim();
}
