using ILD.Core.Services.Implementations.PackageFeeds;

namespace ILD.Tests;

public class AzureFeedUrlTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://pkgs.dev.azure.com/example-org/_packaging/company")]
    [InlineData("https://example.com/example-org/_packaging/company")]
    [InlineData("http://example-org.pkgs.visualstudio.com/_packaging/company")]
    [InlineData("https://pkgs.visualstudio.com/_packaging/company")]
    [InlineData("https://team.example-org.pkgs.visualstudio.com/_packaging/company")]
    [InlineData("https://example-org.pkgs.visualstudio.com/_packaging")]
    [InlineData("https://example-org.pkgs.visualstudio.com/example-project/team/_packaging/company")]
    [InlineData("https://example-org.pkgs.visualstudio.com/_packaging/company/nuget/v3/index.json")]
    [InlineData("https://example-org.pkgs.visualstudio.com/_packaging/company?api-version=7.1")]
    [InlineData("https://pkgs.dev.azure.com/_packaging/company")]
    [InlineData("https://pkgs.dev.azure.com/example-org/_packaging")]
    [InlineData("https://pkgs.dev.azure.com/example-org/_packaging/")]
    [InlineData("https://pkgs.dev.azure.com/example-org/example-project/team/_packaging/company")]
    [InlineData("https://pkgs.dev.azure.com/example-org/_packaging/company/nuget/v3/index.json")]
    [InlineData("https://pkgs.dev.azure.com/example-org/_packaging/company/npm/registry/")]
    [InlineData("https://pkgs.dev.azure.com/example-org/_packaging/company//")]
    [InlineData("https://pkgs.dev.azure.com/example-org/_packaging/company?api-version=7.1")]
    [InlineData("https://pkgs.dev.azure.com/example-org/_packaging/company#top")]
    public void A_url_that_is_not_an_azure_artifacts_feed_is_refused_with_a_reason(string value)
    {
        Assert.False(AzureFeedUrl.TryParse(value, out var url, out var problem));
        Assert.Null(url);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Theory]
    [InlineData("https://pkgs.dev.azure.com/example-org/_packaging/company", "https://pkgs.dev.azure.com/example-org/_packaging/company")]
    [InlineData("https://pkgs.dev.azure.com/example-org/_packaging/company/", "https://pkgs.dev.azure.com/example-org/_packaging/company")]
    [InlineData("https://pkgs.dev.azure.com/example-org/example-project/_packaging/tools", "https://pkgs.dev.azure.com/example-org/example-project/_packaging/tools")]
    [InlineData("https://example-org.pkgs.visualstudio.com/_packaging/legacy/", "https://example-org.pkgs.visualstudio.com/_packaging/legacy")]
    [InlineData("https://example-org.pkgs.visualstudio.com/example-project/_packaging/legacy-tools", "https://example-org.pkgs.visualstudio.com/example-project/_packaging/legacy-tools")]
    public void A_feed_url_on_either_host_is_accepted_as_entered_without_its_trailing_slash(string value, string expected)
    {
        Assert.True(AzureFeedUrl.TryParse(value, out var url, out var problem), problem);
        Assert.Equal(expected, url!.Url);
        Assert.Null(problem);
    }
}
