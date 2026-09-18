using System.Buffers.Text;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace ILD.McpServer.Attachments;

/// <summary>
/// Hands a stored file to the model in the shape it can actually use: an image
/// it can look at, text it can read, and anything else as a resource it can
/// carry without misreading. Knows nothing about work items, so chat
/// attachments shape their files through the same call.
/// </summary>
public static class AttachmentContent
{
    /// <param name="uri">
    /// Identifies the file to the client for a binary resource — the only block
    /// shape that carries one.
    /// </param>
    public static IEnumerable<ContentBlock> Build(string fileName, string contentType, byte[] bytes, string uri)
    {
        var mediaType = MediaTypeOf(contentType);
        // The file name has no home in any of the block shapes, and the model is
        // told which file it asked for either way, so it rides along as metadata.
        var meta = new JsonObject { ["fileName"] = fileName };

        if (mediaType.StartsWith("image/", StringComparison.Ordinal))
        {
            yield return new ImageContentBlock
            {
                // Base64 text as UTF-8 bytes, which is what the protocol carries;
                // the raw file here serialises to mojibake instead of a picture.
                Data = Base64Utf8(bytes),
                MimeType = mediaType,
                Meta = meta,
            };
            yield break;
        }

        if (IsTextLike(mediaType))
        {
            yield return new TextContentBlock { Text = Encoding.UTF8.GetString(bytes), Meta = meta };
            yield break;
        }

        yield return new EmbeddedResourceBlock
        {
            Resource = new BlobResourceContents
            {
                Uri = uri,
                MimeType = mediaType,
                Blob = Base64Utf8(bytes),
            },
            Meta = meta,
        };
    }

    /// <summary>
    /// The base64 text of the file as UTF-8 bytes, which is what the protocol
    /// carries — the raw file in these fields serialises to mojibake rather than
    /// to the picture. Encoded straight into one buffer: going through a string
    /// would hold a 25 MB image three times over, once as itself and twice more
    /// as its base64.
    /// </summary>
    private static ReadOnlyMemory<byte> Base64Utf8(byte[] bytes)
    {
        var encoded = new byte[Base64.GetMaxEncodedToUtf8Length(bytes.Length)];
        Base64.EncodeToUtf8(bytes, encoded, out _, out var written);
        return encoded.AsMemory(0, written);
    }

    private static bool IsTextLike(string mediaType)
        => mediaType.StartsWith("text/", StringComparison.Ordinal)
        || mediaType is "application/json" or "application/xml"
        || mediaType.EndsWith("+json", StringComparison.Ordinal)
        || mediaType.EndsWith("+xml", StringComparison.Ordinal);

    /// <summary>The bare media type: a caller may hand over a whole header value.</summary>
    private static string MediaTypeOf(string? contentType)
    {
        var bare = (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        return bare.Length == 0 ? "application/octet-stream" : bare;
    }
}
