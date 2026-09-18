namespace ILD.Core.Services.Attachments;

/// <summary>
/// How much a work item may carry in attachments, as this ILD instance reads it.
/// The WorkItem server reads the same two variables and enforces the same
/// numbers from its own copy of this class (<c>ILD.WorkItemServer.Attachments.AttachmentLimits</c>);
/// it takes no project references (ADR-0001), so the class is mirrored rather
/// than shared. Read once at startup — a limit that changes mid-process would
/// leave the multipart ceilings derived from it behind.
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

    /// <summary>
    /// The configured megabytes as bytes, never beyond what one attachment can be:
    /// a file is a <c>byte[]</c> from the request to the <c>bytea</c> column, so a
    /// maximum above <see cref="Array.MaxLength"/> would promise a size nothing on
    /// the path could hold.
    /// </summary>
    private static long Megabytes(string? raw, int fallbackMb)
        => Math.Min(
            (int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var mb) && mb > 0 ? mb : fallbackMb) * Megabyte,
            Array.MaxLength);
}
