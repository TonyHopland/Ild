using System.Net.Http.Headers;

namespace ILD.WorkItemServer.Attachments;

/// <summary>
/// The content type of an attachment comes from whoever uploaded it and is
/// echoed on every download, so anything that is not a media type is turned into
/// one here — on the way in, and again on the way out for rows stored before
/// this existed.
/// </summary>
public static class AttachmentContentType
{
    public const string Fallback = "application/octet-stream";

    /// <summary>
    /// The stored form of a supplied content type: the bare media type, lower
    /// cased and without parameters, or <see cref="Fallback"/> for anything
    /// missing, blank or unparseable.
    /// </summary>
    public static string Normalize(string? supplied)
        => MediaTypeHeaderValue.TryParse(supplied, out var parsed) && !string.IsNullOrWhiteSpace(parsed.MediaType)
            ? parsed.MediaType.ToLowerInvariant()
            : Fallback;
}
