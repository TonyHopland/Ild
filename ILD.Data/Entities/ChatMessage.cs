using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ILD.Data.Entities;

/// <summary>
/// One renderable turn in a <see cref="ChatSession"/> transcript. Rehydrates the
/// chat bubble on reopen/restart. Tool activity is inlined into
/// <see cref="Content"/> as markers by the streaming adapter rather than stored
/// structurally; the partial assistant reply of an interrupted turn is kept and
/// flagged via <see cref="Interrupted"/>.
/// </summary>
public class ChatMessage
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [ForeignKey(nameof(ChatSession))]
    public Guid ChatSessionId { get; set; }

    /// <summary><c>user</c> or <c>assistant</c>.</summary>
    [Required]
    [MaxLength(16)]
    public string Role { get; set; } = string.Empty;

    /// <summary>Markdown content (unbounded text).</summary>
    [Required]
    public string Content { get; set; } = string.Empty;

    /// <summary>True when this assistant reply was cut short by a mid-turn interrupt.</summary>
    public bool Interrupted { get; set; }

    /// <summary>
    /// JSON-serialized array of <see cref="DTOs.AttachmentRef"/> — the files the
    /// human attached to this turn. Metadata only: the bytes sit in the session's
    /// scratch directory, and the agent was handed their paths in the prompt.
    /// Null on every turn that carried no attachment, which is most of them.
    /// </summary>
    public string? AttachmentsJson { get; set; }

    /// <summary>Monotonic per-session ordering key.</summary>
    public int Sequence { get; set; }

    public DateTime CreatedAt { get; set; }

    public ChatSession ChatSession { get; set; } = null!;
}
