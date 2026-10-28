using System.Net;
using ILD.Core.Services.Implementations.Network;

namespace ILD.Tests;

public sealed class EgressAddressRulesTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.255.255.255")]
    [InlineData("10.0.0.0")]
    [InlineData("10.255.255.255")]
    [InlineData("100.64.0.0")]
    [InlineData("100.127.255.255")]
    [InlineData("127.0.0.0")]
    [InlineData("127.255.255.255")]
    [InlineData("169.254.0.0")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.255.255")]
    [InlineData("172.16.0.0")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.0")]
    [InlineData("192.168.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fc00::")]
    [InlineData("fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("fe80::")]
    [InlineData("fe80::1")]
    [InlineData("febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("::ffff:0.0.0.0")]
    public void The_first_and_last_address_of_every_blocked_range_is_blocked(string address)
        => Assert.True(EgressAddressRules.IsBlocked(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.0")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.0")]
    [InlineData("11.0.0.0")]
    [InlineData("9.255.255.255")]
    [InlineData("126.255.255.255")]
    [InlineData("128.0.0.0")]
    [InlineData("1.0.0.0")]
    [InlineData("169.253.255.255")]
    [InlineData("169.255.0.0")]
    [InlineData("192.167.255.255")]
    [InlineData("192.169.0.0")]
    [InlineData("2606:4700::1111")]
    [InlineData("fbff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("fec0::")]
    [InlineData("::ffff:8.8.8.8")]
    public void Public_addresses_and_the_edges_just_outside_a_range_are_not_blocked(string address)
        => Assert.False(EgressAddressRules.IsBlocked(IPAddress.Parse(address)));

    [Theory]
    [InlineData(" Example.COM , 10.0.0.0/8\n10.0.0.0/8,\r\n\n", "example.com\n10.0.0.0/8")]
    [InlineData("192.168.1.5,fd00::/8\nexample.com\nEXAMPLE.com", "192.168.1.5\nfd00::/8\nexample.com")]
    [InlineData("", "")]
    [InlineData(" \n , \r\n", "")]
    public void Valid_input_is_split_trimmed_deduplicated_and_joined_with_newlines(string raw, string expected)
    {
        Assert.True(EgressAddressRules.TryNormalizeInternalAllow(raw, out var canonical, out var error), error);
        Assert.Equal(expected, canonical);
    }

    [Fact]
    public void A_wildcard_suffix_is_saved_the_way_the_rule_lists_save_it()
    {
        Assert.True(EgressRules.TryNormalizePattern("*.Lan.Example", out var listForm, out _));
        Assert.True(EgressAddressRules.TryNormalizeInternalAllow("*.Lan.Example", out var canonical, out _));
        Assert.Equal(listForm, canonical);
    }

    [Theory]
    [InlineData("http://lan.example/", "http://lan.example/")]
    [InlineData("lan.example:8080", "lan.example:8080")]
    [InlineData("10.0.0.1/8", "10.0.0.1/8")]
    [InlineData("10.0.0.0/33", "10.0.0.0/33")]
    [InlineData("fd00::/129", "fd00::/129")]
    [InlineData("ok.example\nnot a host!", "not a host!")]
    public void An_invalid_pattern_is_refused_and_named(string raw, string bad)
    {
        Assert.False(EgressAddressRules.TryNormalizeInternalAllow(raw, out _, out var error));
        Assert.Contains(bad, error);
    }

    [Fact]
    public void A_canonical_result_over_the_column_limit_is_refused()
    {
        var raw = string.Join('\n', Enumerable.Range(0, 400).Select(i => $"host{i}.example.com"));

        Assert.False(EgressAddressRules.TryNormalizeInternalAllow(raw, out _, out var error));
        Assert.Contains("4096", error);
    }

    [Theory]
    [InlineData("10.1.2.3", "db.lan.example", true)]
    [InlineData("10.1.2.3", "lan.example", true)]
    [InlineData("10.1.2.3", "db.lan.example.", true)]
    [InlineData("10.1.2.3", "notlan.example", false)]
    [InlineData("10.1.2.3", "wiki.example", true)]
    [InlineData("10.1.2.3", "other.example", false)]
    [InlineData("127.0.0.1", "wiki.example", true)]
    [InlineData("127.0.0.1", "other.example", false)]
    [InlineData("8.8.8.8", "other.example", true)]
    public void A_host_pattern_permits_every_address_its_name_resolves_to(string address, string host, bool permitted)
        => Assert.Equal(permitted, EgressAddressRules.Permits(new[] { ".lan.example", "wiki.example" }, host, IPAddress.Parse(address)));

    [Theory]
    [InlineData("10.200.0.1", true)]
    [InlineData("::ffff:10.1.2.3", true)]
    [InlineData("192.168.1.5", true)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("192.168.1.6", false)]
    [InlineData("fd00::1", true)]
    [InlineData("fd01::1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    public void An_address_pattern_permits_only_the_addresses_it_contains(string address, bool permitted)
        => Assert.Equal(permitted, EgressAddressRules.Permits(new[] { "10.0.0.0/8", "192.168.1.5", "fd00::/16" }, "unrelated.example", IPAddress.Parse(address)));

    [Fact]
    public void With_no_patterns_only_public_addresses_are_permitted()
    {
        Assert.True(EgressAddressRules.Permits(Array.Empty<string>(), "example.com", IPAddress.Parse("93.184.216.34")));
        Assert.False(EgressAddressRules.Permits(Array.Empty<string>(), "example.com", IPAddress.Parse("127.0.0.1")));
    }
}
