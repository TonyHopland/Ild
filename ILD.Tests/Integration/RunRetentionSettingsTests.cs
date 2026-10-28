using System.Net.Http.Json;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// run.retentionDays: 14 on a fresh install, so a finished run lasts from one weekly
/// retro to the next; an integer from 0 to 3650, where 0 deletes it at the next cleanup.
/// </summary>
// A host per test: it reads the host-wide retention setting on a fresh install.
public class RunRetentionSettingsTests
{
    private const string Key = "run.retentionDays";

    private static async Task<string> StoredAsync(HttpClient client)
        => (await client.GetFromJsonAsync<JsonElement>($"/api/v1/settings/{Key}", TestContext.Current.CancellationToken))
            .GetProperty("value").GetString()!;

    [Fact]
    public async Task A_fresh_install_reports_14_days_and_the_sweeper_reads_14()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var all = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/settings", TestContext.Current.CancellationToken);
        var values = all!.ToDictionary(s => s.GetProperty("key").GetString()!, s => s.GetProperty("value").GetString());
        Assert.Equal("14", values[Key]);
        Assert.Equal("14", await StoredAsync(client));

        using var scope = factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISchedulerSettingsService>();
        Assert.Equal(14, await settings.GetRunRetentionDaysAsync(TestContext.Current.CancellationToken));
    }
}
