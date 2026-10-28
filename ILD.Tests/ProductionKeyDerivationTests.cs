using System.Buffers.Binary;
using System.Text.Json;
using ILD.Core.Services.Backup;
using ILD.Core.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using static ILD.Tests.AuthServiceTests;

namespace ILD.Tests;

/// <summary>
/// The one place each production PBKDF2 strength is pinned, with the suite-wide lowered counts
/// switched off, so quietly weakening either one fails here.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public class ProductionKeyDerivationTests
{
    [Fact]
    public async Task A_backup_written_with_the_production_setting_records_600000_iterations()
    {
        var ct = TestContext.Current.CancellationToken;
        BackupCipher.IterationsOverride = null;
        try
        {
            var file = new MemoryStream();
            await using (var encrypting = await BackupCipher.OpenWriteAsync(file, "correct horse battery", "core", ct))
            {
                await encrypting.WriteAsync(new byte[] { 1, 2, 3 }, ct);
                await encrypting.CompleteAsync(ct);
            }

            var bytes = file.ToArray();
            const int magicLength = 7;
            var headerLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(magicLength));
            using var header = JsonDocument.Parse(bytes.AsMemory(magicLength + 4, headerLength));
            Assert.Equal(600000, header.RootElement.GetProperty("iterations").GetInt32());
        }
        finally { BackupCipher.IterationsOverride = FastKeyDerivation.Iterations; }
    }

    [Fact]
    public async Task A_password_hashed_with_the_production_setting_is_stored_with_100000_iterations()
    {
        AuthService.Pbkdf2IterationsOverride = null;
        try
        {
            using var db = new TestDb();
            var svc = Make(db);

            Assert.True((await svc.LoginAsync("admin", "secret")).Success);

            var hash = await db.Context.Users.Select(u => u.PasswordHash).SingleAsync(TestContext.Current.CancellationToken);
            Assert.StartsWith("100000.", hash);
        }
        finally { AuthService.Pbkdf2IterationsOverride = FastKeyDerivation.Iterations; }
    }
}
