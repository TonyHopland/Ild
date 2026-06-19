using System.ComponentModel.DataAnnotations;

namespace ILD.Data.Entities;

/// <summary>
/// A standalone, one-per-user interactive chat with a configured
/// <see cref="AiProvider"/>, opened from the in-app chat bubble. Deliberately
/// NOT a <c>LoopRun</c> (see ADR-0010): it has no WorkItem, worktree, branch, or
/// PR, but reuses the loop's agent-adapter execution layer and the same
/// <see cref="AdapterSessionSnapshot"/> store (widened to key on either a
/// LoopRun or a ChatSession). It is durable — the session row, its bound adapter
/// session, its <see cref="ChatMessage"/> transcript, and its scratch directory
/// survive process restarts and are reclaimed only when the user explicitly ends
/// the chat (the <c>ChatSessionRetentionSweeper</c> backstops abandoned sessions).
/// </summary>
public class ChatSession : IHasUpdatedAt
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>The owning user (the authenticated username). One chat per user.</summary>
    [Required]
    [MaxLength(128)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>The chosen <see cref="AiProvider"/>; fixed for the session's life.</summary>
    [Required]
    public Guid AiProviderId { get; set; }

    /// <summary>The provider type (e.g. <c>claude-code</c>) captured at start for display/recovery.</summary>
    [Required]
    [MaxLength(64)]
    public string ProviderType { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated tool allowlist (subset of <c>read</c>/<c>write</c>/
    /// <c>execute</c>/<c>ild</c>) chosen at start; fixed for the session's life.
    /// </summary>
    [Required]
    [MaxLength(256)]
    public string ToolAllowlistCsv { get; set; } = string.Empty;

    /// <summary>The durable scratch directory the agent runs in (acts as the synthesized worktree).</summary>
    [Required]
    [MaxLength(1024)]
    public string ScratchPath { get; set; } = string.Empty;

    /// <summary>
    /// The bound adapter session id captured mid-stream, so a later turn (or a
    /// turn after a restart) resumes the SAME agent session. Null until the
    /// first turn binds one.
    /// </summary>
    [MaxLength(256)]
    public string? CurrentSessionId { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Last-activity timestamp; also the cutoff the idle sweeper checks.</summary>
    public DateTime? UpdatedAt { get; set; }

    public List<ChatMessage> Messages { get; set; } = new();
}
