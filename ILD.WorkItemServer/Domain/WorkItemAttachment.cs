namespace ILD.WorkItemServer.Domain;

/// <summary>
/// One file attached to a work item. The bytes live on the server's data volume
/// under a path derived from <see cref="Id"/> alone — never from
/// <see cref="FileName"/> — so a client-supplied name can never steer a read or
/// a write. The name is display metadata: what the human called it, and what
/// the file is called again once an ILD instance materializes it for a run.
/// </summary>
/// <param name="Id">Server-assigned identity; also the on-disk file name.</param>
/// <param name="FileName">Sanitized original name, shown to humans and agents.</param>
/// <param name="ContentType">Reported MIME type, or null when the client sent none.</param>
/// <param name="SizeBytes">Size of the stored file.</param>
/// <param name="CreatedAt">When it was attached.</param>
public sealed record WorkItemAttachment(
    string Id,
    string FileName,
    string? ContentType,
    long SizeBytes,
    DateTime CreatedAt);
