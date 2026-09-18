using ILD.WorkItemServer.Attachments;
using ILD.WorkItemServer.Dtos;
using ILD.WorkItemServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace ILD.WorkItemServer.Controllers;

/// <summary>
/// The files a work item carries. Every limit is enforced here as well as on the
/// ILD instance in front of it: an API key is all it takes to reach this server
/// directly, so this is the boundary that has to hold.
/// </summary>
[ApiController]
[Route("workitems/{id}/attachments")]
public sealed class WorkItemAttachmentsController : ControllerBase
{
    /// <summary>The form field every attachment upload arrives under.</summary>
    private const string AttachmentFieldName = "files";

    private readonly IWorkItemAttachmentService _attachments;

    public WorkItemAttachmentsController(IWorkItemAttachmentService attachments) => _attachments = attachments;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkItemAttachmentDto>>> List(string id, CancellationToken ct)
    {
        var listed = await _attachments.ListAsync(id, ct);
        return listed == null ? NotFound() : Ok(listed);
    }

    /// <summary>
    /// The files arrive as a form this action reads itself, and the cap on how
    /// much body it may read is raised by the filter, which runs before anything
    /// touches the body.
    /// </summary>
    [HttpPost]
    [RaiseAttachmentBodyLimit]
    public async Task<IActionResult> Upload(string id, CancellationToken ct)
    {
        if (!Request.HasFormContentType)
            return BadRequest(new { error = "Attachments are uploaded as multipart/form-data under the field 'files'." });

        var form = await Request.ReadFormAsync(ct);
        // Size now, bytes only if the service accepts them: a refused upload
        // copies nothing, and an accepted one is copied once per file.
        var files = form.Files.GetFiles(AttachmentFieldName)
            .Select(file => new IncomingAttachment(
                file.FileName, file.ContentType, file.Length, token => ReadExactlyAsync(file, token)))
            .ToList();

        var result = await _attachments.AddAsync(id, files, ct);
        return result.Outcome switch
        {
            AddAttachmentsOutcome.Created => StatusCode(StatusCodes.Status201Created, result.Created),
            AddAttachmentsOutcome.NotFound => NotFound(),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    [HttpGet("{attachmentId:guid}")]
    public async Task<IActionResult> Download(string id, Guid attachmentId, CancellationToken ct)
    {
        var stored = await _attachments.GetContentAsync(id, attachmentId, ct);
        if (stored == null) return NotFound();

        // This server has no security-headers middleware, and an uploaded page
        // served inline would run on its origin.
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(stored.Content, stored.ContentType, stored.FileName);
    }

    [HttpDelete("{attachmentId:guid}")]
    public async Task<IActionResult> Delete(string id, Guid attachmentId, CancellationToken ct)
        => await _attachments.DeleteAsync(id, attachmentId, ct) ? NoContent() : NotFound();

    /// <summary>
    /// The file as one array of exactly its length. A <see cref="MemoryStream"/>
    /// would hold the bytes twice over — its own doubling buffer and the copy
    /// <c>ToArray</c> takes — which at ten files of the maximum size is the
    /// difference between one payload in memory and two.
    /// </summary>
    private static async Task<byte[]> ReadExactlyAsync(IFormFile file, CancellationToken ct)
    {
        var content = new byte[file.Length];
        await using var stream = file.OpenReadStream();
        await stream.ReadExactlyAsync(content, ct);
        return content;
    }
}
