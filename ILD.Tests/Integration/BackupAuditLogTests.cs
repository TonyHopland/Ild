using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ILD.Tests.Integration;

/// <summary>
/// Backup and restore are audit-logged with the acting username, and the
/// passphrase never reaches the log. ILD logs through the process-wide Serilog
/// logger, which the newest host installs, so this runs alone.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public class BackupAuditLogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Created_refused_and_succeeded_backups_are_logged_with_the_username_and_never_the_passphrase()
    {
        const string secret = "audit-test passphrase 9d41";
        await using var host = await BackupApiTests.Host.StartAsync();

        var backup = await host.CreateBackupAsync("workitems", secret);
        using var refused = await host.RestoreAsync("workitems", backup, "not the passphrase", "RESTORE");
        using var restored = await host.RestoreAsync("workitems", backup, secret, "RESTORE");
        using var download = await host.User.GetAsync("/api/v1/storage/backups/workitems/pre-restore", Ct);
        Assert.False(refused.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);

        using var entries = await host.User.GetAsync("/api/v1/logging/entries?take=500", Ct);
        var messages = (await entries.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray()
            .Select(e => e.GetProperty("message").GetString() ?? "").ToList();

        var audit = messages.Where(m => m.Contains("backup", StringComparison.OrdinalIgnoreCase)
            && m.Contains("admin", StringComparison.Ordinal)).ToList();
        Assert.Contains(audit, m => m.Contains("creat", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit, m => m.Contains("refus", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit, m => m.Contains("succe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit, m => m.Contains("pre-restore", StringComparison.OrdinalIgnoreCase));
        Assert.All(audit, m => Assert.Contains("workitems", m, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(messages, m => m.Contains(secret, StringComparison.Ordinal)
            || m.Contains("not the passphrase", StringComparison.Ordinal));
    }
}
