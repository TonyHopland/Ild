using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using ILD.Api.Configuration;

namespace ILD.Tests;

public class CorsConfigurationTests
{
    [Fact]
    public void ParseAllowedOrigins_uses_defaults_when_env_is_empty()
    {
        var origins = CorsConfiguration.ParseAllowedOrigins(null);
        origins.Should().Contain("http://localhost:3000").And.Contain("http://localhost:5173");
    }

    [Fact]
    public void ParseAllowedOrigins_splits_csv_and_trims()
    {
        var origins = CorsConfiguration.ParseAllowedOrigins(" https://a.example , https://b.example ");
        origins.Should().Equal("https://a.example", "https://b.example");
    }

    [Fact]
    public void ParseAllowedOrigins_filters_empty_entries()
    {
        var origins = CorsConfiguration.ParseAllowedOrigins("https://a.example,, ,https://b.example");
        origins.Should().Equal("https://a.example", "https://b.example");
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
        WebhookSignatureVerifier.Verify(body, sig, "topsecret").Should().BeTrue();
    }

    [Fact]
    public void Verify_returns_true_with_sha256_prefix()
    {
        var body = "{\"x\":1}";
        var sig = "sha256=" + ComputeHmac("topsecret", body);
        WebhookSignatureVerifier.Verify(body, sig, "topsecret").Should().BeTrue();
    }

    [Fact]
    public void Verify_returns_false_for_mismatched_signature()
    {
        WebhookSignatureVerifier.Verify("{}", "deadbeef", "topsecret").Should().BeFalse();
    }

    [Fact]
    public void Verify_returns_false_for_empty_signature_or_secret()
    {
        WebhookSignatureVerifier.Verify("{}", "", "topsecret").Should().BeFalse();
        WebhookSignatureVerifier.Verify("{}", "abc", "").Should().BeFalse();
        WebhookSignatureVerifier.Verify("{}", null, "topsecret").Should().BeFalse();
    }
}
