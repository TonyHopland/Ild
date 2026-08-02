namespace ILD.Data.DTOs;

/// <summary>
/// A single file in a worktree, with its change status relative to the
/// default branch's fork point. <see cref="ChangeStatus"/> is one of
/// <c>"none"</c>, <c>"added"</c>, <c>"modified"</c> or <c>"deleted"</c>.
/// </summary>
public sealed class WorktreeFileEntry
{
    public string Path { get; set; } = string.Empty;
    public string ChangeStatus { get; set; } = "none";
}

/// <summary>
/// The flat list of files in a worktree. Deleted files are included so the
/// PR-style diff view can surface them even though they no longer exist on disk.
/// </summary>
public sealed class WorktreeFilesResponse
{
    public string WorktreePath { get; set; } = string.Empty;
    public List<WorktreeFileEntry> Files { get; set; } = new();
}

/// <summary>
/// A single file's full content plus its unified diff against the default
/// branch's fork point. <see cref="Content"/> is null for binary or missing
/// (e.g. deleted) files; <see cref="Diff"/> is null when the file is unchanged.
/// <para>
/// A binary file the file viewer can render as an image is the one exception to
/// the withheld-content rule: it still reports <see cref="IsBinary"/> and a null
/// <see cref="Content"/>, but carries its bytes inline in
/// <see cref="ImageBase64"/> alongside the <see cref="ImageMimeType"/> to render
/// them under. The pair is set together or not at all, so a viewer only has to
/// test one of them. Every other binary — and an image past the inlining size
/// cap — leaves both null and keeps the content-less shape exactly as before.
/// </para>
/// </summary>
public sealed class WorktreeFileContentResponse
{
    public string Path { get; set; } = string.Empty;
    public string ChangeStatus { get; set; } = "none";
    public string? Content { get; set; }
    public string? Diff { get; set; }
    public bool IsBinary { get; set; }

    /// <summary>
    /// The image media type to render <see cref="ImageBase64"/> under (e.g.
    /// <c>image/png</c>), or null when this file is not an inlined image.
    /// </summary>
    public string? ImageMimeType { get; set; }

    /// <summary>
    /// The file's raw bytes, base64-encoded, when it is an image small enough to
    /// inline; null otherwise. Inlined rather than served from a bytes endpoint
    /// so the viewer needs no second, unauthenticated fetch path.
    /// </summary>
    public string? ImageBase64 { get; set; }
}
