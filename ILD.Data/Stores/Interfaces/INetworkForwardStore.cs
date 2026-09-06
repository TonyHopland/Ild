using ILD.Data.Entities;

namespace ILD.Data.Stores.Interfaces;

/// <summary>
/// The declared TCP forwards the orchestrator serves on loopback. Rows rather
/// than configuration, so adding one takes effect on the next connection without
/// a restart, exactly as a list edit does.
/// </summary>
public interface INetworkForwardStore
{
    /// <summary>Ordered by local port, which is how they are read and shown.</summary>
    Task<IReadOnlyList<NetworkForwardEntry>> GetForwardsAsync(CancellationToken ct = default);

    /// <summary>The forward already holding this local port, if any.</summary>
    Task<NetworkForwardEntry?> FindByLocalPortAsync(int localPort, CancellationToken ct = default);

    Task AddForwardAsync(NetworkForwardEntry forward, CancellationToken ct = default);
    Task<bool> DeleteForwardAsync(Guid id, CancellationToken ct = default);
}
