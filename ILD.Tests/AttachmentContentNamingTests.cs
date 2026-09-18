using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ILD.McpServer;
using ILD.McpServer.Attachments;
using ILD.McpServer.Tools;
using ModelContextProtocol.Protocol;

namespace ILD.Tests;

/// <summary>
/// Which file the model is looking at. None of the three block shapes has a
/// place for a name, so it travels as block metadata — and when the API serves
/// a file without one, the tool still has to hand back a block rather than fail.
/// </summary>
public class AttachmentContentNamingTests
{
    private const string Uri = "ild://workitems/42/attachments/9c6f4a2e";

    private static string? NameOf(ContentBlock block) => block.Meta?["fileName"]?.GetValue<string>();

    [Theory]
    [InlineData("sketch.png", "image/png")]
    [InlineData("notes.md", "text/markdown")]
    [InlineData("archive.zip", "application/zip")]
    public void Every_shape_carries_the_file_name_it_was_built_from(string fileName, string contentType)
    {
        var block = Assert.Single(
            AttachmentContent.Build(fileName, contentType, Encoding.UTF8.GetBytes("payload"), Uri).ToList());

        Assert.Equal(fileName, NameOf(block));
    }

    [Fact]
    public void A_content_type_carrying_parameters_is_shaped_by_its_media_type()
    {
        var block = Assert.Single(
            AttachmentContent.Build("notes.txt", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("hello"), Uri).ToList());

        Assert.Equal("hello", Assert.IsType<TextContentBlock>(block).Text);
    }

    [Fact]
    public async Task A_file_served_without_a_name_still_reaches_the_model()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var tools = new AttachmentTools(new IldClient(
            new HttpClient(new NamelessFileHandler(bytes)) { BaseAddress = new Uri("http://ild-host:8080/") },
            new IldClientOptions("http://ild-host:8080", "the-agent-token", LoopRunId: null)));

        var blocks = (await tools.GetWorkItemAttachment("42", Guid.NewGuid().ToString())).ToList();

        var image = Assert.IsType<ImageContentBlock>(Assert.Single(blocks));
        Assert.Equal(bytes, image.DecodedData.ToArray());
        Assert.False(string.IsNullOrWhiteSpace(NameOf(image)));
    }

    /// <summary>Answers with a file and no <c>Content-Disposition</c> at all.</summary>
    private sealed class NamelessFileHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
