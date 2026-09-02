using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>The mode and lists in force at one moment, ready to judge a destination.</summary>
public sealed record EgressPolicySnapshot(NetworkMode Mode, IReadOnlyList<NetworkPolicyEntry> Entries)
{
    public static readonly EgressPolicySnapshot Off = new(NetworkMode.Off, Array.Empty<NetworkPolicyEntry>());

    public NetworkDecision Decide(string host, Guid? aiProviderId)
        => EgressRules.Decide(Mode, Entries, host, aiProviderId);
}

/// <summary>
/// What the proxy asks before every connection. The snapshot is what the
/// database says, at most <see cref="EgressPolicy.CacheTtl"/> old — or newer,
/// because every edit also calls <see cref="Invalidate"/>, so an operator's
/// click is judged against on the very next connection rather than after the
/// cache turns over.
/// </summary>
public interface IEgressPolicy
{
    ValueTask<EgressPolicySnapshot> GetAsync(CancellationToken ct = default);

    /// <summary>Drop the cached snapshot and tell listeners the rules changed.</summary>
    void Invalidate();

    /// <summary>Raised by <see cref="Invalidate"/>; the proxy re-judges its open tunnels on it.</summary>
    event Action? Changed;
}

public sealed class EgressPolicy : IEgressPolicy
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _reload = new(1, 1);
    private EgressPolicySnapshot? _snapshot;
    private long _loadedAt;

    public EgressPolicy(IServiceScopeFactory scopes, TimeProvider clock)
    {
        _scopes = scopes;
        _clock = clock;
    }

    public event Action? Changed;

    public async ValueTask<EgressPolicySnapshot> GetAsync(CancellationToken ct = default)
    {
        if (Fresh() is { } cached) return cached;

        await _reload.WaitAsync(ct);
        try
        {
            if (Fresh() is { } raced) return raced;

            using var scope = _scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<INetworkPolicyStore>();
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingStore>();

            var modeSetting = await settings.GetByKeyAsync(AppSettingKeys.NetworkMode, ct);
            EgressRules.TryParseMode(modeSetting?.Value ?? AppSettingKeys.DefaultNetworkMode, out var mode);
            var entries = await store.GetEntriesAsync(ct);

            var snapshot = new EgressPolicySnapshot(mode, entries);
            Volatile.Write(ref _loadedAt, _clock.GetTimestamp());
            Volatile.Write(ref _snapshot, snapshot);
            return snapshot;
        }
        finally
        {
            _reload.Release();
        }
    }

    public void Invalidate()
    {
        Volatile.Write(ref _snapshot, null);
        Changed?.Invoke();
    }

    private EgressPolicySnapshot? Fresh()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null) return null;
        return _clock.GetElapsedTime(Volatile.Read(ref _loadedAt)) < CacheTtl ? snapshot : null;
    }
}
