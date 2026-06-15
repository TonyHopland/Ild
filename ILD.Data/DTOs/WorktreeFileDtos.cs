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
/// </summary>
public sealed class WorktreeFileContentResponse
{
    public string Path { get; set; } = string.Empty;
    public string ChangeStatus { get; set; } = "none";
    public string? Content { get; set; }
    public string? Diff { get; set; }
    public bool IsBinary { get; set; }
}
