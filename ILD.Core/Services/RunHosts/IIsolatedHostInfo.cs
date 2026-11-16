using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.RunHosts.Isolated;

namespace ILD.Core.Services.RunHosts;

/// <summary>What a run host that puts runs in pods or containers says about them and their network, for the settings page, the sweep and the destination guard. A host with no such environment does not implement it.</summary>
public interface IIsolatedHostInfo
{
    /// <summary>How often the sweep looks for what finished runs left behind.</summary>
    TimeSpan SweepInterval { get; }

    /// <summary>The address ranges the runs' networks use, which no run may reach.</summary>
    Task<IReadOnlyList<IpRange>> IsolatedRangesAsync(CancellationToken ct = default);

    /// <summary>The addresses of the cluster's control plane, which no run may reach.</summary>
    Task<IReadOnlyList<System.Net.IPAddress>> ControlPlaneAddressesAsync(CancellationToken ct = default);

    /// <summary>A problem with the engine the operator should see, or null when there is none or the engine cannot say.</summary>
    Task<string?> WarningAsync(CancellationToken ct = default);

    /// <summary>The runs and chats that have a direct route to the network now (network mode Off).</summary>
    Task<IReadOnlyList<DirectRoute>> DirectRoutesAsync(CancellationToken ct = default);
}
