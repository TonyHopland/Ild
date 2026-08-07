using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;

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
    /// Base64 SHA-256 of the bearer token. The token is 256 bits of
    /// <see cref="System.Security.Cryptography.RandomNumberGenerator"/> output,
    /// so a plain hash is enough — there is nothing to brute-force and a KDF
    /// would cost a stretch on every authenticated request.
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

    /// <summary>
    /// Maps a bearer token to the <see cref="TokenHash"/> that addresses its row.
    /// Lives on the entity because it is the only way a token and a session row
    /// are ever related — the sign-in path and the one-time carry-across of the
    /// old plaintext <c>User.SessionToken</c> column must agree on it exactly.
    /// </summary>
    public static string HashToken(string token)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
