using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// run.retentionDays: 0 on a fresh install, where 0 means a finished run is deleted
/// at the next sweep once its work item is Done; an integer from 0 to 3650.
/// </summary>
public class RunRetentionSettingsTests
{
    private const string Key = "run.retentionDays";

    private static async Task<string> StoredAsync(HttpClient client)
        => (await client.GetFromJsonAsync<JsonElement>($"/api/v1/settings/{Key}", TestContext.Current.CancellationToken))
            .GetProperty("value").GetString()!;

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string value)
        => client.PutAsJsonAsync($"/api/v1/settings/{Key}", new { value }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_fresh_install_reports_0_days_and_the_sweeper_reads_0()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var all = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/settings", TestContext.Current.CancellationToken);
        var values = all!.ToDictionary(s => s.GetProperty("key").GetString()!, s => s.GetProperty("value").GetString());
        Assert.Equal("0", values[Key]);
        Assert.Equal("0", await StoredAsync(client));

        using var scope = factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISchedulerSettingsService>();
        Assert.Equal(0, await settings.GetRunRetentionDaysAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_days_take_0_to_3650_and_the_refusal_calls_0_delete_when_done()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        foreach (var valid in new[] { "3650", "30", "0" })
        {
            using var response = await PutAsync(client, valid);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(valid, await StoredAsync(client));
        }

        foreach (var invalid in new[] { "-1", "3651", "7.5", "" })
        {
            using var refused = await PutAsync(client, invalid);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var error = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("when done", error);
            Assert.DoesNotContain("disabled", error);
            Assert.Equal("0", await StoredAsync(client));
        }
    }
}
