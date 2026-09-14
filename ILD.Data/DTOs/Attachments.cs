namespace ILD.Data.DTOs;

/// <summary>
/// One attached file as it sits on disk for an agent to open.
/// <see cref="StoredPath"/> is the absolute path the agent is told to open —
/// attachments reach the model as paths in the rendered prompt, never as inline
/// content, because adapters are transport and pass
/// <c>AgentExecutionContext.Prompt</c> through unchanged (ADR-0007).
///
/// <para>
/// This is the <em>materialized</em> view, not the durable one. A work item's
/// attachments are downloaded to a run directory; a chat's are written out of
/// the database for the length of one turn. Neither store is this record.
/// </para>
/// </summary>
public sealed record AttachmentRef(
    string Id,
    string FileName,
    string StoredPath,
    string? ContentType,
    long SizeBytes)
{
    public AttachmentView ToView() => new(Id, FileName, ContentType, SizeBytes);
}

/// <summary>
/// What a client is told about an attachment. Deliberately without the stored
/// path: where ILD keeps the bytes is not a browser's business, and the download
/// endpoints address an attachment by id.
/// </summary>
public sealed record AttachmentView(
    string Id,
    string FileName,
    string? ContentType,
    long SizeBytes);
