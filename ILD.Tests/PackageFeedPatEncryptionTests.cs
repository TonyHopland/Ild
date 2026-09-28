using ILD.Data.Entities;
using ILD.Data.Security;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

/// <summary>
/// A feed's PAT is encrypted at rest by the same value converter as every other
/// secret. Mutates the process-wide key, so it shares the serialized
/// "SecretProtector" collection with <see cref="SecretProtectorTests"/>.
/// </summary>
[Collection("SecretProtector")]
public class PackageFeedPatEncryptionTests
{
    private const string Pat = "feedPAT-plain-9d8c7b";

    [Fact]
    public async Task The_pat_is_stored_encrypted_and_decrypts_on_read()
    {
        SecretProtector.Configure("a-strong-test-key");
        try
        {
            using var db = new TestDb();
            var id = Guid.NewGuid();
            db.Context.Set<PackageFeed>().Add(new PackageFeed
            {
                Id = id,
                Name = "company",
                NormalizedName = "COMPANY",
                FeedUrl = "https://pkgs.dev.azure.com/example-org/_packaging/company",
                Pat = Pat,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var raw = ReadRawPat(db);
            Assert.NotNull(raw);
            Assert.NotEqual(Pat, raw);
            Assert.DoesNotContain(Pat, raw);

            using var fresh = db.Fresh();
            var loaded = await fresh.Set<PackageFeed>().FindAsync([id], TestContext.Current.CancellationToken);
            Assert.Equal(Pat, loaded!.Pat);
        }
        finally { SecretProtector.Configure(null); }
    }

    // Bypass the value converter to see what is physically stored in the column.
    private static string? ReadRawPat(TestDb db)
    {
        var entity = db.Context.Model.FindEntityType(typeof(PackageFeed))!;
        var column = entity.FindProperty(nameof(PackageFeed.Pat))!.GetColumnName();
        var conn = db.Context.Database.GetDbConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT \"{column}\" FROM \"{entity.GetTableName()}\" LIMIT 1";
        var value = cmd.ExecuteScalar();
        return value == null || value is DBNull ? null : (string)value;
    }
}
