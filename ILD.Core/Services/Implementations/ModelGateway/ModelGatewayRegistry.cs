using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ILD.Data.DTOs;

namespace ILD.Core.Services.Implementations.ModelGateway;

public enum GatewayTokenState
{
    Unknown,
    Active,
    Ended,
}

/// <summary>What a run token is bound to.</summary>
public sealed record GatewayGrant(Guid ProviderId, Guid OwnerId);

/// <summary>
/// The per-run tokens of the model gateway (ADR-0031). A token is minted when an agent execution
/// starts and ended when it finishes or ILD restarts, so a token that leaks out of a run is dead
/// the moment the run is.
/// </summary>
public interface IModelGatewayRegistry
{
    /// <param name="providerId">The provider whose credential the token may use.</param>
    /// <param name="ownerId">The loop run or chat session the token belongs to.</param>
    /// <param name="apiUrl">ILD's own address as the agent reaches it.</param>
    /// <param name="modelHosts">Hosts the agent must reach through the gateway while the lease is open.</param>
    ModelGatewayLease Open(Guid providerId, Guid ownerId, string apiUrl, IReadOnlyList<string> modelHosts);

    /// <summary>Looks up the token a request presented, as a bare token or the GitHub-shaped placeholder.</summary>
    GatewayTokenState Lookup(string? presented, out GatewayGrant? grant);

    /// <summary>True when the agent of an open lease for <paramref name="providerId"/> may not connect to <paramref name="host"/> directly.</summary>
    bool BlocksHost(Guid? providerId, string host);
}

public sealed class ModelGatewayRegistry : IModelGatewayRegistry
{
    public const string TokenPrefix = "ildgw_";
    private static readonly TimeSpan EndedRetention = TimeSpan.FromHours(1);

    private sealed class Entry(GatewayGrant grant, IReadOnlyList<string> hosts)
    {
        public GatewayGrant Grant { get; } = grant;
        public IReadOnlyList<string> Hosts { get; } = hosts;
        public DateTime? EndedAt { get; set; }
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly TimeProvider _clock;

    public ModelGatewayRegistry(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    public ModelGatewayLease Open(Guid providerId, Guid ownerId, string apiUrl, IReadOnlyList<string> modelHosts)
    {
        Prune();
        var token = TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var key = KeyOf(token);
        var entry = new Entry(new GatewayGrant(providerId, ownerId), modelHosts);
        _entries[key] = entry;
        return new ModelGatewayLease(token, apiUrl.TrimEnd('/') + "/gateway", () => entry.EndedAt = _clock.GetUtcNow().UtcDateTime);
    }

    public GatewayTokenState Lookup(string? presented, out GatewayGrant? grant)
    {
        grant = null;
        if (string.IsNullOrEmpty(presented)) return GatewayTokenState.Unknown;
        var token = presented.StartsWith("gho_" + TokenPrefix, StringComparison.Ordinal) ? presented[4..] : presented;
        if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal)) return GatewayTokenState.Unknown;
        if (!_entries.TryGetValue(KeyOf(token), out var entry)) return GatewayTokenState.Unknown;
        if (entry.EndedAt is not null) return GatewayTokenState.Ended;
        grant = entry.Grant;
        return GatewayTokenState.Active;
    }

    public bool BlocksHost(Guid? providerId, string host)
    {
        if (providerId is not { } id) return false;
        foreach (var entry in _entries.Values)
        {
            if (entry.EndedAt is not null || entry.Grant.ProviderId != id) continue;
            foreach (var pattern in entry.Hosts)
                if (Matches(pattern, host)) return true;
        }
        return false;
    }

    private static bool Matches(string pattern, string host)
        => pattern.StartsWith('.')
            ? host.Equals(pattern[1..], StringComparison.OrdinalIgnoreCase) || host.EndsWith(pattern, StringComparison.OrdinalIgnoreCase)
            : host.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    private void Prune()
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime - EndedRetention;
        foreach (var (key, entry) in _entries)
            if (entry.EndedAt is { } ended && ended < cutoff)
                _entries.TryRemove(key, out _);
    }

    private static string KeyOf(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
