using System.ComponentModel.DataAnnotations;

namespace ILD.Data.Entities;

/// <summary>Whose agent launch an <see cref="AgentToken"/> was issued for.</summary>
public enum AgentTokenSource
{
    Run = 0,
    Chat = 1,
    Grant = 2,
}

/// <summary>
/// The bearer token ILD hands one agent launch for the agent API: a loop run's node execution or a chat turn.
/// It names the run or chat the launch belongs to, which is how the API knows who is calling; nothing the agent
/// sends can say otherwise. Only <see cref="TokenHash"/> is stored. Revocation stamps <see cref="RevokedAt"/>.
/// </summary>
public class AgentToken
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    public AgentTokenSource Source { get; set; }

    /// <summary>The loop run or chat session id, by <see cref="Source"/>.</summary>
    public Guid SourceId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }
}
