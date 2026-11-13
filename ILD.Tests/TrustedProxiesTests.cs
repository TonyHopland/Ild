using System.Net;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

public class TrustedProxiesTests
{
    private static readonly IPAddress Ingress = IPAddress.Parse("10.42.0.202");

    private static IPAddress? ClientOf(string? trusted, string remote, params string[] forwardedFor)
        => TrustedProxies.Parse(trusted).ClientOf(IPAddress.Parse(remote), forwardedFor);

    [Fact]
    public void With_no_trusted_proxy_the_header_is_ignored()
    {
        Assert.Equal(Ingress, ClientOf(null, "10.42.0.202", "203.0.113.7"));
    }

    [Fact]
    public void A_caller_that_is_not_a_trusted_proxy_cannot_name_its_own_address()
    {
        Assert.Equal(IPAddress.Parse("198.51.100.9"), ClientOf("10.42.0.0/16", "198.51.100.9", "203.0.113.7"));
    }

    [Fact]
    public void A_trusted_proxy_names_the_caller()
    {
        Assert.Equal(IPAddress.Parse("203.0.113.7"), ClientOf("10.42.0.202", "10.42.0.202", "203.0.113.7"));
        Assert.Equal(IPAddress.Parse("203.0.113.7"), ClientOf("10.42.0.0/16", "::ffff:10.42.0.202", "203.0.113.7"));
    }

    [Fact]
    public void The_nearest_address_that_is_not_a_trusted_proxy_is_the_caller()
    {
        Assert.Equal(
            IPAddress.Parse("203.0.113.7"),
            ClientOf("10.42.0.0/16", "10.42.0.202", "192.0.2.1, 203.0.113.7, 10.42.1.5"));
        Assert.Equal(
            IPAddress.Parse("203.0.113.7"),
            ClientOf("10.42.0.0/16", "10.42.0.202", "192.0.2.1", "203.0.113.7"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("10.42.1.5")]
    public void A_header_that_names_no_caller_leaves_the_proxys_own_address(string forwardedFor)
    {
        Assert.Equal(Ingress, ClientOf("10.42.0.0/16", "10.42.0.202", forwardedFor));
    }

    [Fact]
    public void An_entry_that_is_neither_an_address_nor_a_range_stops_the_parse()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TrustedProxies.Parse("10.42.0.0/16, ingress.example"));

        Assert.Contains("ingress.example", ex.Message);
        Assert.Contains(TrustedProxies.EnvVar, ex.Message);
    }
}
