using System.Text;
using ILD.Data.DTOs;

namespace ILD.Core.Services.Attachments;

/// <summary>
/// How attached files are described to the agent. One wording for every path —
/// a chat turn, a work item's AI node, the <c>{{WorkItem.Attachments}}</c>
/// placeholder — so an agent trained by one never meets a different shape in
/// another.
/// </summary>
public static class AttachmentPromptBlock
{
    public static string? Format(IReadOnlyList<AttachmentRef>? attachments)
    {
        if (attachments is null || attachments.Count == 0) return null;

        var sb = new StringBuilder("[Attachments]\n");
        sb.Append("The human attached the following files. Read them from these absolute paths ");
        sb.Append("with your file tools — images included, they are on disk, not in this message.");
        foreach (var a in attachments)
            sb.Append("\n- ").Append(a.FileName).Append(" (").Append(Describe(a)).Append("): ").Append(a.StoredPath);

        return sb.ToString();
    }

    private static string Describe(AttachmentRef a)
        => a.ContentType is null ? FormatSize(a.SizeBytes) : $"{a.ContentType}, {FormatSize(a.SizeBytes)}";

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.#} MB"
         : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB"
         : $"{bytes} B";
}
