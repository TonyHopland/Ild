using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

/// <summary>
/// <see cref="PreviewProxyBase"/> decides which requests the preview proxy is
/// allowed to touch. The apex-host case is the one that matters most: if the host
/// serving the ILD UI ever matched, the whole app would be routed into somebody's
/// half-built worktree.
/// </summary>
public class PreviewProxyBaseTests
{
    [Fact]
    public void An_unset_value_disables_the_proxy_entirely()
    {
        var proxyBase = PreviewProxyBase.Parse(null);

        Assert.False(proxyBase.Enabled);
        Assert.Null(proxyBase.ConfigurationError);
        Assert.False(proxyBase.TryGetHostLabel("wi-7.ild.kube", out _));
    }

    [Theory]
    [InlineData("http://ild.kube", "http", "ild.kube", null)]
    [InlineData("http://ild.localhost:8080", "http", "ild.localhost", 8080)]
    [InlineData("https://ild.example.com", "https", "ild.example.com", null)]
    [InlineData("https://ild.example.com:443", "https", "ild.example.com", null)]
    [InlineData("  ild.kube:8080  ", "http", "ild.kube", 8080)]
    public void Scheme_host_and_optional_port_are_parsed(string value, string scheme, string host, int? port)
    {
        var proxyBase = PreviewProxyBase.Parse(value);

        Assert.True(proxyBase.Enabled);
        Assert.Equal(scheme, proxyBase.Scheme);
        Assert.Equal(host, proxyBase.Host);
        Assert.Equal(port, proxyBase.Port);
    }

    [Theory]
    [InlineData("ftp://ild.kube")]
    [InlineData("http://")]
    [InlineData(":::")]
    public void An_unusable_value_disables_the_proxy_and_says_why(string value)
    {
        var proxyBase = PreviewProxyBase.Parse(value);

        Assert.False(proxyBase.Enabled);
        Assert.NotNull(proxyBase.ConfigurationError);
    }

    [Theory]
    [InlineData("wi-7.ild.kube", "wi-7")]
    [InlineData("wi-7-api.ild.kube", "wi-7-api")]
    [InlineData("WI-7.ILD.KUBE", "WI-7")]
    public void A_subdomain_of_the_base_yields_its_label(string requestHost, string expected)
    {
        var proxyBase = PreviewProxyBase.Parse("http://ild.kube");

        Assert.True(proxyBase.TryGetHostLabel(requestHost, out var label));
        Assert.Equal(expected, label);
    }

    [Theory]
    [InlineData("ild.kube")]        // the apex host: this is the ILD UI, never a preview
    [InlineData(".ild.kube")]       // empty label
    [InlineData("notild.kube")]     // suffix match without the dot separator
    [InlineData("ild.kube.evil.io")]
    [InlineData("localhost")]
    [InlineData("")]
    public void Everything_else_is_left_to_the_rest_of_the_pipeline(string requestHost)
    {
        var proxyBase = PreviewProxyBase.Parse("http://ild.kube");

        Assert.False(proxyBase.TryGetHostLabel(requestHost, out _));
    }

    [Theory]
    [InlineData("http://ild.kube", "http://wi-7.ild.kube")]
    [InlineData("http://ild.localhost:8080", "http://wi-7.ild.localhost:8080")]
    [InlineData("https://ild.example.com", "https://wi-7.ild.example.com")]
    public void Advertised_urls_are_built_from_the_base(string value, string expected)
    {
        Assert.Equal(expected, PreviewProxyBase.Parse(value).BuildUrl("wi-7"));
    }
}
