using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ILD.McpServer;
using ILD.McpServer.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ILD.Tests;

/// <summary>
/// The one tool that hands an agent a file. It reads through the same
/// agent-scoped API path <c>get_workitem</c> uses, and it reports a failure the
/// way every other ILD tool does — with the status and the address in the
/// message, because the MCP host replaces anything that is not an
/// <see cref="McpException"/> with "An error occurred invoking '&lt;tool&gt;'".
/// </summary>
public class AttachmentToolsTests
{
    private const string BaseAddress = "http://ild-host:8080/";
    private const string WorkItemId = "42";
    private static readonly Guid AttachmentId = Guid.Parse("9c6f4a2e-1111-2222-3333-444455556666");

    private static AttachmentTools Tools(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new IldClient(
            new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri(BaseAddress) },
            new IldClientOptions(BaseAddress.TrimEnd('/'), "the-agent-token", LoopRunId: null)));

    private static HttpResponseMessage File(byte[] bytes, string contentType, string fileName)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = fileName };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    [Fact]
    public async Task An_image_attachment_reaches_the_model_as_an_image_it_can_see()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0xFF, 0x00, 0x10 };
        HttpRequestMessage? seen = null;
        var tools = Tools(req => { seen = req; return File(bytes, "image/png", "sketch.png"); });

        var blocks = (await tools.GetWorkItemAttachment(WorkItemId, AttachmentId.ToString())).ToList();

        Assert.Equal($"{BaseAddress}api/v1/agent/workitems/{WorkItemId}/attachments/{AttachmentId}",
            seen!.RequestUri!.ToString());
        var image = Assert.IsType<ImageContentBlock>(Assert.Single(blocks));
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(bytes, image.DecodedData.ToArray());
    }

    [Fact]
    public async Task A_text_attachment_reaches_the_model_as_text()
    {
        const string body = "# design notes";
        var tools = Tools(_ => File(Encoding.UTF8.GetBytes(body), "text/markdown", "notes.md"));

        var blocks = (await tools.GetWorkItemAttachment(WorkItemId, AttachmentId.ToString())).ToList();

        Assert.Equal(body, Assert.IsType<TextContentBlock>(Assert.Single(blocks)).Text);
    }

    [Fact]
    public async Task A_missing_attachment_is_an_McpException_naming_the_status_and_the_address()
    {
        var tools = Tools(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"error":"Not Found"}"""),
        });

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.GetWorkItemAttachment(WorkItemId, AttachmentId.ToString()));

        Assert.Contains("404", ex.Message);
        Assert.Contains($"{BaseAddress}api/v1/agent/workitems/{WorkItemId}/attachments/{AttachmentId}", ex.Message);
    }

    [Fact]
    public async Task An_expired_agent_token_is_distinguishable_from_a_missing_attachment()
    {
        var tools = Tools(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"Unauthorized","message":"Invalid or expired session"}"""),
        });

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.GetWorkItemAttachment(WorkItemId, AttachmentId.ToString()));

        Assert.Contains("401", ex.Message);
        Assert.Contains("Invalid or expired session", ex.Message);
    }

    [Fact]
    public async Task An_unreachable_API_names_the_address_it_tried()
    {
        var tools = Tools(_ => throw new HttpRequestException("Connection refused (ild-host:8080)"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.GetWorkItemAttachment(WorkItemId, AttachmentId.ToString()));

        Assert.Contains($"ILD_API_URL={BaseAddress.TrimEnd('/')}", ex.Message);
        Assert.Contains("Connection refused", ex.Message);
    }

    [Fact]
    public void The_server_offers_exactly_one_attachment_tool_and_it_only_reads()
    {
        var names = McpServerToolReflection.Names();

        Assert.Contains("get_workitem_attachment", names);
        Assert.Equal(
            new[] { "get_workitem_attachment" },
            names.Where(n => n.Contains("attachment", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
