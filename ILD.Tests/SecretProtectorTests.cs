using ILD.Data.Security;

namespace ILD.Tests;

/// <summary>
/// Mutates the process-wide encryption key via <see cref="SecretProtector.Configure"/>,
/// so these tests must not run concurrently with each other. Each test restores the
/// disabled (no-key) default in a finally block.
/// </summary>
[Collection("SecretProtector")]
public class SecretProtectorTests
{
    [Fact]
    public void Passthrough_when_no_key_configured()
    {
        SecretProtector.Configure(null);
        try
        {
            Assert.False(SecretProtector.IsEnabled);
            Assert.Equal("plain-secret", SecretProtector.Protect("plain-secret"));
            Assert.Equal("plain-secret", SecretProtector.Unprotect("plain-secret"));
        }
        finally { SecretProtector.Configure(null); }
    }

    [Fact]
    public void Round_trips_when_key_configured()
    {
        SecretProtector.Configure("a-strong-test-key");
        try
        {
            Assert.True(SecretProtector.IsEnabled);
            const string secret = "sk-live-abcdef0123456789";
            var stored = SecretProtector.Protect(secret);

            Assert.NotNull(stored);
            Assert.NotEqual(secret, stored);
            Assert.StartsWith("enc.v1.", stored);
            Assert.Equal(secret, SecretProtector.Unprotect(stored));
        }
        finally { SecretProtector.Configure(null); }
    }

    [Fact]
    public void Encryption_uses_random_nonce_so_ciphertexts_differ()
    {
        SecretProtector.Configure("a-strong-test-key");
        try
        {
            var a = SecretProtector.Protect("same-input");
            var b = SecretProtector.Protect("same-input");
            Assert.NotEqual(a, b);
            Assert.Equal("same-input", SecretProtector.Unprotect(a));
            Assert.Equal("same-input", SecretProtector.Unprotect(b));
        }
        finally { SecretProtector.Configure(null); }
    }

    [Fact]
    public void Legacy_plaintext_is_readable_after_key_added()
    {
        SecretProtector.Configure("a-strong-test-key");
        try
        {
            // A value written before encryption was enabled has no envelope prefix.
            Assert.Equal("legacy-plaintext", SecretProtector.Unprotect("legacy-plaintext"));
        }
        finally { SecretProtector.Configure(null); }
    }

    [Fact]
    public void Protect_is_idempotent_on_already_encrypted_values()
    {
        SecretProtector.Configure("a-strong-test-key");
        try
        {
            var once = SecretProtector.Protect("secret");
            var twice = SecretProtector.Protect(once);
            Assert.Equal(once, twice);
        }
        finally { SecretProtector.Configure(null); }
    }

    [Fact]
    public void Null_is_preserved()
    {
        SecretProtector.Configure("a-strong-test-key");
        try
        {
            Assert.Null(SecretProtector.Protect(null));
            Assert.Null(SecretProtector.Unprotect(null));
        }
        finally { SecretProtector.Configure(null); }
    }
}

[CollectionDefinition("SecretProtector", DisableParallelization = true)]
public class SecretProtectorCollection { }
