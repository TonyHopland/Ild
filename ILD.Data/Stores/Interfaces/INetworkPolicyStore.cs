using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Data.Stores.Interfaces;

/// <summary>
/// The whitelist/blacklist entries and the visited-destination log behind the
/// egress proxy. Both are plain tables rather than an <see cref="AppSetting"/>
/// value: the lists have no size ceiling and the log is append-heavy.
/// </summary>
public interface INetworkPolicyStore
{
    Task<IReadOnlyList<NetworkPolicyEntry>> GetEntriesAsync(CancellationToken ct = default);
    Task<NetworkPolicyEntry?> GetEntryAsync(Guid id, CancellationToken ct = default);

    /// <summary>The one entry with exactly this pattern, list and scope, if it exists.</summary>
    Task<NetworkPolicyEntry?> FindEntryAsync(NetworkListKind kind, string host, Guid? aiProviderId, CancellationToken ct = default, bool installers = false);
    Task AddEntryAsync(NetworkPolicyEntry entry, CancellationToken ct = default);

    /// <summary>Adds all of the entries in one save: all of them are stored or none is.</summary>
    Task AddEntriesAsync(IReadOnlyList<NetworkPolicyEntry> entries, CancellationToken ct = default);

    /// <summary>Replaces the pattern, list and scope of an entry, keeping its Id and CreatedAt. Null when no entry has this id.</summary>
    Task<NetworkPolicyEntry?> UpdateEntryAsync(Guid id, string host, NetworkListKind kind, Guid? aiProviderId, CancellationToken ct = default, bool installers = false);
    Task<bool> DeleteEntryAsync(Guid id, CancellationToken ct = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<NetworkLogEntry>> GetLogAsync(int take, CancellationToken ct = default);
    Task<NetworkLogEntry?> GetLogEntryAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// The hosts logged since <paramref name="since"/> for a scope, one row for each host and provider that used it: the installers' connections, or the agent
    /// connections of one provider (every provider when null). A connection refused for a reason the lists do not control is left out.
    /// </summary>
    Task<IReadOnlyList<NetworkLogDestination>> GetLogDestinationsAsync(Guid? aiProviderId, bool installers, DateTime since, CancellationToken ct = default);
    Task AppendLogAsync(IReadOnlyList<NetworkLogEntry> entries, CancellationToken ct = default);
    Task<int> ClearLogAsync(CancellationToken ct = default);

    /// <summary>Drops every log row stamped before <paramref name="cutoff"/>, and says how many.</summary>
    Task<int> DeleteLogOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
}

public sealed record NetworkLogDestination(string Host, Guid? AiProviderId, int Count, DateTime LastSeen);
