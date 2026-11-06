using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Core.Services.Implementations.Network;
using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Tests;

public class ModelGatewayRegistryTests
{
    private readonly ModelGatewayRegistry _registry = new();
    private readonly Guid _provider = Guid.NewGuid();

    [Fact]
    public void An_open_lease_is_active_and_bound_to_its_provider_and_owner()
    {
        var owner = Guid.NewGuid();
        using var lease = _registry.Open(_provider, owner, "http://127.0.0.1:8080/", ["api.example.test"]);

        Assert.Equal(GatewayTokenState.Active, _registry.Lookup(lease.Token, out var grant));
        Assert.Equal(new GatewayGrant(_provider, owner), grant);
        Assert.Equal("http://127.0.0.1:8080/gateway/anthropic", lease.UrlFor("anthropic"));
    }

    [Fact]
    public void A_disposed_lease_is_ended_and_an_invented_token_is_unknown()
    {
        var lease = _registry.Open(_provider, Guid.NewGuid(), "http://x", []);
        lease.Dispose();

        Assert.Equal(GatewayTokenState.Ended, _registry.Lookup(lease.Token, out var grant));
        Assert.Null(grant);
        Assert.Equal(GatewayTokenState.Unknown, _registry.Lookup("ildgw_nope", out _));
        Assert.Equal(GatewayTokenState.Unknown, _registry.Lookup(null, out _));
        Assert.Equal(GatewayTokenState.Unknown, _registry.Lookup("some-other-token", out _));
    }

    [Fact]
    public void The_github_shaped_placeholder_is_the_same_token()
    {
        using var lease = _registry.Open(_provider, Guid.NewGuid(), "http://x", []);

        Assert.StartsWith("gho_", lease.GitHubPlaceholder);
        Assert.Equal(GatewayTokenState.Active, _registry.Lookup(lease.GitHubPlaceholder, out _));
    }

    [Fact]
    public void Every_lease_has_its_own_token()
    {
        using var first = _registry.Open(_provider, Guid.NewGuid(), "http://x", []);
        using var second = _registry.Open(_provider, Guid.NewGuid(), "http://x", []);

        Assert.NotEqual(first.Token, second.Token);
    }

    [Fact]
    public void Model_hosts_are_blocked_only_for_the_leased_provider_and_only_while_the_lease_is_open()
    {
        var lease = _registry.Open(_provider, Guid.NewGuid(), "http://x", ["api.example.test", ".models.example.test"]);

        Assert.True(_registry.BlocksHost(_provider, "api.example.test"));
        Assert.True(_registry.BlocksHost(_provider, "API.example.test"));
        Assert.True(_registry.BlocksHost(_provider, "eu.models.example.test"));
        Assert.True(_registry.BlocksHost(_provider, "models.example.test"));
        Assert.False(_registry.BlocksHost(_provider, "other.example.test"));
        Assert.False(_registry.BlocksHost(Guid.NewGuid(), "api.example.test"));
        Assert.False(_registry.BlocksHost(null, "api.example.test"));

        lease.Dispose();
        Assert.False(_registry.BlocksHost(_provider, "api.example.test"));
    }

    [Fact]
    public void Ended_tokens_are_forgotten_after_an_hour()
    {
        var clock = new SteppedClock();
        var registry = new ModelGatewayRegistry(clock);
        var lease = registry.Open(_provider, Guid.NewGuid(), "http://x", []);
        lease.Dispose();

        clock.Now += TimeSpan.FromMinutes(61);
        registry.Open(_provider, Guid.NewGuid(), "http://x", []);

        Assert.Equal(GatewayTokenState.Unknown, registry.Lookup(lease.Token, out _));
    }

    [Theory]
    [InlineData(NetworkMode.Monitor)]
    [InlineData(NetworkMode.Whitelist)]
    [InlineData(NetworkMode.Blacklist)]
    [InlineData(NetworkMode.Off)]
    public void The_egress_policy_refuses_the_model_host_in_every_mode_while_the_gateway_is_in_use(NetworkMode mode)
    {
        var entry = new NetworkPolicyEntry { Host = "api.example.test", ListKind = NetworkListKind.Whitelist };
        using var lease = _registry.Open(_provider, Guid.NewGuid(), "http://x", ["api.example.test"]);
        var snapshot = new EgressPolicySnapshot(mode, [entry], _registry);

        Assert.Equal(NetworkDecision.Blocked, snapshot.Decide("api.example.test", _provider));
        Assert.NotEqual(NetworkDecision.Blocked, snapshot.Decide("api.example.test", Guid.NewGuid()));
    }

    private sealed class SteppedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
