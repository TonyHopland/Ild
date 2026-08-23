using System.Security.Cryptography;
using System.Text;
using ILD.Data.Entities;
using ILD.Data.Security;
using Xunit;

namespace ILD.Tests;

/// <summary>
/// The property the sessions table rests on: with a pepper configured, the value
/// that addresses a session row cannot be produced by anyone who does not hold the
/// pepper — which is what a party with write access to the database is.
///
/// Mutates the process-wide pepper through <see cref="SessionTokenHasher.Configure"/>,
/// so it shares the non-parallel "AuthEnvironment" collection with the other tests
/// that read it and restores the unkeyed baseline in a finally.
/// </summary>
[Collection("AuthEnvironment")]
public class SessionTokenHasherTests
{
    private const string Pepper = "a-strong-test-pepper";

    private static string UnkeyedHash(string token)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    [Fact]
    public void Without_a_pepper_the_hash_is_the_bare_sha256_it_always_was()
    {
        Assert.False(SessionTokenHasher.IsPeppered);
        Assert.Equal(UnkeyedHash("a-token"), SessionTokenHasher.Hash("a-token"));
    }

    [Fact]
    public void A_pepper_makes_the_hash_underivable_from_the_token_alone()
    {
        SessionTokenHasher.Configure(Pepper);
        try
        {
            Assert.True(SessionTokenHasher.IsPeppered);
            Assert.NotEqual(UnkeyedHash("a-token"), SessionTokenHasher.Hash("a-token"));
            Assert.Equal(SessionTokenHasher.Hash("a-token"), SessionTokenHasher.Hash("a-token"));
        }
        finally { SessionTokenHasher.Configure(null); }
    }

    [Fact]
    public void Different_peppers_address_different_rows()
    {
        try
        {
            SessionTokenHasher.Configure(Pepper);
            var first = SessionTokenHasher.Hash("a-token");

            SessionTokenHasher.Configure(Pepper + "-rotated");
            Assert.NotEqual(first, SessionTokenHasher.Hash("a-token"));
        }
        finally { SessionTokenHasher.Configure(null); }
    }

    [Fact]
    public void The_peppered_hash_fits_the_column()
    {
        SessionTokenHasher.Configure(Pepper);
        try
        {
            var max = typeof(UserSession)
                .GetProperty(nameof(UserSession.TokenHash))!
                .GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.MaxLengthAttribute), false)
                .Cast<System.ComponentModel.DataAnnotations.MaxLengthAttribute>()
                .Single().Length;

            Assert.True(SessionTokenHasher.Hash(new string('t', 4096)).Length <= max);
        }
        finally { SessionTokenHasher.Configure(null); }
    }
}
