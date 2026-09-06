using ILD.Api.Controllers;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
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
    private EgressPolicySnapshot _snapshot = EgressPolicySnapshot.Off;

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

        var result = await controller.AddEntry(new NetworkController.AddEntryRequest { Host = " *.GitHub.com ", ListKind = NetworkListKind.Whitelist }, default);

        var created = Assert.IsType<CreatedAtActionResult>(result);
        var view = Body<EntryView>(created);
        Assert.Equal(".github.com", view.Host);
        Assert.Equal(NetworkListKind.Whitelist, view.ListKind);
        Assert.Null(view.AiProviderId);
        Assert.Equal(".github.com", Assert.Single(await _db.Network.GetEntriesAsync()).Host);
        _policy.Verify(p => p.Invalidate(), Times.Once);
        _notifier.Verify(n => n.PolicyChangedAsync(), Times.Once);
    }

    [Fact]
    public async Task Adding_an_entry_that_exists_answers_with_the_existing_row_and_changes_nothing()
    {
        var controller = Build();
        var first = Body<EntryView>(Assert.IsType<CreatedAtActionResult>(
            await controller.AddEntry(new NetworkController.AddEntryRequest { Host = "api.example.com", ListKind = NetworkListKind.Blacklist }, default)));

        var again = Body<EntryView>(Assert.IsType<OkObjectResult>(
            await controller.AddEntry(new NetworkController.AddEntryRequest { Host = "API.example.com." , ListKind = NetworkListKind.Blacklist }, default)));

        Assert.Equal(first.Id, again.Id);
        Assert.Single(await _db.Network.GetEntriesAsync());
        _policy.Verify(p => p.Invalidate(), Times.Once);
    }

    [Fact]
    public async Task The_unique_index_refuses_a_duplicate_scoped_entry_inserted_behind_the_controllers_back()
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "claude", Type = "claude-code", BaseUrl = "", Model = "" };
        await _db.Providers.CreateAiProviderAsync(provider);
        await _db.Network.AddEntryAsync(new NetworkPolicyEntry { Host = "api.example.com", ListKind = NetworkListKind.Whitelist, AiProviderId = provider.Id });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.Network.AddEntryAsync(
            new NetworkPolicyEntry { Host = "api.example.com", ListKind = NetworkListKind.Whitelist, AiProviderId = provider.Id }));
    }

    [Fact]
    public async Task An_undefined_list_kind_is_refused_before_anything_is_stored()
    {
        var controller = Build();

        var result = await controller.AddEntry(new NetworkController.AddEntryRequest { Host = "api.example.com", ListKind = (NetworkListKind)7 }, default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await _db.Network.GetEntriesAsync());
        _policy.Verify(p => p.Invalidate(), Times.Never);
    }

    [Fact]
    public async Task A_url_is_refused_with_the_reason_and_nothing_changes()
    {
        var controller = Build();

        var result = await controller.AddEntry(new NetworkController.AddEntryRequest { Host = "https://github.com", ListKind = NetworkListKind.Blacklist }, default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await _db.Network.GetEntriesAsync());
        _policy.Verify(p => p.Invalidate(), Times.Never);
    }

    [Fact]
    public async Task A_scope_to_an_unknown_provider_is_refused()
    {
        var controller = Build();

        var result = await controller.AddEntry(new NetworkController.AddEntryRequest { Host = "api.example.com", ListKind = NetworkListKind.Whitelist, AiProviderId = Guid.NewGuid() }, default);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Deleting_an_entry_invalidates_the_proxy()
    {
        var entry = new NetworkPolicyEntry { Host = "api.example.com", ListKind = NetworkListKind.Blacklist };
        await _db.Network.AddEntryAsync(entry);
        var controller = Build();

        Assert.IsType<NoContentResult>(await controller.DeleteEntry(entry.Id, default));
        Assert.IsType<NotFoundResult>(await controller.DeleteEntry(entry.Id, default));

        Assert.Empty(await _db.Network.GetEntriesAsync());
        _policy.Verify(p => p.Invalidate(), Times.Once);
    }

    [Fact]
    public async Task A_log_entry_can_be_promoted_to_either_list_globally_or_scoped_to_its_provider()
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "claude", Type = "claude-code", BaseUrl = "", Model = "" };
        await _db.Providers.CreateAiProviderAsync(provider);
        var logged = new NetworkLogEntry { Host = "registry.npmjs.org", Port = 443, Timestamp = DateTime.UtcNow, Decision = NetworkDecision.Blocked, AiProviderId = provider.Id };
        await _db.Network.AppendLogAsync(new[] { logged });
        var controller = Build();

        var global = Body<EntryView>(Assert.IsType<CreatedAtActionResult>(await controller.WhitelistFromLog(logged.Id, null, default)));
        var scoped = Body<EntryView>(Assert.IsType<CreatedAtActionResult>(
            await controller.BlacklistFromLog(logged.Id, new NetworkController.AddFromLogRequest { ScopeToProvider = true }, default)));

        Assert.Equal(("registry.npmjs.org", NetworkListKind.Whitelist, (Guid?)null), (global.Host, global.ListKind, global.AiProviderId));
        Assert.Equal(("registry.npmjs.org", NetworkListKind.Blacklist, (Guid?)provider.Id), (scoped.Host, scoped.ListKind, scoped.AiProviderId));

        // Promoting the same line twice does not duplicate the entry.
        Assert.IsType<OkObjectResult>(await controller.WhitelistFromLog(logged.Id, null, default));
        Assert.Equal(2, (await _db.Network.GetEntriesAsync()).Count);

        Assert.IsType<NotFoundResult>(await controller.WhitelistFromLog(Guid.NewGuid(), null, default));
    }

    [Fact]
    public async Task Clearing_the_log_empties_it_and_broadcasts()
    {
        await _db.Network.AppendLogAsync(new[]
        {
            new NetworkLogEntry { Host = "a.example", Port = 443, Timestamp = DateTime.UtcNow, Decision = NetworkDecision.Advisory },
            new NetworkLogEntry { Host = "b.example", Port = 80, Timestamp = DateTime.UtcNow, Decision = NetworkDecision.Advisory },
        });
        var controller = Build();

        Assert.Equal(2, Body<Dictionary<string, int>>(await controller.ClearLog(default))["removed"]);

        Assert.Empty(await _db.Network.GetLogAsync(10));
        _notifier.Verify(n => n.LogClearedAsync(), Times.Once);
    }

    [Fact]
    public async Task The_log_is_read_newest_first_and_capped()
    {
        var now = DateTime.UtcNow;
        await _db.Network.AppendLogAsync(Enumerable.Range(0, 5).Select(i => new NetworkLogEntry
        {
            Host = $"h{i}.example", Port = 443, Timestamp = now.AddSeconds(i), Decision = NetworkDecision.Allowed,
        }).ToList());
        var controller = Build();

        var page = Body<List<Dictionary<string, object>>>(await controller.GetLog(take: 2, ct: default));

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
            await controller.AddForward(Forward(name: " postgres ", host: " POSTGRES. "), default));

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

        var result = await controller.AddForward(Forward(host: host), default);

        Assert.Contains("not a pattern", Body<Dictionary<string, string>>(result)["error"]);
        Assert.Empty(await _db.NetworkForwards.GetForwardsAsync());
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

        Assert.IsType<BadRequestObjectResult>(await controller.AddForward(Forward(name, host, port, localPort), default));

        Assert.Empty(await _db.NetworkForwards.GetForwardsAsync());
        _policy.Verify(p => p.Invalidate(), Times.Never);
    }

    [Fact]
    public async Task A_local_port_already_forwarded_is_refused_with_what_holds_it()
    {
        var controller = Build();
        await controller.AddForward(Forward(), default);

        var result = await controller.AddForward(Forward(name: "redis", host: "cache", port: 6379), default);

        Assert.Contains("postgres:5432", Body<Dictionary<string, string>>(result)["error"]);
        Assert.Single(await _db.NetworkForwards.GetForwardsAsync());
    }

    [Fact]
    public async Task The_unique_index_refuses_a_second_forward_on_a_local_port_behind_the_controllers_back()
    {
        await _db.NetworkForwards.AddForwardAsync(new NetworkForwardEntry { Name = "a", Host = "a.example", Port = 1, LocalPort = 15432 });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.NetworkForwards.AddForwardAsync(
            new NetworkForwardEntry { Name = "b", Host = "b.example", Port = 2, LocalPort = 15432 }));
    }

    [Fact]
    public async Task A_forward_reads_back_with_the_current_verdict_on_its_host_and_its_listener_state()
    {
        var controller = Build();
        var created = Body<ForwardView>(Assert.IsType<CreatedAtActionResult>(await controller.AddForward(Forward(), default)));
        _forwarder.Errors[created.Id] = "Local port 15432 is already in use by something else";
        _snapshot = new EgressPolicySnapshot(NetworkMode.Whitelist, Array.Empty<NetworkPolicyEntry>());

        var blocked = Assert.Single(Body<List<ForwardView>>(await controller.GetForwards(default)));
        Assert.Equal(NetworkDecision.Blocked, blocked.Decision);
        Assert.Contains("already in use", blocked.ListenError);

        _snapshot = new EgressPolicySnapshot(NetworkMode.Whitelist, new[]
        {
            new NetworkPolicyEntry { Host = "postgres", ListKind = NetworkListKind.Whitelist },
        });

        Assert.Equal(NetworkDecision.Allowed, Assert.Single(Body<List<ForwardView>>(await controller.GetForwards(default))).Decision);
    }

    [Fact]
    public async Task Deleting_a_forward_invalidates_the_policy()
    {
        var controller = Build();
        var created = Body<ForwardView>(Assert.IsType<CreatedAtActionResult>(await controller.AddForward(Forward(), default)));
        _policy.Invocations.Clear();

        Assert.IsType<NoContentResult>(await controller.DeleteForward(created.Id, default));
        Assert.IsType<NotFoundResult>(await controller.DeleteForward(created.Id, default));

        Assert.Empty(await _db.NetworkForwards.GetForwardsAsync());
        _policy.Verify(p => p.Invalidate(), Times.Once);
    }

    [Fact]
    public void Status_reports_the_enforcement_the_entrypoint_announced()
    {
        var advisory = Build(NetworkEnforcementStatus.Resolve(EgressProxyOptions.Parse("3128"), "advisory", "NET_ADMIN was not granted"));

        var status = Body<Dictionary<string, object>>(advisory.GetStatus());

        Assert.Equal("advisory", status["enforcement"].ToString());
        Assert.Equal("NET_ADMIN was not granted", status["reason"].ToString());
    }
}
