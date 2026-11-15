using System.ComponentModel.DataAnnotations;

namespace ILD.Data.Entities;

public enum ExternalAgentGrantScope
{
    /// <summary>
    /// Agent-level rights, no run control: reads, work items of its own, and proposals. Stored as 0 so existing
    /// grants keep it; the UI calls it "Read, create and propose".
    /// </summary>
    Full = 0,

    /// <summary>Reads only. Creating work items and making proposals are refused.</summary>
    ReadOnly = 1,

    /// <summary>
    /// Everything <see cref="Full"/> offers, and control of the work it is handed (ADR-0039): start any work item,
    /// queue it and answer a human-feedback stop.
    /// </summary>
    Control = 2,
}

/// <summary>
/// Temporary access an operator hands an AI session running outside ILD (ADR-0037). The grant is
/// its own agent session: what it creates is stamped with <see cref="Id"/>, and what it proposes
/// waits for a human, exactly as a loop run's or a chat's does. Only <see cref="TokenHash"/> is
/// stored, so the token cannot be read back after it was shown once.
/// </summary>
public class ExternalAgentGrant
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public ExternalAgentGrantScope Scope { get; set; }

    public DateTime? LastUsedAt { get; set; }

    [MaxLength(64)]
    public string? LastUsedFrom { get; set; }

    public DateTime? RevokedAt { get; set; }
}

/// <summary>One request a grant's token made, as the audit trail on the grant's page lists it.</summary>
public class ExternalAgentGrantActivity
{
    [Key]
    public Guid Id { get; set; }

    public Guid GrantId { get; set; }

    public DateTime At { get; set; }

    [Required]
    [MaxLength(16)]
    public string Method { get; set; } = string.Empty;

    [Required]
    [MaxLength(512)]
    public string Path { get; set; } = string.Empty;

    public int StatusCode { get; set; }

    [MaxLength(64)]
    public string? From { get; set; }
}
