using System.Security.Cryptography;
using System.Text;

namespace ILD.Api.Configuration;

public static class WebhookSignatureVerifier
{
    /// <summary>
    /// Verifies an HMAC-SHA256 signature for a webhook body against a secret.
    /// Accepts both raw hex and "sha256=<hex>" prefixed signatures (Forgejo / Gitea style).
    /// </summary>
    public static bool Verify(string body, string? signature, string? secret)
    {
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(secret) || body == null)
            return false;

        var sig = signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? signature.Substring("sha256=".Length)
            : signature;

        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(sig.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(expected));
    }
}
