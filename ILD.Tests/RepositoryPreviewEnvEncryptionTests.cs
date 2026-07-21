using ILD.Data.Entities;
using ILD.Data.Security;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

/// <summary>
/// The repository custom <c>.env</c> holds secrets, so it is encrypted at rest via
/// the same <see cref="SecretProtector"/> value converter the provider API keys use.
/// Mutates the process-wide key, so it shares the serialized "SecretProtector"
/// collection with <see cref="SecretProtectorTests"/>.
/// </summary>
[Collection("SecretProtector")]
public class RepositoryPreviewEnvEncryptionTests
{
    private const string EnvText = "API_TOKEN=s3cr3t-value\nDB_URL=postgres://user:pw@host/db\n";

    [Fact]
    public async Task PreviewEnv_is_stored_encrypted_and_decrypts_on_read()
    {
        SecretProtector.Configure("a-strong-test-key");
        try
        {
            using var db = new TestDb();
            var id = await SeedRepositoryAsync(db, EnvText);

            // The raw column holds the encrypted envelope, never the plaintext.
            var raw = ReadRawPreviewEnv(db);
            Assert.NotNull(raw);
            Assert.StartsWith("enc.v1.", raw);
            Assert.DoesNotContain("s3cr3t-value", raw);

            // A fresh context decrypts transparently through the value converter.
            using var fresh = db.Fresh();
            var loaded = await fresh.Repositories.FindAsync(id);
            Assert.Equal(EnvText, loaded!.PreviewEnv);
        }
        finally { SecretProtector.Configure(null); }
    }

    [Fact]
    public async Task PreviewEnv_is_plaintext_when_no_key_configured()
    {
        SecretProtector.Configure(null);
        using var db = new TestDb();
        await SeedRepositoryAsync(db, EnvText);

        var raw = ReadRawPreviewEnv(db);
        Assert.Equal(EnvText, raw);
    }

    private static async Task<Guid> SeedRepositoryAsync(TestDb db, string previewEnv)
    {
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = "repo",
            CloneUrl = "https://example/repo.git",
            RemoteProviderId = remote.Id,
            PreviewEnv = previewEnv,
        };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        await db.Context.SaveChangesAsync();
        return repo.Id;
    }

    // Bypass the value converter to see what is physically stored in the column.
    private static string? ReadRawPreviewEnv(TestDb db)
    {
        var conn = db.Context.Database.GetDbConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT PreviewEnv FROM Repositories LIMIT 1";
        var value = cmd.ExecuteScalar();
        return value == null || value is DBNull ? null : (string)value;
    }
}
