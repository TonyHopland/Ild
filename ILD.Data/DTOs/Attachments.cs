namespace ILD.Data.DTOs;

/// <summary>
/// One file a human attached, as it is stored: the bytes live on disk and this
/// record lives in a JSON column beside the thing they were attached to (a
/// <c>ChatMessage</c>, a work item). <see cref="StoredPath"/> is the absolute
/// path the agent is told to open — attachments reach the model as paths in the
/// rendered prompt, never as inline content, because adapters are transport and
/// pass <c>AgentExecutionContext.Prompt</c> through unchanged (ADR-0007).
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
