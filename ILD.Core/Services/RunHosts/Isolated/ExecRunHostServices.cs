using ILD.Core.Services.Implementations.Network;

namespace ILD.Core.Services.RunHosts.Isolated;

/// <summary>What ILD gives an isolated run host when it makes it, besides its settings. Each is null where the host has none.</summary>
public sealed record ExecRunHostServices
{
    /// <summary>The declared forwards, read when a command or terminal starts in a run or chat.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<RunForward>>>? Forwards { get; init; }

    /// <summary>Checks, from the first environment made, that the network leaves no way out but the proxy.</summary>
    public NetworkEnforcementProbe? Probe { get; init; }

    /// <summary>What the egress proxy refused each toolchain installer, to name in a failed install.</summary>
    public InstallerRefusals? Refusals { get; init; }

    /// <summary>Where the preview data repositories keep between runs is stored.</summary>
    public PreviewDataStore? PreviewData { get; init; }
}
