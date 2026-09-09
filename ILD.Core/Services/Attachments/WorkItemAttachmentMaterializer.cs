using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Attachments;

/// <summary>
/// A run's work-item attachments as files on disk: the directory holding them
/// and what to tell the agent about each. Empty when the item has none.
/// </summary>
public sealed record MaterializedAttachments(string? Directory, IReadOnlyList<AttachmentRef> Files)
{
    public static readonly MaterializedAttachments None = new(null, Array.Empty<AttachmentRef>());
}

/// <summary>
/// Brings a work item's attachments — held by the WorkItem server (ADR-0001) —
/// down to where the coding agent of a particular run can open them.
/// </summary>
public interface IWorkItemAttachmentMaterializer
{
    /// <summary>
    /// Ensure every attachment of <paramref name="workItem"/> exists on this
    /// machine for run <paramref name="runId"/>, and return where. Idempotent and
    /// safe to call from more than one place in the same run — a file already
    /// downloaded at its recorded size is left alone — so the prompt renderer and
    /// the AI node executor can each ask without paying for the fetch twice.
    /// A download that fails is skipped rather than fatal: an AI node losing one
    /// picture is better than the run dying.
    /// </summary>
    Task<MaterializedAttachments> EnsureLocalAsync(WorkItemView workItem, Guid runId, CancellationToken ct = default);
}

public sealed class WorkItemAttachmentMaterializer : IWorkItemAttachmentMaterializer
{
    private readonly IWorkItemManager _workItems;
    private readonly ILogger<WorkItemAttachmentMaterializer>? _log;

    public WorkItemAttachmentMaterializer(
        IWorkItemManager workItems, ILogger<WorkItemAttachmentMaterializer>? log = null)
    {
        _workItems = workItems;
        _log = log;
    }

    public async Task<MaterializedAttachments> EnsureLocalAsync(
        WorkItemView workItem, Guid runId, CancellationToken ct = default)
    {
        if (workItem.Attachments.Count == 0) return MaterializedAttachments.None;

        // The shared agent scratch root, not the worktree: these are inputs to the
        // run, not part of the change under review, and writing them into the
        // checkout would put them in front of `git add` (ADR-0008). The root is the
        // setgid tree ADR-0014 provisions for exactly this hand-off, so a file
        // written here under umask 002 is readable by the agent uid without any
        // mode fixing of our own. The AI node grants the directory to the agent as
        // an extra allowed directory, the same mechanism the chat uses for a
        // worktree it does not live in (ADR-0011).
        var directory = AgentIsolation.CreateScratchDirectory("workitem-attachments", runId.ToString("N"));

        var files = new List<AttachmentRef>(workItem.Attachments.Count);
        foreach (var attachment in workItem.Attachments)
        {
            // Keyed on the attachment id so a second file the human called
            // "screenshot.png" cannot land on the first, while the name the agent
            // is shown stays the one they chose.
            var path = Path.Combine(directory, attachment.Id, attachment.FileName);
            if (!IsAlreadyLocal(path, attachment.SizeBytes))
            {
                var content = await Download(workItem.Id, attachment.Id, ct);
                if (content is null) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, content.Bytes, ct);
            }

            files.Add(new AttachmentRef(
                attachment.Id, attachment.FileName, path, attachment.ContentType, attachment.SizeBytes));
        }

        return files.Count == 0 ? MaterializedAttachments.None : new MaterializedAttachments(directory, files);
    }

    private static bool IsAlreadyLocal(string path, long expectedSize)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length == expectedSize;
    }

    private async Task<RemoteAttachmentContent?> Download(string workItemId, string attachmentId, CancellationToken ct)
    {
        try
        {
            return await _workItems.GetAttachmentAsync(workItemId, attachmentId, ct);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex,
                "Could not fetch attachment {AttachmentId} of work item {WorkItemId}; the agent will not see it",
                attachmentId, workItemId);
            return null;
        }
    }
}
