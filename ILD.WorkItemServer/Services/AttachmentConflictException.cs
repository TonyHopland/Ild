namespace ILD.WorkItemServer.Services;

/// <summary>
/// Other writers kept winning the race for a work item's attachment list, so
/// this change was not applied and nothing was lost. Distinct from "no such work
/// item" because it is worth retrying — the same distinction
/// <see cref="Dtos.RecordPullRequestOutcome.Conflict"/> draws for pull requests,
/// and reported to clients the same way, as a 409.
/// </summary>
public sealed class AttachmentConflictException : Exception
{
    public AttachmentConflictException(string workItemId)
        : base($"The attachments of work item '{workItemId}' were being written concurrently. Retry.")
    {
    }
}
