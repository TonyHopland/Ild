using ILD.WorkItemServer.Attachments;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// What the server is willing to store, and later hand back, as a content type.
/// The value comes from whoever uploaded the file, and it is echoed on every
/// download, so anything that is not a media type has to become one before it is
/// stored — and again before it is served, for rows written when it was not.
/// </summary>
public class AttachmentContentTypeTests
{
    private const string Fallback = "application/octet-stream";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a content type")]
    [InlineData("a/b/c")]
    [InlineData("application/")]
    public void Anything_that_is_not_a_media_type_becomes_application_octet_stream(string? supplied)
    {
        Assert.Equal(Fallback, AttachmentContentType.Normalize(supplied));
    }

    [Theory]
    [InlineData("image/png", "image/png")]
    [InlineData("text/plain", "text/plain")]
    [InlineData("application/vnd.api+json", "application/vnd.api+json")]
    public void A_media_type_is_kept(string supplied, string expected)
    {
        Assert.Equal(expected, AttachmentContentType.Normalize(supplied));
    }

    [Fact]
    public void Parameters_are_dropped_and_the_type_is_lower_cased()
    {
        Assert.Equal("text/plain", AttachmentContentType.Normalize("text/plain; charset=utf-8"));
        Assert.Equal("image/png", AttachmentContentType.Normalize("IMAGE/PNG"));
    }
}
