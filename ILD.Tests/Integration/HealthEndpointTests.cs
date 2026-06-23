using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using ILD.Api.Controllers;
using ILD.Data.DTOs;
using Xunit;

namespace ILD.Tests.Integration;

/// <summary>
/// Proves the ILD image surfaces the build-time informational version on
/// <c>/health</c>, which is what CI stamps to the release tag (docs/adr/0012).
/// </summary>
public sealed class HealthEndpointTests
{
    [Fact]
    public async Task Health_reports_the_stamped_informational_version()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/health");
        var body = await resp.Content.ReadFromJsonAsync<HealthResponse>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var expected = typeof(HealthController).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.NotNull(body);
        // /health must report the informational version (release stamp == tag),
        // not the numeric AssemblyVersion the props pin.
        Assert.Equal(expected, body!.Version);
        // A plain build carries no source-revision suffix, so a release stamp of
        // X.Y.Z surfaces exactly as the tag rather than X.Y.Z+<sha>.
        Assert.DoesNotContain("+", body.Version);
    }
}
