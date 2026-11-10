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
    public const string GatewayTlsPortSetting = "ILD_GATEWAY_TLS_PORT";
    public const string SweepIntervalSetting = "ILD_K8S_SWEEP_INTERVAL_MINUTES";
    public const string ReadyTimeoutSetting = "ILD_K8S_READY_TIMEOUT_SECONDS";
    public const string KubeconfigSetting = "ILD_K8S_KUBECONFIG";
    public const string ToolsClaimSetting = "ILD_K8S_TOOLS_CLAIM";
    public const string IldNamespaceSetting = "ILD_K8S_ILD_NAMESPACE";

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

    /// <summary>The CPU and memory limits of a run's pod; null is no limit, which is what a setting of <c>0</c> means.</summary>
    public string? CpuLimit { get; init; } = "4";
    public string? MemoryLimit { get; init; } = "8Gi";

    /// <summary>The labels of ILD's own pods, which the run's network policy lets the run reach and be reached by.</summary>
    public IReadOnlyDictionary<string, string> IldPodSelector { get; init; }
        = new Dictionary<string, string> { ["app.kubernetes.io/name"] = "ild" };

    /// <summary>The namespace ILD runs in, which is the only one the run's network policy accepts ILD's pods from; set from the setting or, in the cluster, from ILD's service account.</summary>
    public string IldNamespace { get; init; } = string.Empty;

    /// <summary>The port of ILD's HTTP endpoint, which serves the model gateway and the MCP endpoint.</summary>
    public int IldPort { get; init; } = 8080;

    /// <summary>The port of ILD's egress proxy; 0 when there is none.</summary>
    public int EgressProxyPort { get; init; }

    /// <summary>The port of the model gateway's HTTPS listener, which the Copilot CLI is pointed at; 0 when there is none.</summary>
    public int GatewayTlsPort { get; init; } = ILD.Core.Services.Implementations.ModelGateway.GatewayTlsEndpoint.DefaultPort;

    /// <summary>The git author and committer identity ILD was given (<c>GIT_AUTHOR_NAME</c> and the like), which the pod has no <c>.gitconfig</c> to take from.</summary>
    public IReadOnlyDictionary<string, string> GitIdentity { get; init; } = new Dictionary<string, string>();

    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>How long the chat pod may be idle before the sweep deletes it; zero never deletes it.</summary>
    public TimeSpan ChatIdleTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>A kubeconfig file to reach the cluster with, for an ILD that does not run in it; null means the pod's service account.</summary>
    public string? Kubeconfig { get; init; }

    /// <summary>ILD's address as the run's pods reach it (<c>ILD_API_URL</c>): ILD's Service, never a loopback address.</summary>
    public string IldUrl { get; init; } = string.Empty;

    /// <summary>Whether runs get toolchains and agents from the store (<see cref="RunHostOptions.ToolchainsSetting"/>).</summary>
    public bool Toolchains { get; init; } = true;

    /// <summary>
    /// The ReadWriteMany claim, in the run namespace, that holds the toolchain store; null when there is none, and runs
    /// then have only what the runner image has.
    /// </summary>
    public string? ToolsClaim { get; init; }

    /// <summary>Whether runs mount the store: toolchains on and a claim to hold it.</summary>
    public bool UsesToolStore => Toolchains && ToolsClaim is not null;

    public static KubernetesRunHostOptions From(Func<string, string?> setting)
    {
        string? Value(string name) => string.IsNullOrWhiteSpace(setting(name)) ? null : setting(name)!.Trim();

        var defaults = new KubernetesRunHostOptions();
        var ildUrl = Value("ILD_API_URL")
            ?? throw new InvalidOperationException("ILD_API_URL must be ILD's Service address when ILD runs in Kubernetes: the run's pods reach the model gateway and the MCP endpoint there.");
        if (!Uri.TryCreate(ildUrl, UriKind.Absolute, out var uri) || uri.IsLoopback)
            throw new InvalidOperationException($"ILD_API_URL is '{ildUrl}'; with runs in pods it must be an address the run's pods can reach, not a loopback one.");

        var image = Value(RunnerImageSetting)
            ?? throw new InvalidOperationException($"{RunnerImageSetting} is required when ILD runs in Kubernetes.");

        return defaults with
        {
            Namespace = Value(NamespaceSetting),
            Instance = CheckedInstance(Value(InstanceSetting)) ?? defaults.Instance,
            RunnerImage = image,
            StorageClass = Value(StorageClassSetting),
            StorageSize = Value(StorageSizeSetting) ?? defaults.StorageSize,
            CpuRequest = Value(CpuRequestSetting) ?? defaults.CpuRequest,
            MemoryRequest = Value(MemoryRequestSetting) ?? defaults.MemoryRequest,
            CpuLimit = Limit(Value(RunHostOptions.CpuLimitSetting) ?? Value(CpuLimitSetting) ?? defaults.CpuLimit),
            MemoryLimit = Limit(Value(RunHostOptions.MemoryLimitSetting) ?? Value(MemoryLimitSetting) ?? defaults.MemoryLimit),
            IldPodSelector = Value(IldPodSelectorSetting) is { } selector ? ParseSelector(selector) : defaults.IldPodSelector,
            IldPort = uri.Port,
            EgressProxyPort = int.TryParse(Value(EgressProxyPortSetting), out var proxyPort) ? proxyPort : 0,
            GatewayTlsPort = ILD.Core.Services.Implementations.ModelGateway.GatewayTlsEndpoint.PortFrom(Value(GatewayTlsPortSetting)) ?? 0,
            SweepInterval = TimeSpan.FromMinutes(PositiveInt(Value(SweepIntervalSetting), SweepIntervalSetting) ?? 10),
            ReadyTimeout = TimeSpan.FromSeconds(PositiveInt(Value(ReadyTimeoutSetting), ReadyTimeoutSetting) ?? 300),
            GitIdentity = GitIdentitySettings.Select(name => (name, value: Value(name))).Where(e => e.value is not null).ToDictionary(e => e.name, e => e.value!),
            Kubeconfig = Value(KubeconfigSetting),
            IldUrl = ildUrl,
            ChatIdleTimeout = RunHostOptions.ChatIdleTimeoutOf(Value(RunHostOptions.ChatIdleTimeoutSetting)),
            Toolchains = RunHostOptions.ToolchainsOn(Value(RunHostOptions.ToolchainsSetting)),
            ToolsClaim = Value(ToolsClaimSetting),
            IldNamespace = Value(IldNamespaceSetting) ?? string.Empty,
        };
    }

    // 0 means no limit, as it does for the container host; the API refuses a limit below the request.
    private static string? Limit(string? quantity)
        => quantity is not null && decimal.TryParse(quantity, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n) && n == 0 ? null : quantity;

    // The instance is in the name of the chat pod ("ild-chat-" + instance) and in a label value: a DNS-1123 label, short enough for both.
    private static string? CheckedInstance(string? instance)
    {
        if (instance is null) return null;
        if (instance.Length > 50 || !System.Text.RegularExpressions.Regex.IsMatch(instance, "^[a-z0-9]([-a-z0-9]*[a-z0-9])?$"))
            throw new InvalidOperationException($"{InstanceSetting} is '{instance}'; it must be lowercase letters, digits and '-', starting and ending with a letter or digit, and at most 50 characters.");
        return instance;
    }

    private static int? PositiveInt(string? value, string name)
    {
        if (value is null) return null;
        return int.TryParse(value, out var n) && n > 0
            ? n
            : throw new InvalidOperationException($"{name} is '{value}'; it must be a positive whole number.");
    }

    // An empty selector selects every pod, so nothing that parses to one gets through, and every key and value must be label syntax.
    private static IReadOnlyDictionary<string, string> ParseSelector(string selector)
    {
        var malformed = new InvalidOperationException($"{IldPodSelectorSetting} is '{selector}'; it must be a comma-separated list of key=value labels, with at least one.");
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in selector.Split(',', StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) throw malformed;
            var key = pair[..eq].Trim();
            var value = pair[(eq + 1)..].Trim();
            if (key.Length > 316 || !LabelKey.IsMatch(key) || !LabelValue.IsMatch(value) || value.Length == 0) throw malformed;
            labels[key] = value;
        }
        return labels.Count > 0 ? labels : throw malformed;
    }

    // A label key is an optional DNS subdomain prefix and a name of at most 63 characters; a value is empty or a name.
    private static readonly System.Text.RegularExpressions.Regex LabelKey = new(
        "^([a-z0-9]([-a-z0-9.]*[a-z0-9])?/)?[A-Za-z0-9]([-A-Za-z0-9_.]{0,61}[A-Za-z0-9])?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex LabelValue = new(
        "^([A-Za-z0-9]([-A-Za-z0-9_.]{0,61}[A-Za-z0-9])?)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
