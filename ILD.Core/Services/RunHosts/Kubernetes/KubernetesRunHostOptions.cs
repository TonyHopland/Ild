namespace ILD.Core.Services.RunHosts.Kubernetes;

/// <summary>
/// The settings of the <c>kubernetes</c> run host. Every name is documented in <c>docs/configuration.md</c>.
/// </summary>
public sealed record KubernetesRunHostOptions
{
    public const string NamespaceSetting = "ILD_K8S_NAMESPACE";
    public const string InstanceSetting = "ILD_K8S_INSTANCE";
    public const string RunnerImageSetting = "ILD_K8S_RUNNER_IMAGE";
    public const string StorageClassSetting = "ILD_K8S_STORAGE_CLASS";
    public const string StorageSizeSetting = "ILD_K8S_STORAGE_SIZE";
    public const string CpuRequestSetting = "ILD_K8S_CPU_REQUEST";
    public const string MemoryRequestSetting = "ILD_K8S_MEMORY_REQUEST";
    public const string CpuLimitSetting = "ILD_K8S_CPU_LIMIT";
    public const string MemoryLimitSetting = "ILD_K8S_MEMORY_LIMIT";
    public const string IldPodSelectorSetting = "ILD_K8S_ILD_POD_SELECTOR";
    public const string EgressProxyPortSetting = "ILD_NETWORK_PROXY_PORT";
    public const string SweepIntervalSetting = "ILD_K8S_SWEEP_INTERVAL_MINUTES";
    public const string ReadyTimeoutSetting = "ILD_K8S_READY_TIMEOUT_SECONDS";
    public const string KubeconfigSetting = "ILD_K8S_KUBECONFIG";

    private static readonly string[] GitIdentitySettings = ["GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL"];

    /// <summary>The namespace the run objects live in; null means the namespace ILD itself runs in.</summary>
    public string? Namespace { get; init; }

    /// <summary>Names this ILD install among others sharing the namespace; the sweeper touches only objects of its own.</summary>
    public string Instance { get; init; } = "default";

    public string RunnerImage { get; init; } = string.Empty;

    /// <summary>The StorageClass of a run's volume; null leaves the cluster's default class to decide.</summary>
    public string? StorageClass { get; init; }

    public string StorageSize { get; init; } = "10Gi";
    public string CpuRequest { get; init; } = "100m";
    public string MemoryRequest { get; init; } = "256Mi";
    public string CpuLimit { get; init; } = "4";
    public string MemoryLimit { get; init; } = "8Gi";

    /// <summary>The labels of ILD's own pods, which the run's network policy lets the run reach and be reached by.</summary>
    public IReadOnlyDictionary<string, string> IldPodSelector { get; init; }
        = new Dictionary<string, string> { ["app.kubernetes.io/name"] = "ild" };

    /// <summary>The port of ILD's HTTP endpoint, which serves the model gateway and the MCP endpoint.</summary>
    public int IldPort { get; init; } = 8080;

    /// <summary>The port of ILD's egress proxy; 0 when there is none.</summary>
    public int EgressProxyPort { get; init; }

    /// <summary>The git author and committer identity ILD was given (<c>GIT_AUTHOR_NAME</c> and the like), which the pod has no <c>.gitconfig</c> to take from.</summary>
    public IReadOnlyDictionary<string, string> GitIdentity { get; init; } = new Dictionary<string, string>();

    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>A kubeconfig file to reach the cluster with, for an ILD that does not run in it; null means the pod's service account.</summary>
    public string? Kubeconfig { get; init; }

    /// <summary>ILD's address as the run's pods reach it (<c>ILD_API_URL</c>): ILD's Service, never a loopback address.</summary>
    public string IldUrl { get; init; } = string.Empty;

    public static KubernetesRunHostOptions From(Func<string, string?> setting)
    {
        string? Value(string name) => string.IsNullOrWhiteSpace(setting(name)) ? null : setting(name)!.Trim();

        var defaults = new KubernetesRunHostOptions();
        var ildUrl = Value("ILD_API_URL")
            ?? throw new InvalidOperationException("ILD_API_URL must be ILD's Service address when ILD_RUN_HOST=kubernetes: the run's pods reach the model gateway and the MCP endpoint there.");
        if (!Uri.TryCreate(ildUrl, UriKind.Absolute, out var uri) || uri.IsLoopback)
            throw new InvalidOperationException($"ILD_API_URL is '{ildUrl}'; with ILD_RUN_HOST=kubernetes it must be an address the run's pods can reach, not a loopback one.");

        var image = Value(RunnerImageSetting)
            ?? throw new InvalidOperationException($"{RunnerImageSetting} is required when ILD_RUN_HOST=kubernetes.");

        return defaults with
        {
            Namespace = Value(NamespaceSetting),
            Instance = Value(InstanceSetting) ?? defaults.Instance,
            RunnerImage = image,
            StorageClass = Value(StorageClassSetting),
            StorageSize = Value(StorageSizeSetting) ?? defaults.StorageSize,
            CpuRequest = Value(CpuRequestSetting) ?? defaults.CpuRequest,
            MemoryRequest = Value(MemoryRequestSetting) ?? defaults.MemoryRequest,
            CpuLimit = Value(CpuLimitSetting) ?? defaults.CpuLimit,
            MemoryLimit = Value(MemoryLimitSetting) ?? defaults.MemoryLimit,
            IldPodSelector = Value(IldPodSelectorSetting) is { } selector ? ParseSelector(selector) : defaults.IldPodSelector,
            IldPort = uri.Port,
            EgressProxyPort = int.TryParse(Value(EgressProxyPortSetting), out var proxyPort) ? proxyPort : 0,
            SweepInterval = TimeSpan.FromMinutes(PositiveInt(Value(SweepIntervalSetting), SweepIntervalSetting) ?? 10),
            ReadyTimeout = TimeSpan.FromSeconds(PositiveInt(Value(ReadyTimeoutSetting), ReadyTimeoutSetting) ?? 300),
            GitIdentity = GitIdentitySettings.Select(name => (name, value: Value(name))).Where(e => e.value is not null).ToDictionary(e => e.name, e => e.value!),
            Kubeconfig = Value(KubeconfigSetting),
            IldUrl = ildUrl,
        };
    }

    private static int? PositiveInt(string? value, string name)
    {
        if (value is null) return null;
        return int.TryParse(value, out var n) && n > 0
            ? n
            : throw new InvalidOperationException($"{name} is '{value}'; it must be a positive whole number.");
    }

    private static IReadOnlyDictionary<string, string> ParseSelector(string selector)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in selector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                throw new InvalidOperationException($"{IldPodSelectorSetting} is '{selector}'; it must be a comma-separated list of key=value labels.");
            labels[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
        }
        return labels;
    }
}
