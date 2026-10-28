using ILD.Core.Services.Implementations.Network;

namespace ILD.Tests;

[Collection("ILD_API_URL")]
public sealed class EgressProxyOptionsTests
{
    private static EgressProxyOptions? WithApiUrl(string? value)
    {
        var previous = Environment.GetEnvironmentVariable("ILD_API_URL");
        try
        {
            Environment.SetEnvironmentVariable("ILD_API_URL", value);
            return EgressProxyOptions.FromEnvironment();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ILD_API_URL", previous);
        }
    }

    [Theory]
    [InlineData("http://localhost:8080", "localhost", 8080)]
    [InlineData("http://ILD.Internal.Test:9000/api/", "ild.internal.test", 9000)]
    [InlineData("https://ild.example", "ild.example", 443)]
    [InlineData("http://ild.example", "ild.example", 80)]
    [InlineData("http://[::1]:5000", "::1", 5000)]
    public void A_valid_api_url_names_the_exempt_host_and_port(string url, string host, int port)
        => Assert.Equal<(string, int)?>((host, port), WithApiUrl(url)!.ApiEndpoint);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("localhost:8080")]
    [InlineData("ftp://ild.example:21")]
    [InlineData("/relative/path")]
    public void An_unset_or_unusable_api_url_exempts_nothing(string? url)
        => Assert.Null(WithApiUrl(url)!.ApiEndpoint);
}
