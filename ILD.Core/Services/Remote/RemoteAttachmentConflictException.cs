namespace ILD.Core.Services.Remote;

/// <summary>
/// The WorkItem server refused an attachment change because other writers kept
/// winning the race for the item's list: nothing was stored and nothing was
/// lost, so the caller should retry rather than treat the item as missing.
///
/// <para>
/// Mirrors the server's own conflict signal without ILD.Core taking a project
/// reference on that assembly, the same way <see cref="RemoteWorkItemStatus"/>
/// mirrors its enum. Named apart from the server's type because the test
/// assembly sees both.
/// </para>
/// </summary>
public sealed class RemoteAttachmentConflictException : Exception
{
    public RemoteAttachmentConflictException(string workItemId)
        : base($"The attachments of work item '{workItemId}' were being written concurrently. Retry.")
    {
    }
}
