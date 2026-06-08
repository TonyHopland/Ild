using System.Security.Cryptography;
using System.Text;

namespace ILD.Data.Security;

/// <summary>
/// Transparent encryption-at-rest for sensitive entity columns (provider API keys,
/// webhook secrets). Used by EF Core value converters so plaintext never touches the
/// database when an encryption key is configured.
///
/// <para>
/// Key source: the <c>ILD_SECRET_KEY</c> environment variable. Any non-empty string is
/// accepted; the AES-256 key is derived via SHA-256, so an operator can supply a
/// passphrase or (recommended) a high-entropy value such as <c>openssl rand -hex 32</c>.
/// </para>
///
/// <para>
/// Behaviour is intentionally backwards-compatible so existing deployments are never
/// bricked by toggling the feature:
/// <list type="bullet">
///   <item>When no key is set, values are stored and read as plaintext (passthrough).
///   <see cref="IsEnabled"/> is <c>false</c> so the host can warn at startup.</item>
///   <item>On read, values that are not in the encrypted envelope format are returned
///   verbatim, so legacy plaintext rows keep working after a key is added. They become
///   encrypted the next time the row is written.</item>
/// </list>
/// </para>
/// </summary>
public static class SecretProtector
{
    /// <summary>Prefix that marks a value as an encrypted envelope (versioned).</summary>
    private const string Prefix = "enc.v1.";

    private const int NonceSize = 12; // AES-GCM standard nonce
    private const int TagSize = 16;   // AES-GCM authentication tag

    private static byte[]? _key = DeriveKey(Environment.GetEnvironmentVariable("ILD_SECRET_KEY"));

    /// <summary>True when an encryption key is configured; secrets are encrypted at rest.</summary>
    public static bool IsEnabled => _key is not null;

    /// <summary>
    /// Overrides the encryption key derived from <c>ILD_SECRET_KEY</c> at startup. Intended
    /// for hosts that source the key from configuration rather than the environment, and for
    /// tests. Passing null or whitespace disables encryption (passthrough).
    /// </summary>
    public static void Configure(string? rawKey) => _key = DeriveKey(rawKey);

    private static byte[]? DeriveKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>
    /// Encrypts a plaintext value for storage. Returns the input unchanged when no key
    /// is configured. Inputs already in envelope format are returned as-is (idempotent).
    /// </summary>
    public static string? Protect(string? plaintext)
    {
        if (plaintext is null) return null;
        if (_key is null) return plaintext;
        if (plaintext.StartsWith(Prefix, StringComparison.Ordinal)) return plaintext;

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        var envelope = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, envelope, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, envelope, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, envelope, NonceSize + TagSize, cipher.Length);

        return Prefix + Convert.ToBase64String(envelope);
    }

    /// <summary>
    /// Decrypts a stored value. Values not in envelope format are returned verbatim
    /// (legacy plaintext). Throws if an encrypted value is read without a configured key.
    /// </summary>
    public static string? Unprotect(string? stored)
    {
        if (stored is null) return null;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored; // legacy plaintext

        if (_key is null)
            throw new InvalidOperationException(
                "An encrypted secret was read but ILD_SECRET_KEY is not set. Restore the key used to encrypt it.");

        var envelope = Convert.FromBase64String(stored[Prefix.Length..]);
        if (envelope.Length < NonceSize + TagSize)
            throw new CryptographicException("Encrypted secret envelope is malformed.");

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var cipher = new byte[envelope.Length - NonceSize - TagSize];
        Buffer.BlockCopy(envelope, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(envelope, NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(envelope, NonceSize + TagSize, cipher, 0, cipher.Length);

        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
