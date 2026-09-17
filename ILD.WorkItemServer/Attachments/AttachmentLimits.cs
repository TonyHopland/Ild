namespace ILD.WorkItemServer.Attachments;

/// <summary>
/// How much a work item may carry in attachments. Read once at startup from the
/// environment, because the same two variables are set on the ILD container as
/// well: both processes enforce the limits independently, and anything holding
/// an API key can reach this server without an ILD instance in front of it.
///
/// The ILD side mirrors this class (<c>ILD.Core.Services.Attachments.AttachmentLimits</c>)
/// rather than sharing it — this server takes no project references (ADR-0001).
/// </summary>
public sealed class AttachmentLimits
{
    public const string MaxAttachmentMbVariable = "ILD_MAX_ATTACHMENT_MB";
    public const string MaxAttachmentsTotalMbVariable = "ILD_MAX_ATTACHMENTS_TOTAL_MB";

    /// <summary>A megabyte is 1024 × 1024 bytes here and in the documentation.</summary>
    public const long Megabyte = 1024 * 1024;

    public const int DefaultMaxAttachmentMb = 25;

    /// <summary>Ten files at the default per-file maximum.</summary>
    public const int DefaultMaxAttachmentsTotalMb = 250;

    /// <summary>
    /// Files one upload may carry. A constant rather than a setting: the number
    /// bounds how much a single request can buffer, and nothing asked for it to
    /// be tuned per deployment.
    /// </summary>
    public const int FilesPerRequest = 10;

    public long MaxBytesPerFile { get; init; } = DefaultMaxAttachmentMb * Megabyte;

    public long MaxTotalBytesPerWorkItem { get; init; } = DefaultMaxAttachmentsTotalMb * Megabyte;

    public int MaxFilesPerRequest { get; init; } = FilesPerRequest;

    /// <summary>
    /// The ceiling a whole multipart upload is allowed to reach: a full request
    /// of maximum-sized files, plus a megabyte for the part headers around them.
    /// Derived so raising the per-file maximum raises it too.
    /// </summary>
    public long MaxRequestBytes => MaxBytesPerFile * MaxFilesPerRequest + Megabyte;

    /// <summary>
    /// Reads both variables as whole megabytes, falling back to the defaults for
    /// anything malformed or non-positive — a deployment that fat-fingers the
    /// value gets the documented limit rather than one that refuses every file.
    /// </summary>
    public static AttachmentLimits FromEnvironment(Func<string, string?>? readVariable = null)
    {
        var read = readVariable ?? Environment.GetEnvironmentVariable;
        return new AttachmentLimits
        {
            MaxBytesPerFile = Megabytes(read(MaxAttachmentMbVariable), DefaultMaxAttachmentMb),
            MaxTotalBytesPerWorkItem = Megabytes(read(MaxAttachmentsTotalMbVariable), DefaultMaxAttachmentsTotalMb),
        };
    }

    private static long Megabytes(string? raw, int fallbackMb)
        => (int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var mb) && mb > 0 ? mb : fallbackMb) * Megabyte;
}
