using System.Security.Cryptography;
using System.Text;
using ILD.Api.Configuration;

namespace ILD.Tests;

public class CorsConfigurationTests
{
    [Fact]
    public void ParseAllowedOrigins_uses_defaults_when_env_is_empty()
    {
        var origins = CorsConfiguration.ParseAllowedOrigins(null);
        Assert.Contains("http://localhost:3000", origins);
        Assert.Contains("http://localhost:5173", origins);
    }

    [Fact]
    public void ParseAllowedOrigins_splits_csv_and_trims()
    {
        var origins = CorsConfiguration.ParseAllowedOrigins(" https://a.example , https://b.example ");
        Assert.Equal(new[] { "https://a.example", "https://b.example" }, origins);
    }

    [Fact]
    public void ParseAllowedOrigins_filters_empty_entries()
    {
        var origins = CorsConfiguration.ParseAllowedOrigins("https://a.example,, ,https://b.example");
        Assert.Equal(new[] { "https://a.example", "https://b.example" }, origins);
    }
}

public class WebhookSignatureTests
{
    private static string ComputeHmac(string secret, string body)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var bytes = h.ComputeHash(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    [Fact]
    public void Verify_returns_true_for_matching_signature()
    {
        var body = "{\"x\":1}";
        var sig = ComputeHmac("topsecret", body);
        Assert.True(WebhookSignatureVerifier.Verify(body, sig, "topsecret"));
    }

    [Fact]
    public void Verify_returns_true_with_sha256_prefix()
    {
        var body = "{\"x\":1}";
        var sig = "sha256=" + ComputeHmac("topsecret", body);
        Assert.True(WebhookSignatureVerifier.Verify(body, sig, "topsecret"));
    }

    [Fact]
    public void Verify_returns_false_for_mismatched_signature()
    {
        Assert.False(WebhookSignatureVerifier.Verify("{}", "deadbeef", "topsecret"));
    }

    [Fact]
    public void Verify_returns_false_for_empty_signature_or_secret()
    {
        Assert.False(WebhookSignatureVerifier.Verify("{}", "", "topsecret"));
        Assert.False(WebhookSignatureVerifier.Verify("{}", "abc", ""));
        Assert.False(WebhookSignatureVerifier.Verify("{}", null, "topsecret"));
    }
}
