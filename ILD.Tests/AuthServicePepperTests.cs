using ILD.Data.Security;
using static ILD.Tests.AuthServiceTests;

namespace ILD.Tests;

/// <summary>
/// The <see cref="AuthService"/> tests that switch the process-wide pepper on
/// through <see cref="SessionTokenHasher.Configure"/>; see <see cref="ProcessGlobalStateCollection"/>.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public class AuthServicePepperTests
{
    /// <summary>
    /// The reason the pepper exists. Anything that can write the database — the
    /// lower-trust agent uid of ADR-0014, a restored backup — can insert a
    /// UserSessions row naming a token it chose. With a pepper configured it cannot
    /// compute the value that row has to be addressed by, so the token it holds
    /// resolves to nothing.
    /// </summary>
    [Fact]
    public async Task A_session_row_whose_hash_the_attacker_computed_does_not_authenticate()
    {
        SessionTokenHasher.Configure("a-strong-test-pepper");
        try
        {
            using var db = new TestDb();
            var svc = Make(db);
            var minted = await LoginAsync(svc);

            var forged = await InsertUnkeyedSessionAsync(db, "attacker-chosen-token");

            Assert.False(await svc.ValidateSessionAsync(forged));
            Assert.Null(await svc.GetUsernameAsync(forged));
            Assert.Empty(await svc.GetSessionsAsync(forged));
            Assert.True(await svc.ValidateSessionAsync(minted));
        }
        finally { SessionTokenHasher.Configure(null); }
    }

    [Fact]
    public async Task Turning_the_pepper_on_signs_existing_devices_out()
    {
        using var db = new TestDb();
        var svc = Make(db);
        var token = await LoginAsync(svc);

        SessionTokenHasher.Configure("a-strong-test-pepper");
        try
        {
            Assert.False(await svc.ValidateSessionAsync(token));
            Assert.True(await svc.ValidateSessionAsync(await LoginAsync(Make(db))));
        }
        finally { SessionTokenHasher.Configure(null); }
    }
}
