using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The one module that edits the network policy: what each edit comes to, how it settles the races the unique indexes decide
/// (real indexes, on SQLite that answers as PostgreSQL does), and that an edit tells the proxy exactly when it changed something.
/// </summary>
public sealed class NetworkPolicyEditsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Mock<IEgressPolicy> _policy = new();
    private readonly Mock<INetworkNotifier> _notifier = new();
    private readonly List<string> _calls = [];

    public NetworkPolicyEditsTests()
    {
        _policy.Setup(p => p.Invalidate()).Callback(() => _calls.Add("invalidate"));
        _notifier.Setup(n => n.PolicyChangedAsync()).Returns(() => { _calls.Add("notify"); return Task.CompletedTask; });
        _policy.Setup(p => p.GetAsync(It.IsAny<CancellationToken>())).Returns(ValueTask.FromResult(EgressPolicySnapshot.Monitor with { Protected = [ProtectedDestination.PrivateRanges] }));
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NetworkPolicyEdits Edits() => _db.NetworkEdits(_policy.Object, _notifier.Object);

    private NetworkPolicyEdits Edits(INetworkPolicyStore entries, INetworkForwardStore? forwards = null)
        => new(entries, forwards ?? _db.NetworkForwards, _db.Providers, _db.Settings, _policy.Object, _notifier.Object);

    private void AssertChangedOnce() => Assert.Equal(["invalidate", "notify"], _calls);

    private void AssertNothingChanged() => Assert.Empty(_calls);

    private async Task<NetworkPolicyEntry> SeedEntryAsync(string host, NetworkListKind kind = NetworkListKind.Whitelist, Guid? providerId = null)
    {
        var entry = new NetworkPolicyEntry { Id = Guid.NewGuid(), Host = host, ListKind = kind, AiProviderId = providerId, CreatedAt = DateTime.UtcNow };
        await _db.Network.AddEntryAsync(entry, Ct);
        return entry;
    }

    private async Task<NetworkForwardEntry> SeedForwardAsync(int localPort)
    {
        var forward = new NetworkForwardEntry { Id = Guid.NewGuid(), Name = "db", Host = "db.internal", Port = 5432, LocalPort = localPort, CreatedAt = DateTime.UtcNow };
        await _db.NetworkForwards.AddForwardAsync(forward, Ct);
        return forward;
    }

    private async Task<Guid> SeedProviderAsync()
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "p-" + Guid.NewGuid().ToString("N")[..6], Type = "claude-code", BaseUrl = "", Model = "" };
        await _db.Providers.CreateAiProviderAsync(provider);
        return provider.Id;
    }

    [Fact]
    public async Task A_change_invalidates_the_policy_and_then_tells_the_listeners_once_for_every_kind_of_edit()
    {
        var edits = Edits();

        var added = await edits.AddEntryAsync(" *.GitHub.com ", NetworkListKind.Whitelist, null, false, Ct);
        Assert.Equal((NetworkEditOutcome.Created, ".github.com"), (added.Outcome, added.Value!.Host));
        AssertChangedOnce();
        _calls.Clear();

        Assert.Equal(NetworkEditOutcome.Updated, (await edits.UpdateEntryAsync(added.Value.Id, "api.example.com", NetworkListKind.Blacklist, null, false, Ct)).Outcome);
        AssertChangedOnce();
        _calls.Clear();

        Assert.True(await edits.DeleteEntryAsync(added.Value.Id, Ct));
        AssertChangedOnce();
        _calls.Clear();

        var forward = await edits.AddForwardAsync("db", "db.internal", 5432, 15432, Ct);
        Assert.Equal(NetworkEditOutcome.Created, forward.Outcome);
        AssertChangedOnce();
        _calls.Clear();

        Assert.Equal(NetworkEditOutcome.Updated, (await edits.UpdateForwardAsync(forward.Value!.Id, "db", "db.internal", 5433, 15432, Ct)).Outcome);
        AssertChangedOnce();
        _calls.Clear();

        Assert.True(await edits.DeleteForwardAsync(forward.Value.Id, Ct));
        AssertChangedOnce();
        _calls.Clear();

        Assert.NotNull(await edits.SetProtectedAsync(ProtectedDestination.PrivateRanges.Id, false, Ct));
        Assert.Equal("false", (await _db.Settings.GetByKeyAsync("network.protect." + ProtectedDestination.PrivateRanges.Id, Ct))!.Value);
        AssertChangedOnce();
        _calls.Clear();

        await edits.SetModeAsync("whitelist", Ct);
        Assert.Equal("whitelist", (await _db.Settings.GetByKeyAsync(AppSettingKeys.NetworkMode, Ct))!.Value);
        AssertChangedOnce();
    }

    [Theory]
    [InlineData(".example.com")]
    [InlineData("*.example.com")]
    public async Task An_entry_saved_unchanged_or_in_an_equivalent_spelling_still_invalidates_and_tells_the_listeners(string spelling)
    {
        var seeded = await SeedEntryAsync(".example.com", NetworkListKind.Blacklist);

        var saved = await Edits().UpdateEntryAsync(seeded.Id, spelling, NetworkListKind.Blacklist, null, false, Ct);

        Assert.Equal((NetworkEditOutcome.Updated, ".example.com"), (saved.Outcome, saved.Value!.Host));
        AssertChangedOnce();
    }

    [Fact]
    public async Task An_edit_that_is_refused_or_changes_nothing_neither_invalidates_nor_tells_anyone()
    {
        var edits = Edits();
        var provider = await SeedProviderAsync();
        var existing = await SeedEntryAsync("api.example.com");
        var other = await SeedEntryAsync("other.example.com");
        var taken = await SeedForwardAsync(15432);
        var elsewhere = await SeedForwardAsync(15433);
        var patternLog = new NetworkLogEntry { Id = Guid.NewGuid(), Host = "*.example.com", Port = 443, Timestamp = DateTime.UtcNow, Decision = NetworkDecision.Blocked };
        await _db.Network.AppendLogAsync([patternLog], Ct);

        var outcomes = new[]
        {
            (await edits.AddEntryAsync("api.example.com", NetworkListKind.Whitelist, null, false, Ct)).Outcome,
            (await edits.AddEntryAsync("https://api.example.com/x", NetworkListKind.Whitelist, null, false, Ct)).Outcome,
            (await edits.AddEntryAsync("a.example.com", (NetworkListKind)7, null, false, Ct)).Outcome,
            (await edits.AddEntryAsync("a.example.com", NetworkListKind.Whitelist, provider, true, Ct)).Outcome,
            (await edits.AddEntryAsync("a.example.com", NetworkListKind.Whitelist, Guid.NewGuid(), false, Ct)).Outcome,
            (await edits.AddFromLogAsync(patternLog.Id, NetworkListKind.Whitelist, false, false, Ct)).Outcome,
            (await edits.AddFromLogAsync(Guid.NewGuid(), NetworkListKind.Whitelist, false, false, Ct)).Outcome,
            (await edits.UpdateEntryAsync(other.Id, "api.example.com", NetworkListKind.Whitelist, null, false, Ct)).Outcome,
            (await edits.UpdateEntryAsync(Guid.NewGuid(), "x.example.com", NetworkListKind.Whitelist, null, false, Ct)).Outcome,
            (await edits.AddForwardAsync("", "db.internal", 5432, 15440, Ct)).Outcome,
            (await edits.AddForwardAsync("db", "db.internal", 5432, taken.LocalPort, Ct)).Outcome,
            (await edits.UpdateForwardAsync(elsewhere.Id, "db", "db.internal", 5432, taken.LocalPort, Ct)).Outcome,
            (await edits.UpdateForwardAsync(Guid.NewGuid(), "db", "db.internal", 5432, 15441, Ct)).Outcome,
        };

        const NetworkEditOutcome Refused = NetworkEditOutcome.Refused, NotFound = NetworkEditOutcome.NotFound;
        Assert.Equal(
            [NetworkEditOutcome.Existing, Refused, Refused, Refused, Refused, Refused, NotFound, Refused, NotFound, Refused, Refused, Refused, NotFound],
            outcomes);
        Assert.False(await edits.DeleteEntryAsync(Guid.NewGuid(), Ct));
        Assert.False(await edits.DeleteForwardAsync(Guid.NewGuid(), Ct));
        Assert.Null(await edits.SetProtectedAsync("nope", true, Ct));
        AssertNothingChanged();
        Assert.Equal(existing.Id, (await _db.Network.FindEntryAsync(NetworkListKind.Whitelist, "api.example.com", null, Ct))!.Id);
    }

    [Fact]
    public async Task Adding_an_entry_that_exists_answers_with_the_existing_row()
    {
        var existing = await SeedEntryAsync("api.example.com", NetworkListKind.Blacklist);

        var again = await Edits().AddEntryAsync("API.example.com.", NetworkListKind.Blacklist, null, false, Ct);

        Assert.Equal((NetworkEditOutcome.Existing, existing.Id), (again.Outcome, again.Value!.Id));
        Assert.Single(await _db.Network.GetEntriesAsync(Ct));
    }

    [Fact]
    public async Task An_add_that_loses_the_race_to_the_unique_index_answers_with_the_winner_and_changes_nothing()
    {
        var provider = await SeedProviderAsync();
        var winner = new NetworkPolicyEntry { Id = Guid.NewGuid(), Host = "api.example.com", ListKind = NetworkListKind.Whitelist, AiProviderId = provider, CreatedAt = DateTime.UtcNow };
        var racing = new RacingEntries(_db.Network) { BeforeAdd = () => new NetworkPolicyStore(_db.Fresh()).AddEntryAsync(winner, Ct) };

        var result = await Edits(racing).AddEntryAsync("api.example.com", NetworkListKind.Whitelist, provider, false, Ct);

        Assert.Equal((NetworkEditOutcome.Existing, winner.Id), (result.Outcome, result.Value!.Id));
        Assert.Single(await _db.Network.GetEntriesAsync(Ct));
        AssertNothingChanged();
    }

    [Fact]
    public async Task An_update_that_loses_the_race_to_the_unique_index_is_answered_like_a_duplicate()
    {
        var provider = await SeedProviderAsync();
        var mine = await SeedEntryAsync("mine.example.com", providerId: provider);
        var racing = new RacingEntries(_db.Network)
        {
            BeforeUpdate = () => new NetworkPolicyStore(_db.Fresh()).AddEntryAsync(
                new NetworkPolicyEntry { Id = Guid.NewGuid(), Host = "api.example.com", ListKind = NetworkListKind.Whitelist, AiProviderId = provider, CreatedAt = DateTime.UtcNow }, Ct),
        };

        var result = await Edits(racing).UpdateEntryAsync(mine.Id, "api.example.com", NetworkListKind.Whitelist, provider, false, Ct);

        Assert.Equal(NetworkEditOutcome.Refused, result.Outcome);
        Assert.Equal("A whitelist rule for api.example.com already exists for this scope", result.Error);
        AssertNothingChanged();
    }

    [Fact]
    public async Task A_local_port_that_is_taken_is_refused_with_what_holds_it_and_one_that_was_raced_is_refused_as_forwarded()
    {
        await SeedForwardAsync(15432);

        var taken = await Edits().AddForwardAsync("again", "other.internal", 5432, 15432, Ct);
        Assert.Equal("Local port 15432 already forwards to db.internal:5432", taken.Error);

        var racing = new RacingForwards(_db.NetworkForwards)
        {
            BeforeAdd = () => new NetworkForwardStore(_db.Fresh()).AddForwardAsync(
                new NetworkForwardEntry { Id = Guid.NewGuid(), Name = "x", Host = "x.internal", Port = 1, LocalPort = 15440, CreatedAt = DateTime.UtcNow }, Ct),
        };
        var raced = await Edits(_db.Network, racing).AddForwardAsync("db", "db.internal", 5432, 15440, Ct);

        Assert.Equal(NetworkEditOutcome.Refused, raced.Outcome);
        Assert.Equal("Local port 15440 is already forwarded", raced.Error);
        AssertNothingChanged();
    }

    [Fact]
    public async Task A_forward_update_that_loses_the_race_for_its_local_port_is_refused_as_forwarded()
    {
        var mine = await SeedForwardAsync(15432);
        var racing = new RacingForwards(_db.NetworkForwards)
        {
            BeforeUpdate = () => new NetworkForwardStore(_db.Fresh()).AddForwardAsync(
                new NetworkForwardEntry { Id = Guid.NewGuid(), Name = "x", Host = "x.internal", Port = 1, LocalPort = 15440, CreatedAt = DateTime.UtcNow }, Ct),
        };

        var result = await Edits(_db.Network, racing).UpdateForwardAsync(mine.Id, "db", "db.internal", 5432, 15440, Ct);

        Assert.Equal(("Local port 15440 is already forwarded", NetworkEditOutcome.Refused), (result.Error, result.Outcome));
        AssertNothingChanged();
    }

    [Fact]
    public async Task An_update_of_an_entry_or_a_forward_deleted_between_its_load_and_its_save_is_answered_as_missing()
    {
        // The window is inside the store's update, so the store's save is made to fail as EF does when the row is gone.
        static Task Gone() => throw new DbUpdateConcurrencyException("The row is gone.");
        var entry = await SeedEntryAsync("mine.example.com");
        var forward = await SeedForwardAsync(15432);

        var entryResult = await Edits(new RacingEntries(_db.Network) { BeforeUpdate = Gone })
            .UpdateEntryAsync(entry.Id, "api.example.com", NetworkListKind.Whitelist, null, false, Ct);
        var forwardResult = await Edits(_db.Network, new RacingForwards(_db.NetworkForwards) { BeforeUpdate = Gone })
            .UpdateForwardAsync(forward.Id, "db", "db.internal", 5432, 15440, Ct);

        Assert.Equal(NetworkEditOutcome.NotFound, entryResult.Outcome);
        Assert.Equal(NetworkEditOutcome.NotFound, forwardResult.Outcome);
        AssertNothingChanged();
    }

    [Fact]
    public async Task Allowing_several_hosts_stores_them_together_and_tells_the_listeners_once_and_only_when_one_is_new()
    {
        var edits = Edits();

        var first = await edits.AddAllowedAsync(["a.example", "b.example", ".c.example", "a.example"], null, false, Ct);

        Assert.Equal(["a.example", "b.example", ".c.example"], first.Value!.Select(e => e.Host));
        Assert.Equal(3, (await _db.Network.GetEntriesAsync(Ct)).Count);
        AssertChangedOnce();
        _calls.Clear();

        var again = await edits.AddAllowedAsync(["a.example", "b.example"], null, false, Ct);

        Assert.Empty(again.Value!);
        AssertNothingChanged();
    }

    /// <summary>The real store, with a chance for another writer to get in between the module's read and its write.</summary>
    private sealed class RacingEntries(INetworkPolicyStore inner) : INetworkPolicyStore
    {
        public Func<Task>? BeforeAdd { get; init; }
        public Func<Task>? BeforeUpdate { get; init; }

        public Task AddEntriesAsync(IReadOnlyList<NetworkPolicyEntry> entries, CancellationToken ct = default) => inner.AddEntriesAsync(entries, ct);

        public async Task AddEntryAsync(NetworkPolicyEntry entry, CancellationToken ct = default)
        {
            if (BeforeAdd is not null) await BeforeAdd();
            await inner.AddEntryAsync(entry, ct);
        }

        public async Task<NetworkPolicyEntry?> UpdateEntryAsync(Guid id, string host, NetworkListKind kind, Guid? aiProviderId, CancellationToken ct = default, bool installers = false)
        {
            if (BeforeUpdate is not null) await BeforeUpdate();
            return await inner.UpdateEntryAsync(id, host, kind, aiProviderId, ct, installers);
        }

        public Task<IReadOnlyList<NetworkPolicyEntry>> GetEntriesAsync(CancellationToken ct = default) => inner.GetEntriesAsync(ct);
        public Task<NetworkPolicyEntry?> GetEntryAsync(Guid id, CancellationToken ct = default) => inner.GetEntryAsync(id, ct);
        public Task<NetworkPolicyEntry?> FindEntryAsync(NetworkListKind kind, string host, Guid? aiProviderId, CancellationToken ct = default, bool installers = false) => inner.FindEntryAsync(kind, host, aiProviderId, ct, installers);
        public Task<bool> DeleteEntryAsync(Guid id, CancellationToken ct = default) => inner.DeleteEntryAsync(id, ct);
        public Task<IReadOnlyList<NetworkLogEntry>> GetLogAsync(int take, CancellationToken ct = default) => inner.GetLogAsync(take, ct);
        public Task<NetworkLogEntry?> GetLogEntryAsync(Guid id, CancellationToken ct = default) => inner.GetLogEntryAsync(id, ct);
        public Task<IReadOnlyList<NetworkLogDestination>> GetLogDestinationsAsync(Guid? aiProviderId, bool installers, DateTime since, CancellationToken ct = default) => inner.GetLogDestinationsAsync(aiProviderId, installers, since, ct);
        public Task AppendLogAsync(IReadOnlyList<NetworkLogEntry> entries, CancellationToken ct = default) => inner.AppendLogAsync(entries, ct);
        public Task<int> ClearLogAsync(CancellationToken ct = default) => inner.ClearLogAsync(ct);
        public Task<int> DeleteLogOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => inner.DeleteLogOlderThanAsync(cutoff, ct);
    }

    private sealed class RacingForwards(INetworkForwardStore inner) : INetworkForwardStore
    {
        public Func<Task>? BeforeAdd { get; init; }
        public Func<Task>? BeforeUpdate { get; init; }

        public async Task AddForwardAsync(NetworkForwardEntry forward, CancellationToken ct = default)
        {
            if (BeforeAdd is not null) await BeforeAdd();
            await inner.AddForwardAsync(forward, ct);
        }

        public async Task<NetworkForwardEntry?> UpdateForwardAsync(Guid id, string name, string host, int port, int localPort, CancellationToken ct = default)
        {
            if (BeforeUpdate is not null) await BeforeUpdate();
            return await inner.UpdateForwardAsync(id, name, host, port, localPort, ct);
        }

        public Task<IReadOnlyList<NetworkForwardEntry>> GetForwardsAsync(CancellationToken ct = default) => inner.GetForwardsAsync(ct);
        public Task<NetworkForwardEntry?> FindByLocalPortAsync(int localPort, CancellationToken ct = default) => inner.FindByLocalPortAsync(localPort, ct);
        public Task<bool> DeleteForwardAsync(Guid id, CancellationToken ct = default) => inner.DeleteForwardAsync(id, ct);
    }
}
