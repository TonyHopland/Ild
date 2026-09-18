using System.Reflection;
using System.Text;
using ILD.McpServer.Attachments;
using ModelContextProtocol.Protocol;

namespace ILD.Tests;

/// <summary>
/// How a stored file is handed to a model: an image it can actually look at,
/// text it can read, and everything else as a resource it can carry but not
/// misread. The shaping knows nothing about work items so the chat-attachment
/// tool can call it too.
///
/// The base64 detail is not cosmetic: <c>ImageContentBlock.Data</c> and
/// <c>BlobResourceContents.Blob</c> are the base64 <em>text</em> as UTF-8 bytes,
/// and handing them the raw file instead yields a block that serialises to
/// mojibake rather than to the picture.
/// </summary>
public class AttachmentContentTests
{
    private const string Uri = "ild://workitems/42/attachments/9c6f4a2e";

    private static byte[] Png() => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xFF, 0x00, 0x7F };

    private static ContentBlock Single(string fileName, string contentType, byte[] bytes, string uri = Uri)
        => Assert.Single(AttachmentContent.Build(fileName, contentType, bytes, uri).ToList());

    [Fact]
    public void An_image_comes_back_as_image_content_carrying_the_stored_bytes()
    {
        var bytes = Png();

        var block = Assert.IsType<ImageContentBlock>(Single("sketch.png", "image/png", bytes));

        Assert.Equal("image", block.Type);
        Assert.Equal("image/png", block.MimeType);
        Assert.Equal(bytes, block.DecodedData.ToArray());
        Assert.Equal(bytes, Convert.FromBase64String(Encoding.UTF8.GetString(block.Data.Span)));
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/markdown")]
    [InlineData("application/json")]
    [InlineData("application/xml")]
    [InlineData("application/vnd.api+json")]
    [InlineData("application/atom+xml")]
    public void A_text_like_file_comes_back_as_the_text_itself(string contentType)
    {
        const string body = "{\"hello\": \"wørld\"}";

        var block = Assert.IsType<TextContentBlock>(Single("payload", contentType, Encoding.UTF8.GetBytes(body)));

        Assert.Equal("text", block.Type);
        Assert.Equal(body, block.Text);
    }

    [Theory]
    [InlineData("application/zip")]
    [InlineData("application/pdf")]
    [InlineData("application/octet-stream")]
    public void Anything_else_comes_back_as_an_embedded_binary_resource(string contentType)
    {
        var bytes = Png();

        var block = Assert.IsType<EmbeddedResourceBlock>(Single("archive.bin", contentType, bytes));

        var resource = Assert.IsType<BlobResourceContents>(block.Resource);
        Assert.Equal("resource", block.Type);
        Assert.Equal(contentType, resource.MimeType);
        Assert.Equal(Uri, resource.Uri);
        Assert.Equal(bytes, resource.DecodedData.ToArray());
        Assert.Equal(bytes, Convert.FromBase64String(Encoding.UTF8.GetString(resource.Blob.Span)));
    }

    [Fact]
    public void The_shaping_takes_a_file_and_not_a_work_item()
    {
        // Chat attachments shape their content through this same call, with an
        // identifier of their own and no work item anywhere in sight.
        const string chatUri = "ild://chats/7/attachments/1";
        var bytes = Png();

        var block = Assert.IsType<EmbeddedResourceBlock>(
            Assert.Single(AttachmentContent.Build("archive.zip", "application/zip", bytes, chatUri).ToList()));

        Assert.Equal(chatUri, Assert.IsType<BlobResourceContents>(block.Resource).Uri);

        var parameters = typeof(AttachmentContent)
            .GetMethod(nameof(AttachmentContent.Build), BindingFlags.Public | BindingFlags.Static)!
            .GetParameters();
        Assert.All(parameters, p => Assert.DoesNotContain("workitem", p.Name!, StringComparison.OrdinalIgnoreCase));
    }
}
