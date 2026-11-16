using ILD.Core.Services.Implementations.PackageFeeds;

namespace ILD.Core.Services.Implementations;

internal sealed class PreviewRuntime
{
    public PreviewRuntime(
        string worktreePath,
        string configPath,
        string profileName,
        string stateDirectory,
        string logDirectory,
        string publicHost,
        Dictionary<string, int> ports,
        List<ManagedPreviewProcess> processes,
        IReadOnlyDictionary<string, string> customEnv,
        PackageFeedFileShare feeds,
        IReadOnlyDictionary<string, string> volumes,
        PreviewDataClaims.Lease? previewDataLease = null,
        string? workItemId = null)
    {
        Feeds = feeds;
        Volumes = volumes;
        PreviewDataLease = previewDataLease;
        WorkItemId = workItemId;
        WorktreePath = worktreePath;
        ConfigPath = configPath;
        ProfileName = profileName;
        StateDirectory = stateDirectory;
        LogDirectory = logDirectory;
        PublicHost = publicHost;
        Ports = ports;
        _processes = processes.ToArray();
        CustomEnv = customEnv;
    }

    /// <summary>
    /// The work item this worktree belongs to, when the starter knew it. Only
    /// used to build <c>wi-{id}.{proxy base}</c> public URLs; null leaves those
    /// URLs on their loopback form.
    /// </summary>
    public string? WorkItemId { get; }

    /// <summary>How many services the profile defines, so a preview with fewer running reads as partial.</summary>
    public int ServiceCount { get; init; }

    public string WorktreePath { get; }
    public string ConfigPath { get; }
    public string ProfileName { get; }

    /// <summary>Shared with the agent — see <c>BuildStateDirectory</c>.</summary>
    public string StateDirectory { get; }

    /// <summary>Orchestrator-private — see <c>BuildLogDirectory</c>.</summary>
    public string LogDirectory { get; }

    public string PublicHost { get; }
    public Dictionary<string, int> Ports { get; }

    private volatile IReadOnlyList<ManagedPreviewProcess> _processes;

    /// <summary>
    /// The service processes started for this runtime. The list is replaced
    /// wholesale on every change rather than mutated in place, so a reader always
    /// enumerates a complete snapshot and can never see the collection halfway
    /// through a modification.
    /// <para>
    /// The writers here are already serialized by the service's start/stop gate,
    /// but that gate is held across process launches and health probes — seconds
    /// to minutes — and the preview proxy reads this on <em>every</em> HTTP request
    /// to a preview hostname: every asset, XHR and reload poll. Making readers wait
    /// on the gate would stall a loading page behind a service restart, so instead
    /// the read takes no lock and the writer pays for a copy of a list that never
    /// holds more than a handful of entries.
    /// </para>
    /// </summary>
    public IReadOnlyList<ManagedPreviewProcess> Processes => _processes;

    public void AddProcess(ManagedPreviewProcess process)
        => _processes = [.. _processes, process];

    public void RemoveProcess(ManagedPreviewProcess process)
        => _processes = _processes.Where(p => !ReferenceEquals(p, process)).ToArray();

    public void ClearProcesses() => _processes = [];

    /// <summary>Parsed repository custom <c>.env</c> (see <c>Repository.PreviewEnv</c>),
    /// injected into every step's environment. Empty when none is configured.</summary>
    public IReadOnlyDictionary<string, string> CustomEnv { get; }

    /// <summary>
    /// The selected package feeds' credentials. Their file exists only while an
    /// install run or a service of this runtime is running; each holds a lease.
    /// </summary>
    public PackageFeedFileShare Feeds { get; }

    /// <summary>
    /// Each declared volume's folder by name (any case), fixed when the runtime is
    /// created: a later change of the repository's setting does not move it.
    /// </summary>
    public IReadOnlyDictionary<string, string> Volumes { get; }

    /// <summary>
    /// This preview's hold on its repository's preview data, released when the
    /// runtime leaves the registry (<c>RetireRuntimeAsync</c>); null without a repository.
    /// </summary>
    public PreviewDataClaims.Lease? PreviewDataLease { get; }
}
