using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ILD.Data.Entities;

/// <summary>
/// A file a human attached to a chat turn, bytes and all.
///
/// <para>
/// The bytes live here rather than on disk deliberately. A chat session's scratch
/// directory is shared-group readable by the agent uid (ADR-0014), and there is
/// one such uid for every chat, so anything kept there for the life of a chat is
/// readable by every later agent. The database is not reachable by that uid at
/// all — the launch seam strips the connection strings from the agent's
/// environment — so a retained attachment is genuinely private to its chat, and
/// showing it to the human never touches the filesystem.
/// </para>
///
/// <para>
/// The agent still needs a real file to open, so a turn writes its attachments
/// into the session's scratch directory for the length of that turn and removes
/// them afterwards. On-disk presence is transient; this row is the durable copy.
/// </para>
/// </summary>
public class ChatAttachment
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>The owning session, so the rows go when the chat is deleted.</summary>
    [Required]
    [ForeignKey(nameof(ChatSession))]
    public Guid ChatSessionId { get; set; }

    /// <summary>
    /// The transcript turn this was attached to. Null between the upload and the
    /// turn that carries it — a chat that is abandoned in that window leaves rows
    /// that are reclaimed with the session like any other.
    /// </summary>
    [ForeignKey(nameof(ChatMessage))]
    public Guid? ChatMessageId { get; set; }

    /// <summary>Sanitized name, shown to the human and used for the file the agent opens.</summary>
    [Required]
    [MaxLength(260)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>Reported MIME type, or null when the client sent none.</summary>
    [MaxLength(255)]
    public string? ContentType { get; set; }

    /// <summary>The file itself.</summary>
    [Required]
    public byte[] Content { get; set; } = Array.Empty<byte>();

    public DateTime CreatedAt { get; set; }

    public ChatSession ChatSession { get; set; } = null!;

    public ChatMessage? ChatMessage { get; set; }
}
