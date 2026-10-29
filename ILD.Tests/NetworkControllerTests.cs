using ILD.Api.Controllers;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

public sealed class NetworkControllerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Mock<IEgressPolicy> _policy = new();
    private readonly Mock<INetworkNotifier> _notifier = new();
    private readonly StubForwarderState _forwarder = new();
    private EgressPolicySnapshot _snapshot = EgressPolicySnapshot.Monitor;

    public NetworkControllerTests()
        => _policy.Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(_snapshot));

    public void Dispose() => _db.Dispose();

    private NetworkController Build(NetworkEnforcementStatus? status = null)
        => new(_db.Network, _db.NetworkForwards, _db.Providers, _policy.Object, _notifier.Object,
            status ?? NetworkEnforcementStatus.Resolve(EgressProxyOptions.Parse("3128"), "enforced", null),
            _forwarder);

    private sealed class StubForwarderState : IEgressForwarderState
    {
        public Dictionary<Guid, string> Errors { get; } = new();

        public string? ListenErrorFor(Guid forwardId) => Errors.GetValueOrDefault(forwardId);
    }

    private static T Body<T>(IActionResult result)
    {
        var value = Assert.IsAssignableFrom<ObjectResult>(result).Value!;
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        return System.Text.Json.JsonSerializer.Deserialize<T>(json, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        })!;
    }

    private sealed record EntryView(Guid Id, string Host, NetworkListKind ListKind, Guid? AiProviderId);

    [Fact]
    public async Task Adding_an_entry_canonicalises_it_invalidates_the_proxy_and_broadcasts()
    {
        var controller = Build();

        var result = await controller.AddEntry(new NetworkController.AddEntryRequest { Host = " *.GitHub.com ", ListKind = NetworkListKind.Whitelist }, TestContext.Current.CancellationToken);

        var created = Assert.IsType<CreatedAtActionResult>(result);
        var view = Body<EntryView>(created);
        Assert.Equal(".github.com", view.Host);
        Assert.Equal(NetworkListKind.Whitelist, view.ListKind);
        Assert.Null(view.AiProviderId);
        Assert.Equal(".github.com", Assert.Single(await _db.Network.GetEntriesAsync(TestContext.Current.CancellationToken)).Host);
        _policy.Verify(p => p.Invalidate(), Times.Once);
        _notifier.Verify(n => n.PolicyChangedAsync(), Times.Once);
    }

    [Fact]
    public async Task Adding_an_entry_that_exists_answers_with_the_existing_row_and_changes_nothing()
    {
        var controller = Build();
        var first = Body<EntryView>(Assert.IsType<CreatedAtActionResult>(
            await controller.AddEntry(new NetworkController.AddEntryRequest { Host = "api.example.com", ListKind = NetworkListKind.Blacklist }, TestContext.Current.CancellationToken)));

        var again = Body<EntryView>(Assert.IsType<OkObjectResult>(
            await controller.AddEntry(new NetworkController.AddEntryRequest { Host = "API.example.com." , ListKind = NetworkListKind.Blacklist }, TestContext.Current.CancellationToken)));

        Assert.Equal(first.Id, again.Id);
        Assert.Single(await _db.Network.GetEntriesAsync(TestContext.Current.CancellationToken));
        _policy.Verify(p => p.Invalidate(), Times.Once);
    }

    [Fact]
    public async Task The_unique_index_refuses_a_duplicate_scoped_entry_inserted_behind_the_controllers_back()
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "claude", Type = "claude-code", BaseUrl = "", Model = "" };
        await _db.Providers.CreateAiProviderAsync(provider);
        await _db.Network.AddEntryAsync(new NetworkPolicyEntry { Host = "api.example.com", ListKind = NetworkListKind.Whitelist, AiProviderId = provider.Id }, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.Network.AddEntryAsync(
            new NetworkPolicyEntry { Host = "api.example.com", ListKind = NetworkListKind.Whitelist, AiProviderId = provider.Id }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_undefined_list_kind_is_refused_before_anything_is_stored()
    {
        var controller = Build();

        var result = await controller.AddEntry(new NetworkController.AddEntryRequest { Host = "api.example.com", ListKind = (NetworkListKind)7 }, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await _db.Network.GetEntriesAsync(TestContext.Current.CancellationToken));
        _policy.Verify(p => p.Invalidate(), Times.Never);
    }

    [Fact]
    public async Task A_url_is_refused_with_the_reason_and_nothing_changes()
    {
        var controller = Build();

        var result = await controller.AddEntry(new NetworkController.AddEntryRequest { Host = "https://github.com", ListKind = NetworkListKind.Blacklist }, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await _db.Network.GetEntriesAsync(TestContext.Current.CancellationToken));
        _policy.Verify(p => p.Invalidate(), Times.Never);
    }

    [Fact]
    public async Task A_scope_to_an_unknown_provider_is_refused()
    {
        var controller = Build();

        var result = await controller.AddEntry(new NetworkController.AddEntryRequest { Host = "api.example.com", ListKind = NetworkListKind.Whitelist, AiProviderId = Guid.NewGuid() }, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Deleting_an_entry_invalidates_the_proxy()
    {
        var entry = new NetworkPolicyEntry { Host = "api.example.com", ListKind = NetworkListKind.Blacklist };
        await _db.Network.AddEntryAsync(entry, TestContext.Current.CancellationToken);
        var controller = Build();

        Assert.IsType<NoContentResult>(await controller.DeleteEntry(entry.Id, TestContext.Current.CancellationToken));
        Assert.IsType<NotFoundResult>(await controller.DeleteEntry(entry.Id, TestContext.Current.CancellationToken));

        Assert.Empty(await _db.Network.GetEntriesAsync(TestContext.Current.CancellationToken));
        _policy.Verify(p => p.Invalidate(), Times.Once);
    }

    [Fact]
    public async Task A_log_entry_can_be_promoted_to_either_list_globally_or_scoped_to_its_provider()
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "claude", Type = "claude-code", BaseUrl = "", Model = "" };
        await _db.Providers.CreateAiProviderAsync(provider);
        var logged = new NetworkLogEntry { Host = "registry.npmjs.org", Port = 443, Timestamp = DateTime.UtcNow, Decision = NetworkDecision.Blocked, AiProviderId = provider.Id };
        await _db.Network.AppendLogAsync(new[] { logged }, TestContext.Current.CancellationToken);
        var controller = Build();

        var global = Body<EntryView>(Assert.IsType<CreatedAtActionResult>(await controller.WhitelistFromLog(logged.Id, null, TestContext.Current.CancellationToken)));
        var scoped = Body<EntryView>(Assert.IsType<CreatedAtActionResult>(
            await controller.BlacklistFromLog(logged.Id, new NetworkController.AddFromLogRequest { ScopeToProvider = true }, TestContext.Current.CancellationToken)));

        Assert.Equal(("registry.npmjs.org", NetworkListKind.Whitelist, (Guid?)null), (global.Host, global.ListKind, global.AiProviderId));
        Assert.Equal(("registry.npmjs.org", NetworkListKind.Blacklist, (Guid?)provider.Id), (scoped.Host, scoped.ListKind, scoped.AiProviderId));

        // Promoting the same line twice does not duplicate the entry.
        Assert.IsType<OkObjectResult>(await controller.WhitelistFromLog(logged.Id, null, TestContext.Current.CancellationToken));
        Assert.Equal(2, (await _db.Network.GetEntriesAsync(TestContext.Current.CancellationToken)).Count);

        Assert.IsType<NotFoundResult>(await controller.WhitelistFromLog(Guid.NewGuid(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Clearing_the_log_empties_it_and_broadcasts()
    {
        await _db.Network.AppendLogAsync(new[]
        {
            new NetworkLogEntry { Host = "a.example", Port = 443, Timestamp = DateTime.UtcNow, Decision = NetworkDecision.Advisory },
            new NetworkLogEntry { Host = "b.example", Port = 80, Timestamp = DateTime.UtcNow, Decision = NetworkDecision.Advisory },
        }, TestContext.Current.CancellationToken);
        var controller = Build();

        Assert.Equal(2, Body<Dictionary<string, int>>(await controller.ClearLog(TestContext.Current.CancellationToken))["removed"]);

        Assert.Empty(await _db.Network.GetLogAsync(10, TestContext.Current.CancellationToken));
        _notifier.Verify(n => n.LogClearedAsync(), Times.Once);
    }

    [Fact]
    public async Task The_log_is_read_newest_first_and_capped()
    {
        var now = DateTime.UtcNow;
        await _db.Network.AppendLogAsync(Enumerable.Range(0, 5).Select(i => new NetworkLogEntry
        {
            Host = $"h{i}.example", Port = 443, Timestamp = now.AddSeconds(i), Decision = NetworkDecision.Allowed,
        }).ToList(), TestContext.Current.CancellationToken);
        var controller = Build();

        var page = Body<List<Dictionary<string, object>>>(await controller.GetLog(take: 2, ct: TestContext.Current.CancellationToken));

        Assert.Equal(new[] { "h4.example", "h3.example" }, page.Select(e => e["host"].ToString()));
    }

    private sealed record ForwardView(
        Guid Id, string Name, string Host, int Port, int LocalPort, NetworkDecision Decision, string? ListenError);

    private static NetworkController.AddForwardRequest Forward(
        string name = "postgres", string host = "postgres", int port = 5432, int localPort = 15432)
        => new() { Name = name, Host = host, Port = port, LocalPort = localPort };

    [Fact]
    public async Task Adding_a_forward_canonicalises_its_host_invalidates_the_policy_and_broadcasts()
    {
        var controller = Build();

        var created = Assert.IsType<CreatedAtActionResult>(
            await controller.AddForward(Forward(name: " postgres ", host: " POSTGRES. "), TestContext.Current.CancellationToken));

        var view = Body<ForwardView>(created);
        Assert.Equal(("postgres", "postgres", 5432, 15432), (view.Name, view.Host, view.Port, view.LocalPort));
        Assert.Equal(NetworkDecision.Advisory, view.Decision);
        Assert.Null(view.ListenError);
        _policy.Verify(p => p.Invalidate(), Times.Once);
        _notifier.Verify(n => n.PolicyChangedAsync(), Times.Once);
    }

    [Theory]
    [InlineData(".example.com")]
    [InlineData("*.example.com")]
    public async Task A_pattern_is_not_a_destination_and_is_refused(string host)
    {
        var controller = Build();

        var result = await controller.AddForward(Forward(host: host), TestContext.Current.CancellationToken);

        Assert.Contains("not a pattern", Body<Dictionary<string, string>>(result)["error"]);
        Assert.Empty(await _db.NetworkForwards.GetForwardsAsync(TestContext.Current.CancellationToken));
        _policy.Verify(p => p.Invalidate(), Times.Never);
    }

    [Theory]
    [InlineData("", "postgres", 5432, 15432)]
    [InlineData("postgres", "https://postgres", 5432, 15432)]
    [InlineData("postgres", "postgres:5432", 5432, 15432)]
    [InlineData("postgres", "postgres", 0, 15432)]
    [InlineData("postgres", "postgres", 70000, 15432)]
    [InlineData("postgres", "postgres", 5432, 0)]
    [InlineData("postgres", "postgres", 5432, 65536)]
    public async Task An_unusable_forward_is_refused_before_anything_is_stored(string name, string host, int port, int localPort)
    {
        var controller = Build();

        Assert.IsType<BadRequestObjectResult>(await controller.AddForward(Forward(name, host, port, localPort), TestContext.Current.CancellationToken));

        Assert.Empty(await _db.NetworkForwards.GetForwardsAsync(TestContext.Current.CancellationToken));
        _policy.Verify(p => p.Invalidate(), Times.Never);
    }

    [Fact]
    public async Task A_local_port_already_forwarded_is_refused_with_what_holds_it()
    {
        var controller = Build();
        await controller.AddForward(Forward(), TestContext.Current.CancellationToken);

        var result = await controller.AddForward(Forward(name: "redis", host: "cache", port: 6379), TestContext.Current.CancellationToken);

        Assert.Contains("postgres:5432", Body<Dictionary<string, string>>(result)["error"]);
        Assert.Single(await _db.NetworkForwards.GetForwardsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_unique_index_refuses_a_second_forward_on_a_local_port_behind_the_controllers_back()
    {
        await _db.NetworkForwards.AddForwardAsync(new NetworkForwardEntry { Name = "a", Host = "a.example", Port = 1, LocalPort = 15432 }, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.NetworkForwards.AddForwardAsync(
            new NetworkForwardEntry { Name = "b", Host = "b.example", Port = 2, LocalPort = 15432 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_forward_reads_back_with_the_current_verdict_on_its_host_and_its_listener_state()
    {
        var controller = Build();
        var created = Body<ForwardView>(Assert.IsType<CreatedAtActionResult>(await controller.AddForward(Forward(), TestContext.Current.CancellationToken)));
        _forwarder.Errors[created.Id] = "Local port 15432 is already in use by something else";
        _snapshot = new EgressPolicySnapshot(NetworkMode.Whitelist, Array.Empty<NetworkPolicyEntry>());

        var blocked = Assert.Single(Body<List<ForwardView>>(await controller.GetForwards(TestContext.Current.CancellationToken)));
        Assert.Equal(NetworkDecision.Blocked, blocked.Decision);
        Assert.Contains("already in use", blocked.ListenError);

        _snapshot = new EgressPolicySnapshot(NetworkMode.Whitelist, new[]
        {
            new NetworkPolicyEntry { Host = "postgres", ListKind = NetworkListKind.Whitelist },
        });

        Assert.Equal(NetworkDecision.Allowed, Assert.Single(Body<List<ForwardView>>(await controller.GetForwards(TestContext.Current.CancellationToken))).Decision);
    }

    private async Task<ForwardView> SeedForwardAsync(NetworkController controller, string name = "postgres", string host = "postgres", int port = 5432, int localPort = 15432)
    {
        var created = Body<ForwardView>(Assert.IsType<CreatedAtActionResult>(
            await controller.AddForward(Forward(name, host, port, localPort), TestContext.Current.CancellationToken)));
        _policy.Invocations.Clear();
        _notifier.Invocations.Clear();
        return created;
    }

    private async Task<NetworkForwardEntry> StoredAsync(Guid id)
        => Assert.Single(await _db.NetworkForwards.GetForwardsAsync(TestContext.Current.CancellationToken), f => f.Id == id);

    [Fact]
    public async Task Updating_a_forward_changes_all_four_fields_keeps_its_identity_and_invalidates_the_policy_once()
    {
        var controller = Build();
        var seeded = await SeedForwardAsync(controller);
        var before = await StoredAsync(seeded.Id);

        var result = await controller.UpdateForward(
            seeded.Id, Forward(name: " cache ", host: " REDIS.Example. ", port: 6379, localPort: 16379), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        var view = Body<ForwardView>(result);
        Assert.Equal((seeded.Id, "cache", "redis.example", 6379, 16379), (view.Id, view.Name, view.Host, view.Port, view.LocalPort));
        Assert.Equal(NetworkDecision.Advisory, view.Decision);
        var stored = await StoredAsync(seeded.Id);
        Assert.Equal(("cache", "redis.example", 6379, 16379), (stored.Name, stored.Host, stored.Port, stored.LocalPort));
        Assert.Equal(before.CreatedAt, stored.CreatedAt);
        _policy.Verify(p => p.Invalidate(), Times.Once);
        _notifier.Verify(n => n.PolicyChangedAsync(), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_forward_keeps_its_own_local_port_when_saved_unchanged_or_with_only_its_name_changed(bool renameOnly)
    {
        var controller = Build();
        var seeded = await SeedForwardAsync(controller);

        var result = await controller.UpdateForward(
            seeded.Id, Forward(name: renameOnly ? "primary" : "postgres"), TestContext.Current.CancellationToken);

        var view = Body<ForwardView>(Assert.IsType<OkObjectResult>(result));
        Assert.Equal(15432, view.LocalPort);
        Assert.Equal(renameOnly ? "primary" : "postgres", (await StoredAsync(seeded.Id)).Name);
        Assert.Equal(15432, (await StoredAsync(seeded.Id)).LocalPort);
    }

    [Fact]
    public async Task Moving_a_forward_onto_another_forwards_local_port_is_refused_and_both_stay_as_they_were()
    {
        var controller = Build();
        var mine = await SeedForwardAsync(controller, name: "redis", host: "cache", port: 6379, localPort: 16379);
        var other = await SeedForwardAsync(controller);

        var result = await controller.UpdateForward(mine.Id, Forward(name: "renamed", host: "other", port: 1, localPort: 15432), TestContext.Current.CancellationToken);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Local port 15432 already forwards to postgres:5432", Body<Dictionary<string, string>>(bad)["error"]);
        var storedMine = await StoredAsync(mine.Id);
        Assert.Equal(("redis", "cache", 6379, 16379), (storedMine.Name, storedMine.Host, storedMine.Port, storedMine.LocalPort));
        var storedOther = await StoredAsync(other.Id);
        Assert.Equal(("postgres", "postgres", 5432, 15432), (storedOther.Name, storedOther.Host, storedOther.Port, storedOther.LocalPort));
        _policy.Verify(p => p.Invalidate(), Times.Never);
        _notifier.Verify(n => n.PolicyChangedAsync(), Times.Never);
    }

    [Fact]
    public async Task Updating_an_unknown_forward_is_not_found_and_creates_nothing()
    {
        var controller = Build();

        var result = await controller.UpdateForward(Guid.NewGuid(), Forward(), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(await _db.NetworkForwards.GetForwardsAsync(TestContext.Current.CancellationToken));
        _policy.Verify(p => p.Invalidate(), Times.Never);
        _notifier.Verify(n => n.PolicyChangedAsync(), Times.Never);
    }

    [Theory]
    [InlineData("", "postgres", 5432, 15432)]
    [InlineData("   ", "postgres", 5432, 15432)]
    [InlineData("postgres", "https://postgres", 5432, 15432)]
    [InlineData("postgres", "postgres:5432", 5432, 15432)]
    [InlineData("postgres", ".example.com", 5432, 15432)]
    [InlineData("postgres", "*.example.com", 5432, 15432)]
    [InlineData("postgres", "postgres", 0, 15432)]
    [InlineData("postgres", "postgres", 70000, 15432)]
    [InlineData("postgres", "postgres", 5432, 0)]
    [InlineData("postgres", "postgres", 5432, 65536)]
    public async Task An_unusable_update_is_refused_and_the_forward_stays_as_it_was(string name, string host, int port, int localPort)
    {
        var controller = Build();
        var seeded = await SeedForwardAsync(controller);

        var result = await controller.UpdateForward(seeded.Id, Forward(name, host, port, localPort), TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(Body<Dictionary<string, string>>(Assert.IsType<BadRequestObjectResult>(result))["error"]));
        var stored = await StoredAsync(seeded.Id);
        Assert.Equal(("postgres", "postgres", 5432, 15432), (stored.Name, stored.Host, stored.Port, stored.LocalPort));
        _policy.Verify(p => p.Invalidate(), Times.Never);
        _notifier.Verify(n => n.PolicyChangedAsync(), Times.Never);
    }

    [Fact]
    public async Task A_name_over_the_limit_after_trimming_is_refused_on_update_but_a_padded_one_at_the_limit_is_kept()
    {
        var controller = Build();
        var seeded = await SeedForwardAsync(controller);

        Assert.IsType<BadRequestObjectResult>(await controller.UpdateForward(seeded.Id, Forward(name: new string('n', 129)), TestContext.Current.CancellationToken));
        Assert.Equal("postgres", (await StoredAsync(seeded.Id)).Name);

        Assert.IsType<OkObjectResult>(await controller.UpdateForward(seeded.Id, Forward(name: " " + new string('n', 128) + " "), TestContext.Current.CancellationToken));
        Assert.Equal(new string('n', 128), (await StoredAsync(seeded.Id)).Name);
    }

    [Fact]
    public async Task The_unique_index_refuses_moving_a_forward_onto_another_local_port_behind_the_controllers_back()
    {
        var ct = TestContext.Current.CancellationToken;
        await _db.NetworkForwards.AddForwardAsync(new NetworkForwardEntry { Name = "a", Host = "a.example", Port = 1, LocalPort = 15432 }, ct);
        var b = new NetworkForwardEntry { Name = "b", Host = "b.example", Port = 2, LocalPort = 15433 };
        await _db.NetworkForwards.AddForwardAsync(b, ct);

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.NetworkForwards.UpdateForwardAsync(b.Id, "b", "b.example", 2, 15432, ct));
    }

    [Fact]
    public async Task Deleting_a_forward_invalidates_the_policy()
    {
        var controller = Build();
        var created = Body<ForwardView>(Assert.IsType<CreatedAtActionResult>(await controller.AddForward(Forward(), TestContext.Current.CancellationToken)));
        _policy.Invocations.Clear();

        Assert.IsType<NoContentResult>(await controller.DeleteForward(created.Id, TestContext.Current.CancellationToken));
        Assert.IsType<NotFoundResult>(await controller.DeleteForward(created.Id, TestContext.Current.CancellationToken));

        Assert.Empty(await _db.NetworkForwards.GetForwardsAsync(TestContext.Current.CancellationToken));
        _policy.Verify(p => p.Invalidate(), Times.Once);
    }

    [Fact]
    public void Status_reports_the_enforcement_the_entrypoint_announced()
    {
        var advisory = Build(NetworkEnforcementStatus.Resolve(EgressProxyOptions.Parse("3128"), "advisory", "NET_ADMIN was not granted"));

        var status = Body<Dictionary<string, object>>(advisory.GetStatus());

        Assert.Equal("advisory", status["enforcement"].ToString());
        Assert.Equal("NET_ADMIN was not granted", status["reason"].ToString());
        Assert.Equal("True", status["offAvailable"].ToString());
    }

    [Fact]
    public void Status_reports_off_unavailable_under_the_firewall_without_an_open_group()
    {
        var enforced = Build(NetworkEnforcementStatus.Resolve(EgressProxyOptions.Parse("3128"), "enforced", null));
        var open = Build(NetworkEnforcementStatus.Resolve(EgressProxyOptions.Parse("3128"), "enforced", null, "agent-open"));

        Assert.Equal("False", Body<Dictionary<string, object>>(enforced.GetStatus())["offAvailable"].ToString());
        Assert.Equal("True", Body<Dictionary<string, object>>(open.GetStatus())["offAvailable"].ToString());
    }

    private sealed record UpdatedEntryView(Guid Id, string Host, NetworkListKind ListKind, Guid? AiProviderId, DateTime CreatedAt);

    private static NetworkController.AddEntryRequest Rule(string host = "api.example.com", NetworkListKind kind = NetworkListKind.Whitelist, Guid? provider = null)
        => new() { Host = host, ListKind = kind, AiProviderId = provider };

    private async Task<NetworkPolicyEntry> SeedRuleAsync(string host = "api.example.com", NetworkListKind kind = NetworkListKind.Whitelist, Guid? provider = null)
    {
        await _db.Network.AddEntryAsync(new NetworkPolicyEntry { Host = host, ListKind = kind, AiProviderId = provider }, TestContext.Current.CancellationToken);
        return (await _db.Network.FindEntryAsync(kind, host, provider, TestContext.Current.CancellationToken))!;
    }

    private async Task<Guid> SeedProviderAsync(string name = "claude")
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = name, Type = "claude-code", BaseUrl = "", Model = "" };
        await _db.Providers.CreateAiProviderAsync(provider);
        return provider.Id;
    }

    private async Task<NetworkPolicyEntry> StoredRuleAsync(Guid id)
        => (await _db.Network.GetEntryAsync(id, TestContext.Current.CancellationToken))!;

    private void AssertNothingInvalidated()
    {
        _policy.Verify(p => p.Invalidate(), Times.Never);
        _notifier.Verify(n => n.PolicyChangedAsync(), Times.Never);
    }

    [Fact]
    public async Task Updating_a_rules_host_canonicalises_it_keeps_its_identity_and_invalidates_the_policy_once()
    {
        var seeded = await SeedRuleAsync("typo.example.com");
        var before = await StoredRuleAsync(seeded.Id);

        var result = await Build().UpdateEntry(seeded.Id, Rule(" *.Example.COM "), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        var view = Body<UpdatedEntryView>(result);
        Assert.Equal((seeded.Id, ".example.com", NetworkListKind.Whitelist, (Guid?)null), (view.Id, view.Host, view.ListKind, view.AiProviderId));
        Assert.Equal(before.CreatedAt, view.CreatedAt);
        var stored = await StoredRuleAsync(seeded.Id);
        Assert.Equal((".example.com", NetworkListKind.Whitelist, (Guid?)null, before.CreatedAt), (stored.Host, stored.ListKind, stored.AiProviderId, stored.CreatedAt));
        Assert.Single(await _db.Network.GetEntriesAsync(TestContext.Current.CancellationToken));
        _policy.Verify(p => p.Invalidate(), Times.Once);
        _notifier.Verify(n => n.PolicyChangedAsync(), Times.Once);
    }

    [Fact]
    public async Task Updating_a_rules_kind_changes_only_the_kind()
    {
        var seeded = await SeedRuleAsync();

        var result = await Build().UpdateEntry(seeded.Id, Rule(kind: NetworkListKind.Blacklist), TestContext.Current.CancellationToken);

        Assert.Equal(NetworkListKind.Blacklist, Body<UpdatedEntryView>(result).ListKind);
        var stored = await StoredRuleAsync(seeded.Id);
        Assert.Equal(("api.example.com", NetworkListKind.Blacklist, (Guid?)null), (stored.Host, stored.ListKind, stored.AiProviderId));
        Assert.Single(await _db.Network.GetEntriesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Updating_a_rules_scope_moves_it_to_a_provider_and_back_to_all_providers()
    {
        var provider = await SeedProviderAsync();
        var seeded = await SeedRuleAsync();
        var controller = Build();

        var scoped = await controller.UpdateEntry(seeded.Id, Rule(provider: provider), TestContext.Current.CancellationToken);

        Assert.Equal(provider, Body<UpdatedEntryView>(scoped).AiProviderId);
        Assert.Equal(provider, (await StoredRuleAsync(seeded.Id)).AiProviderId);

        var global = await controller.UpdateEntry(seeded.Id, Rule(provider: null), TestContext.Current.CancellationToken);

        Assert.Null(Body<UpdatedEntryView>(global).AiProviderId);
        var stored = await StoredRuleAsync(seeded.Id);
        Assert.Null(stored.AiProviderId);
        Assert.Equal("api.example.com", stored.Host);
        Assert.Single(await _db.Network.GetEntriesAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(".example.com")]
    [InlineData("*.example.com")]
    public async Task A_rule_saved_unchanged_or_in_an_equivalent_spelling_stays_as_it_was_and_still_invalidates(string spelling)
    {
        var seeded = await SeedRuleAsync(".example.com", NetworkListKind.Blacklist);
        var before = await StoredRuleAsync(seeded.Id);

        var result = await Build().UpdateEntry(seeded.Id, Rule(spelling, NetworkListKind.Blacklist), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        var view = Body<UpdatedEntryView>(result);
        Assert.Equal((seeded.Id, ".example.com", NetworkListKind.Blacklist), (view.Id, view.Host, view.ListKind));
        var stored = await StoredRuleAsync(seeded.Id);
        Assert.Equal((".example.com", NetworkListKind.Blacklist, (Guid?)null, before.CreatedAt), (stored.Host, stored.ListKind, stored.AiProviderId, stored.CreatedAt));
        Assert.Single(await _db.Network.GetEntriesAsync(TestContext.Current.CancellationToken));
        _policy.Verify(p => p.Invalidate(), Times.Once);
        _notifier.Verify(n => n.PolicyChangedAsync(), Times.Once);
    }

    [Fact]
    public async Task Turning_a_rule_into_a_copy_of_another_is_refused_and_both_stay_as_they_were()
    {
        var provider = await SeedProviderAsync();
        var mine = await SeedRuleAsync("mine.example.com", NetworkListKind.Whitelist, provider);
        var other = await SeedRuleAsync(".other.example.com", NetworkListKind.Whitelist, provider);

        var result = await Build().UpdateEntry(mine.Id, Rule("*.Other.example.com", NetworkListKind.Whitelist, provider), TestContext.Current.CancellationToken);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var message = Body<Dictionary<string, string>>(bad)["error"];
        Assert.Contains("already exists", message);
        Assert.Contains(".other.example.com", message);
        var rules = await _db.Network.GetEntriesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, rules.Count);
        Assert.Equal("mine.example.com", (await StoredRuleAsync(mine.Id)).Host);
        Assert.Equal(".other.example.com", (await StoredRuleAsync(other.Id)).Host);
        AssertNothingInvalidated();
    }

    [Fact]
    public async Task The_same_host_on_another_scope_or_kind_is_not_a_duplicate()
    {
        var provider = await SeedProviderAsync();
        await SeedRuleAsync("api.example.com", NetworkListKind.Whitelist, provider);
        await SeedRuleAsync("api.example.com", NetworkListKind.Blacklist);
        var mine = await SeedRuleAsync("mine.example.com");

        var result = await Build().UpdateEntry(mine.Id, Rule("api.example.com"), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(3, (await _db.Network.GetEntriesAsync(TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task The_unique_index_refusing_the_save_is_answered_like_a_duplicate()
    {
        var store = new Mock<INetworkPolicyStore>();
        store.Setup(s => s.UpdateEntryAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<NetworkListKind>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("unique"));
        var controller = new NetworkController(store.Object, _db.NetworkForwards, _db.Providers, _policy.Object, _notifier.Object,
            NetworkEnforcementStatus.Resolve(EgressProxyOptions.Parse("3128"), "enforced", null), _forwarder);

        var result = await controller.UpdateEntry(Guid.NewGuid(), Rule("api.example.com"), TestContext.Current.CancellationToken);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var message = Body<Dictionary<string, string>>(bad)["error"];
        Assert.Contains("already exists", message);
        Assert.Contains("api.example.com", message);
        AssertNothingInvalidated();
    }

    [Fact]
    public async Task A_rule_deleted_between_the_load_and_the_save_is_not_found()
    {
        var store = new Mock<INetworkPolicyStore>();
        store.Setup(s => s.UpdateEntryAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<NetworkListKind>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateConcurrencyException("gone"));
        var controller = new NetworkController(store.Object, _db.NetworkForwards, _db.Providers, _policy.Object, _notifier.Object,
            NetworkEnforcementStatus.Resolve(EgressProxyOptions.Parse("3128"), "enforced", null), _forwarder);

        var result = await controller.UpdateEntry(Guid.NewGuid(), Rule(), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
        AssertNothingInvalidated();
    }

    [Fact]
    public async Task The_unique_index_refusing_a_forward_save_is_answered_like_a_taken_port()
    {
        var store = new Mock<INetworkForwardStore>();
        store.Setup(s => s.UpdateForwardAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("unique"));
        var controller = new NetworkController(_db.Network, store.Object, _db.Providers, _policy.Object, _notifier.Object,
            NetworkEnforcementStatus.Resolve(EgressProxyOptions.Parse("3128"), "enforced", null), _forwarder);

        var result = await controller.UpdateForward(Guid.NewGuid(), Forward(localPort: 15432), TestContext.Current.CancellationToken);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("15432 is already forwarded", Body<Dictionary<string, string>>(bad)["error"]);
        AssertNothingInvalidated();
    }

    [Fact]
    public async Task A_forward_deleted_between_the_load_and_the_save_is_not_found()
    {
        var store = new Mock<INetworkForwardStore>();
        store.Setup(s => s.UpdateForwardAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateConcurrencyException("gone"));
        var controller = new NetworkController(_db.Network, store.Object, _db.Providers, _policy.Object, _notifier.Object,
            NetworkEnforcementStatus.Resolve(EgressProxyOptions.Parse("3128"), "enforced", null), _forwarder);

        var result = await controller.UpdateForward(Guid.NewGuid(), Forward(), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
        AssertNothingInvalidated();
    }

    [Fact]
    public async Task Updating_an_unknown_rule_is_not_found_and_creates_nothing()
    {
        var result = await Build().UpdateEntry(Guid.NewGuid(), Rule(), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(await _db.Network.GetEntriesAsync(TestContext.Current.CancellationToken));
        AssertNothingInvalidated();
    }

    [Fact]
    public async Task An_unusable_rule_update_is_refused_and_the_rule_stays_as_it_was()
    {
        var seeded = await SeedRuleAsync();
        var controller = Build();
        var ct = TestContext.Current.CancellationToken;

        var results = new[]
        {
            await controller.UpdateEntry(seeded.Id, Rule(kind: (NetworkListKind)7), ct),
            await controller.UpdateEntry(seeded.Id, Rule("https://github.com"), ct),
            await controller.UpdateEntry(seeded.Id, Rule(provider: Guid.NewGuid()), ct),
        };

        foreach (var result in results)
        {
            var bad = Assert.IsType<BadRequestObjectResult>(result);
            Assert.False(string.IsNullOrWhiteSpace(Body<Dictionary<string, string>>(bad)["error"]));
        }
        var stored = await StoredRuleAsync(seeded.Id);
        Assert.Equal(("api.example.com", NetworkListKind.Whitelist, (Guid?)null), (stored.Host, stored.ListKind, stored.AiProviderId));
        AssertNothingInvalidated();
    }

    [Fact]
    public async Task The_unique_index_refuses_moving_a_rule_onto_another_rules_kind_host_and_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = await SeedProviderAsync();
        await SeedRuleAsync("a.example.com", NetworkListKind.Whitelist, provider);
        var b = await SeedRuleAsync("b.example.com", NetworkListKind.Whitelist, provider);

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.Network.UpdateEntryAsync(b.Id, "a.example.com", NetworkListKind.Whitelist, provider, ct));
    }

    [Fact]
    public async Task The_store_replaces_host_kind_and_scope_keeps_id_and_created_at_and_knows_no_unknown_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = await SeedProviderAsync();
        var seeded = await SeedRuleAsync();
        var before = await StoredRuleAsync(seeded.Id);

        var updated = await _db.Network.UpdateEntryAsync(seeded.Id, "new.example.com", NetworkListKind.Blacklist, provider, ct);

        Assert.NotNull(updated);
        var stored = await StoredRuleAsync(seeded.Id);
        Assert.Equal(("new.example.com", NetworkListKind.Blacklist, (Guid?)provider, before.CreatedAt), (stored.Host, stored.ListKind, stored.AiProviderId, stored.CreatedAt));
        Assert.Null(await _db.Network.UpdateEntryAsync(Guid.NewGuid(), "x.example.com", NetworkListKind.Whitelist, null, ct));
    }
}
