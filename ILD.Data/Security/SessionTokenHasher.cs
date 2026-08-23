using System.Security.Cryptography;
using System.Text;

namespace ILD.Data.Security;

/// <summary>
/// Maps a bearer session token to the value stored in <c>UserSessions.TokenHash</c>.
/// The single place a token and a session row are ever related: the sign-in path,
/// every lookup, and the one-time carry-across of the retired plaintext
/// <c>User.SessionToken</c> column must all agree on it exactly.
///
/// <para>
/// With <c>ILD_SESSION_TOKEN_PEPPER</c> set the mapping is HMAC-SHA256 keyed on it,
/// so only a process holding the key can derive a stored value from a token. That is
/// what stops someone who can merely <em>write</em> the database — the lower-trust
/// agent uid of ADR-0014, a restored backup, a replica — from inserting a row for a
/// token they chose and presenting it as a live sign-in. An unkeyed hash offers no
/// such protection: anyone can compute one.
/// </para>
///
/// <para>
/// Unset, the mapping is the bare SHA-256 that predates the pepper, so direct-run
/// development and any deployment that has not set the variable keep working
/// unchanged. There is deliberately no acceptance of unkeyed hashes once a pepper is
/// configured — that would hand the attacker back the exact row they are being
/// stopped from writing. Setting or rotating the pepper therefore re-addresses every
/// row and signs every device out once; nothing else about a session changes.
/// </para>
///
/// <para>
/// Not derived from <c>ILD_SECRET_KEY</c>: the two have opposite loss semantics —
/// losing that key makes encrypted secrets unrecoverable, losing this one costs a
/// re-login — and rotating it must not silently end every sign-in.
/// </para>
/// </summary>
public static class SessionTokenHasher
{
    public const string PepperEnvVar = "ILD_SESSION_TOKEN_PEPPER";

    private static byte[]? _pepper = DeriveKey(Environment.GetEnvironmentVariable(PepperEnvVar));

    /// <summary>True when a pepper is configured and stored hashes are keyed.</summary>
    public static bool IsPeppered => _pepper is not null;

    /// <summary>
    /// Overrides the pepper read from <c>ILD_SESSION_TOKEN_PEPPER</c> at startup.
    /// Intended for hosts that source it from configuration rather than the
    /// environment, and for tests. Null or whitespace reverts to unkeyed hashing.
    /// </summary>
    public static void Configure(string? rawPepper) => _pepper = DeriveKey(rawPepper);

    /// <summary>
    /// The stored hash addressing the row for <paramref name="token"/>. Base64 of 32
    /// bytes either way, so it fits the column at any pepper setting.
    /// </summary>
    public static string Hash(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        return Convert.ToBase64String(
            _pepper is null ? SHA256.HashData(bytes) : HMACSHA256.HashData(_pepper, bytes));
    }

    private static byte[]? DeriveKey(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(raw));
}
