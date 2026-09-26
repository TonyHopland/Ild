using ILD.Core.Services.Implementations.Network;
using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Tests;

public class EgressRulesTests
{
    private static readonly Guid ProviderA = Guid.NewGuid();
    private static readonly Guid ProviderB = Guid.NewGuid();

    private static NetworkPolicyEntry Entry(string host, NetworkListKind kind, Guid? provider = null)
        => new() { Id = Guid.NewGuid(), Host = host, ListKind = kind, AiProviderId = provider };

    [Theory]
    [InlineData("api.example.com", "api.example.com", true)]
    [InlineData("api.example.com", "API.EXAMPLE.COM.", true)]
    [InlineData("api.example.com", "www.example.com", false)]
    [InlineData("api.example.com", "xapi.example.com", false)]
    [InlineData(".example.com", "example.com", true)]
    [InlineData(".example.com", "api.example.com", true)]
    [InlineData(".example.com", "deep.api.example.com", true)]
    [InlineData(".example.com", "notexample.com", false)]
    [InlineData(".example.com", "example.com.evil.net", false)]
    [InlineData("10.0.0.5", "10.0.0.5", true)]
    [InlineData("10.0.0.5", "10.0.0.50", false)]
    public void An_exact_pattern_matches_one_host_and_a_dotted_one_matches_the_domain_and_beneath(string pattern, string host, bool expected)
    {
        Assert.Equal(expected, EgressRules.Matches(pattern, EgressRules.NormalizeHost(host)));
    }

    [Fact]
    public void Off_mode_records_without_judging()
    {
        var entries = new[] { Entry("api.example.com", NetworkListKind.Blacklist) };
        Assert.Equal(NetworkDecision.Advisory, EgressRules.Decide(NetworkMode.Off, entries, "api.example.com", null));
    }

    [Fact]
    public void Whitelist_mode_allows_only_listed_hosts()
    {
        var entries = new[] { Entry(".github.com", NetworkListKind.Whitelist), Entry("api.anthropic.com", NetworkListKind.Whitelist) };

        Assert.Equal(NetworkDecision.Allowed, EgressRules.Decide(NetworkMode.Whitelist, entries, "api.github.com", null));
        Assert.Equal(NetworkDecision.Allowed, EgressRules.Decide(NetworkMode.Whitelist, entries, "api.anthropic.com", null));
        Assert.Equal(NetworkDecision.Blocked, EgressRules.Decide(NetworkMode.Whitelist, entries, "registry.npmjs.org", null));
    }

    [Fact]
    public void Blacklist_mode_blocks_only_listed_hosts()
    {
        var entries = new[] { Entry(".evil.net", NetworkListKind.Blacklist) };

        Assert.Equal(NetworkDecision.Blocked, EgressRules.Decide(NetworkMode.Blacklist, entries, "cdn.evil.net", null));
        Assert.Equal(NetworkDecision.Allowed, EgressRules.Decide(NetworkMode.Blacklist, entries, "api.github.com", null));
    }

    [Fact]
    public void Entries_of_the_other_list_are_ignored()
    {
        // A blacklist entry does not become an allow when the mode is whitelist.
        var entries = new[] { Entry("api.example.com", NetworkListKind.Blacklist) };
        Assert.Equal(NetworkDecision.Blocked, EgressRules.Decide(NetworkMode.Whitelist, entries, "api.example.com", null));

        var allow = new[] { Entry("api.example.com", NetworkListKind.Whitelist) };
        Assert.Equal(NetworkDecision.Allowed, EgressRules.Decide(NetworkMode.Blacklist, allow, "api.example.com", null));
    }

    [Fact]
    public void A_provider_scoped_entry_applies_only_to_that_provider()
    {
        var entries = new[] { Entry("api.example.com", NetworkListKind.Whitelist, ProviderA) };

        Assert.Equal(NetworkDecision.Allowed, EgressRules.Decide(NetworkMode.Whitelist, entries, "api.example.com", ProviderA));
        Assert.Equal(NetworkDecision.Blocked, EgressRules.Decide(NetworkMode.Whitelist, entries, "api.example.com", ProviderB));
        // A connection nobody claimed sees only the global entries.
        Assert.Equal(NetworkDecision.Blocked, EgressRules.Decide(NetworkMode.Whitelist, entries, "api.example.com", null));
    }

    [Fact]
    public void A_global_entry_applies_to_every_provider()
    {
        var entries = new[] { Entry("api.example.com", NetworkListKind.Blacklist) };

        Assert.Equal(NetworkDecision.Blocked, EgressRules.Decide(NetworkMode.Blacklist, entries, "api.example.com", ProviderA));
        Assert.Equal(NetworkDecision.Blocked, EgressRules.Decide(NetworkMode.Blacklist, entries, "api.example.com", null));
    }

    [Theory]
    [InlineData("api.example.com", "api.example.com")]
    [InlineData("  API.Example.COM. ", "api.example.com")]
    [InlineData(".example.com", ".example.com")]
    [InlineData("*.example.com", ".example.com")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    [InlineData("[::1]", "::1")]
    [InlineData("under_score.example.com", "under_score.example.com")]
    public void Patterns_are_canonicalised_on_the_way_in(string input, string expected)
    {
        Assert.True(EgressRules.TryNormalizePattern(input, out var pattern, out var error), error);
        Assert.Equal(expected, pattern);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://api.example.com")]
    [InlineData("api.example.com/path")]
    [InlineData(".")]
    [InlineData("*.")]
    [InlineData("api..example.com")]
    [InlineData("-bad.example.com")]
    [InlineData("api.example.com:443")]
    [InlineData("has space.example.com")]
    [InlineData(".10.0.0.5")]
    public void Anything_that_is_not_a_host_is_refused_with_a_reason(string input)
    {
        Assert.False(EgressRules.TryNormalizePattern(input, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("postgres", "postgres")]
    [InlineData(" POSTGRES.internal. ", "postgres.internal")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    [InlineData("[::1]", "::1")]
    public void A_forward_destination_is_canonicalised_like_any_other_host(string input, string expected)
    {
        Assert.True(EgressRules.TryNormalizeForwardHost(input, out var host, out _));
        Assert.Equal(expected, host);
    }

    /// <summary>
    /// A leading-dot or wildcard form is a set of hosts. It is meaningful on a
    /// list and meaningless as somewhere to open a socket, so the forward form
    /// refuses what <see cref="EgressRules.TryNormalizePattern"/> accepts.
    /// </summary>
    [Theory]
    [InlineData(".example.com")]
    [InlineData("*.example.com")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://postgres")]
    [InlineData("postgres/db")]
    [InlineData("postgres:5432")]
    [InlineData("has space.internal")]
    [InlineData("-leading.example.com")]
    public void A_forward_destination_must_be_one_concrete_host(string input)
    {
        Assert.False(EgressRules.TryNormalizeForwardHost(input, out var host, out var error));
        Assert.Empty(host);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void A_pattern_the_lists_accept_is_still_refused_as_a_forward_destination()
    {
        Assert.True(EgressRules.TryNormalizePattern("*.example.com", out var pattern, out _));
        Assert.Equal(".example.com", pattern);
        Assert.False(EgressRules.TryNormalizeForwardHost("*.example.com", out _, out _));
    }

    [Theory]
    [InlineData("off", true, NetworkMode.Off)]
    [InlineData("Whitelist", true, NetworkMode.Whitelist)]
    [InlineData(" blacklist ", true, NetworkMode.Blacklist)]
    [InlineData("allow", false, NetworkMode.Off)]
    [InlineData(null, false, NetworkMode.Off)]
    public void The_mode_setting_parses_its_three_values_only(string? value, bool ok, NetworkMode expected)
    {
        Assert.Equal(ok, EgressRules.TryParseMode(value, out var mode));
        Assert.Equal(expected, mode);
    }
}
