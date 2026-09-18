using System.ComponentModel.DataAnnotations;

namespace ILD.WorkItemServer.Domain;

/// <summary>
/// A file a human attached to a work item. The bytes live in this row and
/// nowhere else — there is no file store and nothing is written to disk — so an
/// attachment cannot outlive, or go missing from, the work item that owns it.
/// </summary>
public class WorkItemAttachment
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// The owning <see cref="WorkItem.InternalId"/> — the WorkItems primary key,
    /// of which <see cref="WorkItem.Id"/> is the textual form.
    /// </summary>
    public int WorkItemId { get; set; }

    [Required]
    [MaxLength(255)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>Normalised on the way in and again on the way out; see <c>AttachmentContentType</c>.</summary>
    [Required]
    [MaxLength(255)]
    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    [Required]
    public byte[] Content { get; set; } = Array.Empty<byte>();

    public DateTime CreatedAt { get; set; }
}
