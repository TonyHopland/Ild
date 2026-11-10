using ILD.Api.Controllers;
using ILD.Api.Services;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The model gateway is under the network policy (ADR-0031), so ILD whitelists each provider's upstream hosts for it. These
/// follow a provider through create, change and delete, and the startup pass for the providers that already exist.
/// </summary>
public class ProviderNetworkEntriesTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AiProvidersController Controller()
    {
        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.GetAllSupportedProviderTypes()).Returns(["opencode", "claude-code"]);
        registry.Setup(r => r.GetModelSupport(It.IsAny<string>())).Returns((string type) => DeclaredModelSupport.For(type));
        registry.Setup(r => r.GetTraits(It.IsAny<string?>())).Returns((string? type) => DeclaredTraits.For(type));
        return new AiProvidersController(
            registry.Object, _db.Context, _db.Providers,
            new InteractiveProviderSessionService(NullLogger<InteractiveProviderSessionService>.Instance),
            Mock.Of<IManagedAgentProvisioner>());
    }

    private static AiProviderDto Dto(string type, string baseUrl) => new() { Name = "p-" + Guid.NewGuid().ToString("N")[..6], Type = type, BaseUrl = baseUrl, Model = "m" };

    private async Task<List<NetworkPolicyEntry>> EntriesAsync(Guid providerId)
        => await _db.Context.NetworkPolicyEntries.AsNoTracking().Where(e => e.AiProviderId == providerId).OrderBy(e => e.Host).ToListAsync(Ct);

    private async Task<Guid> CreateAsync(AiProviderDto dto)
    {
        var created = Assert.IsType<CreatedAtActionResult>(await Controller().Create(dto));
        return Guid.Parse(created.RouteValues!["id"]!.ToString()!);
    }

    [Fact]
    public async Task Creating_an_upstream_provider_whitelists_the_host_of_its_base_url_for_it_alone()
    {
        var id = await CreateAsync(Dto("opencode", "https://llm.example.com/v1"));

        var entry = Assert.Single(await EntriesAsync(id));
        Assert.Equal("llm.example.com", entry.Host);
        Assert.Equal(NetworkListKind.Whitelist, entry.ListKind);
        Assert.True(entry.AddedForProvider);
        Assert.False(entry.Installers);
        Assert.Empty(await _db.Context.NetworkPolicyEntries.Where(e => e.AiProviderId == null).ToListAsync(Ct));
    }

    [Fact]
    public async Task Creating_a_claude_provider_whitelists_the_anthropic_api()
    {
        var id = await CreateAsync(Dto("claude-code", ""));

        Assert.Equal(["api.anthropic.com"], (await EntriesAsync(id)).Select(e => e.Host));
    }

    [Fact]
    public async Task Changing_the_base_url_replaces_the_entry_and_keeps_every_other_entry()
    {
        var id = await CreateAsync(Dto("opencode", "https://old.example.com/v1"));
        var other = await CreateAsync(Dto("opencode", "https://other.example.com"));
        _db.Context.NetworkPolicyEntries.Add(new NetworkPolicyEntry { Id = Guid.NewGuid(), Host = "npm.example.com", ListKind = NetworkListKind.Whitelist, AiProviderId = id });
        _db.Context.NetworkPolicyEntries.Add(new NetworkPolicyEntry { Id = Guid.NewGuid(), Host = "old.example.com", ListKind = NetworkListKind.Blacklist, AiProviderId = id });
        await _db.Context.SaveChangesAsync(Ct);

        var update = Dto("opencode", "https://new.example.com/v1");
        Assert.IsType<OkObjectResult>(await Controller().Update(id.ToString(), update));

        Assert.Equal(
            [("new.example.com", NetworkListKind.Whitelist, true), ("npm.example.com", NetworkListKind.Whitelist, false), ("old.example.com", NetworkListKind.Blacklist, false)],
            (await EntriesAsync(id)).Select(e => (e.Host, e.ListKind, e.AddedForProvider)));
        Assert.Equal(["other.example.com"], (await EntriesAsync(other)).Select(e => e.Host));
    }

    [Fact]
    public async Task An_entry_the_operator_made_for_the_same_host_stays_theirs_and_survives_a_change()
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "own", Type = "opencode", BaseUrl = "https://llm.example.com", Model = "m" };
        await _db.Providers.CreateAiProviderAsync(provider);
        _db.Context.NetworkPolicyEntries.Add(new NetworkPolicyEntry { Id = Guid.NewGuid(), Host = "llm.example.com", ListKind = NetworkListKind.Whitelist, AiProviderId = provider.Id });
        await _db.Context.SaveChangesAsync(Ct);
        var entries = new ProviderNetworkEntries(_db.Context);

        Assert.False(await entries.SyncAsync(provider, Ct));
        provider.BaseUrl = "https://elsewhere.example.com";
        Assert.True(await entries.SyncAsync(provider, Ct));

        Assert.Equal([("elsewhere.example.com", true), ("llm.example.com", false)], (await EntriesAsync(provider.Id)).Select(e => (e.Host, e.AddedForProvider)));
    }

    [Fact]
    public async Task Deleting_a_provider_removes_its_entries_and_no_others()
    {
        var gone = await CreateAsync(Dto("opencode", "https://gone.example.com"));
        var kept = await CreateAsync(Dto("opencode", "https://kept.example.com"));
        _db.Context.NetworkPolicyEntries.Add(new NetworkPolicyEntry { Id = Guid.NewGuid(), Host = "global.example.com", ListKind = NetworkListKind.Whitelist });
        await _db.Context.SaveChangesAsync(Ct);

        await _db.Providers.DeleteAiProviderAsync((await _db.Providers.GetAiProviderByIdAsync(gone))!);

        Assert.Empty(await EntriesAsync(gone));
        Assert.Equal(["kept.example.com"], (await EntriesAsync(kept)).Select(e => e.Host));
        Assert.Single(await _db.Context.NetworkPolicyEntries.Where(e => e.AiProviderId == null).ToListAsync(Ct));
    }

    [Fact]
    public async Task The_startup_pass_adds_what_is_missing_once_and_leaves_operator_entries_alone()
    {
        var copilot = new AiProvider { Id = Guid.NewGuid(), Name = "c", Type = "copilot", BaseUrl = "", Model = "" };
        var upstream = new AiProvider { Id = Guid.NewGuid(), Name = "u", Type = "pi", BaseUrl = "http://192.168.1.20:1234/v1", Model = "m" };
        var noGateway = new AiProvider { Id = Guid.NewGuid(), Name = "n", Type = "unknown-type", BaseUrl = "", Model = "" };
        foreach (var p in new[] { copilot, upstream, noGateway }) await _db.Providers.CreateAiProviderAsync(p);
        _db.Context.NetworkPolicyEntries.Add(new NetworkPolicyEntry { Id = Guid.NewGuid(), Host = "api.github.com", ListKind = NetworkListKind.Whitelist, AiProviderId = copilot.Id });
        await _db.Context.SaveChangesAsync(Ct);

        Assert.Equal(2, await new ProviderNetworkEntries(_db.Context).SeedAsync(Ct));

        Assert.Equal([(".githubcopilot.com", true), ("api.github.com", false)], (await EntriesAsync(copilot.Id)).Select(e => (e.Host, e.AddedForProvider)));
        Assert.Equal(["192.168.1.20"], (await EntriesAsync(upstream.Id)).Select(e => e.Host));
        Assert.Empty(await EntriesAsync(noGateway.Id));

        // An entry the operator deletes afterwards is not put back, and a second pass changes nothing.
        var before = await _db.Context.NetworkPolicyEntries.CountAsync(Ct);
        _db.Context.NetworkPolicyEntries.RemoveRange(_db.Context.NetworkPolicyEntries.Where(e => e.AiProviderId == upstream.Id));
        await _db.Context.SaveChangesAsync(Ct);
        Assert.Equal(0, await new ProviderNetworkEntries(_db.Context).SeedAsync(Ct));
        Assert.Equal(before - 1, await _db.Context.NetworkPolicyEntries.CountAsync(Ct));
    }

    [Fact]
    public async Task Editing_a_managed_entry_in_settings_makes_it_the_operators()
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "e", Type = "opencode", BaseUrl = "https://llm.example.com", Model = "m" };
        await _db.Providers.CreateAiProviderAsync(provider);
        var entries = new ProviderNetworkEntries(_db.Context);
        await entries.SyncAsync(provider, Ct);
        var managed = Assert.Single(await EntriesAsync(provider.Id));

        await _db.Network.UpdateEntryAsync(managed.Id, "llm.example.com", NetworkListKind.Whitelist, provider.Id, Ct);
        provider.BaseUrl = "https://next.example.com";
        await entries.SyncAsync(provider, Ct);

        Assert.Equal([("llm.example.com", false), ("next.example.com", true)], (await EntriesAsync(provider.Id)).Select(e => (e.Host, e.AddedForProvider)));
    }
}
