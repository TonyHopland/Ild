using System.ComponentModel.DataAnnotations;

namespace ILD.Data.Entities;

/// <summary>
/// One live sign-in for a <see cref="User"/> — a browser tab, a phone, a CLI.
/// A user has as many of these as they have devices; logging in on one never
/// touches the others.
///
/// Not to be confused with a <see cref="ChatSession"/> (a chat transcript) or
/// with the coding-agent sessions recorded by <see cref="AdapterSessionSnapshot"/>
/// and <see cref="LoopRunSessionBinding"/>. "Session" unqualified means one of
/// those in this codebase; this one is always a <c>UserSession</c>.
///
/// The bearer token itself is never stored: only <see cref="TokenHash"/>, so a
/// database read cannot be replayed as a login. Revocation stamps
/// <see cref="RevokedAt"/> rather than deleting the row, so the history of what
/// was signed in stays readable.
/// </summary>
public class UserSession
{
    [Key]
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User? User { get; set; }

    /// <summary>
    /// The bearer token as <see cref="ILD.Data.Security.SessionTokenHasher"/> maps
    /// it. The token is 256 bits of
    /// <see cref="System.Security.Cryptography.RandomNumberGenerator"/> output, so
    /// there is nothing to brute-force and no KDF stretch is owed on every
    /// authenticated request. What the hash must additionally be is unforgeable by
    /// someone who can write this table but does not hold the server's pepper —
    /// hence keyed rather than bare. Base64 of 32 bytes either way.
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Last time this session authenticated a request. Bumped coarsely (at most
    /// once a minute) and drives idle expiry plus the active-sessions list.
    /// </summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>
    /// Absolute expiry, fixed when the session is created. Null means the
    /// absolute cap was disabled at sign-in time; idle expiry still applies.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    [MaxLength(512)]
    public string? UserAgent { get; set; }

    [MaxLength(64)]
    public string? CreatedFromIp { get; set; }

    public DateTime? RevokedAt { get; set; }
}
