using System.ComponentModel;
using ILD.McpServer.Attachments;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// Reading the files a human attached to a work item. Read-only: an agent may
/// look at an attachment, never add or remove one.
/// </summary>
[McpServerToolType]
public sealed class AttachmentTools
{
    private readonly IldClient _ild;

    public AttachmentTools(IldClient ild) { _ild = ild; }

    [McpServerTool(Name = "get_workitem_attachment")]
    [Description("Fetch one file attached to a work item — a sketch, a screenshot, a log. Call get_workitem first: its attachments array carries the id, name, content type and size of every file on the item. An image comes back as an image you can look at, a text-like file as its text, and anything else as an embedded binary resource.")]
    public async Task<IEnumerable<ContentBlock>> GetWorkItemAttachment(
        [Description("Work item id, as it appears on get_workitem.")] string workItemId,
        [Description("Attachment id, from the attachments array on get_workitem.")] string attachmentId)
    {
        var path = $"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/attachments/{Uri.EscapeDataString(attachmentId)}";
        var file = await _ild.GetBinaryAsync(path);
        return AttachmentContent.Build(
            file.FileName, file.ContentType, file.Content, $"ild://workitems/{workItemId}/attachments/{attachmentId}");
    }
}
