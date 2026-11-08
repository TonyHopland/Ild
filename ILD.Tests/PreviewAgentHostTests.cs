using ILD.Api.Controllers;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

/// <summary>
/// The preview hostname agents are given, <c>wi-12.preview.ild</c>: an agent's egress proxy carries it to ILD, which
/// serves it as the preview it names. A <c>*.localhost</c> address is the agent's own machine in a chat's or run's
/// container, not ILD.
/// </summary>
public sealed class PreviewAgentHostTests
{
    private static readonly PreviewProxyBase Base = PreviewProxyBase.Parse("http://ild.localhost:8080");
    private static readonly EgressProxyOptions Egress = new(true, 3128);

    [Theory]
    [InlineData("wi-12.preview.ild", "wi-12")]
    [InlineData("wi-12-api.preview.ild", "wi-12-api")]
    [InlineData("WI-12.Preview.ILD", "WI-12")]
    public void The_proxy_serves_an_agents_preview_hostname_as_the_preview_it_names(string host, string expected)
    {
        Assert.True(Base.TryGetHostLabel(host, out var label));
        Assert.Equal(expected, label);
        Assert.True(PreviewProxyBase.IsAgentHost(host));
    }

    [Theory]
    [InlineData("preview.ild")]
    [InlineData("wi-12.preview.ild.example.com")]
    [InlineData("wi-12.notpreview.ild")]
    public void Anything_else_is_not_an_agents_preview_hostname(string host)
        => Assert.False(PreviewProxyBase.IsAgentHost(host));

    [Fact]
    public void With_previews_off_an_agents_preview_hostname_is_served_by_nothing()
        => Assert.False(PreviewProxyBase.Disabled.TryGetHostLabel("wi-12.preview.ild", out _));

    [Fact]
    public void An_agent_outside_the_run_gets_the_agents_hostname_for_a_proxied_preview()
    {
        Assert.Equal("http://wi-12.preview.ild/", AgentController.AgentPreviewUrl("http://wi-12.ild.localhost:8080", Base, Egress));
        Assert.Equal("http://wi-12-api.preview.ild/", AgentController.AgentPreviewUrl("http://wi-12-api.ild.localhost:8080", Base, Egress));
    }

    [Fact]
    public void An_authored_or_direct_url_or_one_with_no_egress_proxy_is_given_as_it_is()
    {
        Assert.Equal("https://staging.example.test/app", AgentController.AgentPreviewUrl("https://staging.example.test/app", Base, Egress));
        Assert.Equal("http://10.0.0.5:3000", AgentController.AgentPreviewUrl("http://10.0.0.5:3000", Base, Egress));
        Assert.Equal("http://wi-12.ild.localhost:8080", AgentController.AgentPreviewUrl("http://wi-12.ild.localhost:8080", Base, EgressProxyOptions.Disabled));
        Assert.Null(AgentController.AgentPreviewUrl(null, Base, Egress));
    }

    [Theory]
    [InlineData("http://+:8090", null, 8090)]
    [InlineData("http://+:8080;https://127.0.0.1:8442", null, 8080)]
    [InlineData("https://+:443;http://[::]:5000", null, 5000)]
    [InlineData("http://localhost", null, 80)]
    [InlineData(null, "8080", 8080)]
    [InlineData("", "8081;8082", 8081)]
    [InlineData("https://+:443", "8080", null)]
    [InlineData(null, null, null)]
    public void Ilds_web_port_is_read_as_aspnet_core_reads_it(string? urls, string? httpPorts, int? expected)
        => Assert.Equal(expected, EgressProxyOptions.ParseWebPort(urls, httpPorts));
}
