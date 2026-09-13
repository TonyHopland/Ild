namespace ILD.WorkItemServer.Services;

/// <summary>
/// An upload exceeded <see cref="WorkItemAttachmentStore.MaxBytesPerFile"/>.
/// Raised by the store while streaming, so the ceiling holds even for a caller
/// that did not check a declared length first; reported to clients as a 400,
/// since it is the request that is wrong.
/// </summary>
public sealed class AttachmentTooLargeException : Exception
{
    public AttachmentTooLargeException(long maxBytes)
        : base($"Files must be {maxBytes / (1024 * 1024)} MB or smaller.")
    {
    }
}
