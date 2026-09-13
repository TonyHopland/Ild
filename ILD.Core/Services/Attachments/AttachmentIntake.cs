using System.Text;
using ILD.Core.Services.Implementations;
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

    /// <summary>
    /// How much a request may weigh beyond its files. Multipart framing (the
    /// boundary lines and each part's headers) rides along with the bytes, and a
    /// chat turn also carries its message and the open Loop Editor's document, so
    /// the transport ceilings have to sit <em>above</em> the per-file one. Without
    /// the headroom a file of exactly <see cref="MaxBytesPerFile"/> — one the UI
    /// accepts and the size check below calls legal — is cut off by ASP.NET's
    /// generic "Request body too large" before any of our own checks run.
    /// </summary>
    public const long MultipartOverheadAllowance = 1L * 1024 * 1024;

    /// <summary>
    /// The most a multi-file upload request may weigh. Endpoints declare this as
    /// both the request-size and the multipart-body limit: ASP.NET's defaults for
    /// the two differ, and a request refused by the smaller of them fails
    /// somewhere other than the check that names the offending file.
    /// </summary>
    public const long MaxRequestBytes = MaxBytesPerFile * MaxFilesPerRequest + MultipartOverheadAllowance;

    /// <summary>The same ceiling for an endpoint that takes exactly one file.</summary>
    public const long MaxSingleFileRequestBytes = MaxBytesPerFile + MultipartOverheadAllowance;

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
    /// The names a file is offered, in order: the one the human gave it, then the
    /// stem suffixed, so a second <c>screenshot.png</c> does not land on the
    /// first. Which one it actually takes is decided by the create below, not by
    /// looking first.
    /// </summary>
    private static IEnumerable<string> NameCandidates(string fileName)
    {
        yield return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var n = 2; n < 1000; n++) yield return $"{stem}-{n}{ext}";
        yield return $"{stem}-{Guid.NewGuid():N}{ext}";
    }

    /// <summary>
    /// Take the first free name and write the upload to it, returning where it
    /// landed.
    ///
    /// <para>
    /// The create is <see cref="FileMode.CreateNew"/> — <c>O_CREAT|O_EXCL</c> —
    /// which is doing two jobs. It reserves the name <em>atomically</em>, so two
    /// uploads racing on one name cannot both believe they have it and write the
    /// same path. And it refuses to open a path that already exists rather than
    /// following it, so a symlink the agent planted in this directory cannot turn
    /// an upload into an overwrite of a file the orchestrator can reach. Checking
    /// with <c>File.Exists</c> first and then creating would lose both properties.
    /// </para>
    /// </summary>
    private static async Task<string> WriteToFreeNameAsync(
        string root, string fileName, Stream content, CancellationToken ct)
    {
        foreach (var candidate in NameCandidates(fileName))
        {
            var path = Path.GetFullPath(Path.Combine(root, candidate));

            // Belt and braces over SanitizeFileName: the invariant that matters is
            // that a stored path never escapes the directory it was destined for,
            // and that is worth asserting on the resolved path rather than
            // inferring it from the filter above.
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new AttachmentRejectedException($"'{fileName}' is not a valid file name.");

            FileStream target;
            try
            {
                target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException)
            {
                continue;   // taken, or a planted link: try the next name
            }

            try
            {
                await using (target)
                {
                    await content.CopyToAsync(target, ct);
                }
            }
            catch
            {
                // Nothing references this path yet, so a half-written upload would
                // just sit on disk forever.
                TryDelete(path);
                throw;
            }

            // The agent reads these files but must never be able to rewrite one:
            // writing an existing file is governed by that file's own mode, not by
            // the directory's (ADR-0014).
            AgentIsolation.ProtectFromAgentWrites(path);
            return path;
        }

        throw new AttachmentRejectedException($"Could not find a free name for '{fileName}'.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Write <paramref name="files"/> into <paramref name="directory"/>, creating
    /// it if needed, and return what the caller should persist beside the thing
    /// they were attached to. Throws <see cref="AttachmentRejectedException"/>
    /// when a file is too large or there are too many of them — before anything
    /// is written. Either every file lands or none does: a failure part-way
    /// through removes what it already wrote, so no request leaves bytes on disk
    /// that nothing references.
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

        var root = PrepareDirectory(directory);

        var saved = new List<AttachmentRef>(files.Count);
        try
        {
            foreach (var file in files)
            {
                var path = await WriteToFreeNameAsync(root, SanitizeFileName(file.FileName), file.Content, ct);
                saved.Add(new AttachmentRef(
                    Guid.NewGuid().ToString("N"),
                    Path.GetFileName(path),
                    path,
                    NormalizeContentType(file.ContentType),
                    new FileInfo(path).Length));
            }
        }
        catch
        {
            // All of them or none: the caller only ever receives the whole list,
            // so a file written before the failure is referenced by nothing, and
            // retrying would leave a suffixed duplicate beside it.
            foreach (var written in saved) TryDelete(written.StoredPath);
            throw;
        }

        return saved;
    }

    /// <summary>
    /// Create the upload directory and close it to the agent.
    ///
    /// <para>
    /// These files live inside a tree the agent can reach — that is the whole
    /// point, it has to read them — but it must never be able to <em>replace</em>
    /// an entry here. The orchestrator writes this directory and serves it back
    /// to the attachment's owner, so an agent that could swap a file for a
    /// symlink would be choosing what the orchestrator overwrites and what it
    /// hands out. Stripping group write is what makes that impossible; the
    /// <c>O_EXCL</c> create is the other half, covering the window before a
    /// directory has been protected. See ADR-0014.
    /// </para>
    /// </summary>
    private static string PrepareDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        var root = Path.GetFullPath(directory);
        AgentIsolation.ProtectFromAgentWrites(root);
        return root;
    }

    private static string? NormalizeContentType(string? contentType)
        => string.IsNullOrWhiteSpace(contentType) ? null : contentType.Trim();
}
