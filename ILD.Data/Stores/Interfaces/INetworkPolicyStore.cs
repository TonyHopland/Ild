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
    Task AddEntryAsync(NetworkPolicyEntry entry, CancellationToken ct = default);
    Task<bool> DeleteEntryAsync(Guid id, CancellationToken ct = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<NetworkLogEntry>> GetLogAsync(int take, CancellationToken ct = default);
    Task<NetworkLogEntry?> GetLogEntryAsync(Guid id, CancellationToken ct = default);
    Task AppendLogAsync(IReadOnlyList<NetworkLogEntry> entries, CancellationToken ct = default);
    Task<int> ClearLogAsync(CancellationToken ct = default);
}
