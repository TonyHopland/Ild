using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Tools;
using k8s.Models;

namespace ILD.Core.Services.RunHosts.Kubernetes;

/// <summary>
/// The objects of one run. The ConfigMap is the owner: the volume claim, the network policy and the pod carry an
/// owner reference to it, so deleting it removes whatever is left. ILD deletes the pod first and the policies only after it,
/// so a pod is never without its policy.
/// </summary>
internal sealed class KubernetesManifests(KubernetesRunHostOptions options, string namespaceName)
{
    internal const uint RunnerUid = 10001;
    private const string WorkVolume = "work";
    private const string StateVolume = "state";
    private const string RunnerContainer = "runner";
    private const string ToolsVolume = "tools";

    private Dictionary<string, string> Labels(Guid ownerId) => RunNames.Labels(options.Instance, runId: ownerId);

    private V1ObjectMeta Meta(Guid ownerId, V1OwnerReference? owner = null) => new()
    {
        Name = RunNames.Run(ownerId),
        NamespaceProperty = namespaceName,
        Labels = Labels(ownerId),
        OwnerReferences = owner is null ? null : [owner],
    };

    // No run label: the sweep, which deletes what a run left, never takes the chat objects for a run's. No owner reference
    // either: the chat pod is replaced when it ends, and the claim, with the chats' folders, outlives it.
    private V1ObjectMeta ChatMeta() => new()
    {
        Name = RunNames.Chat(options.Instance),
        NamespaceProperty = namespaceName,
        Labels = RunNames.Labels(options.Instance, RunPaths.ChatRole),
    };

    public V1PersistentVolumeClaim ChatClaim() => new() { Metadata = ChatMeta(), Spec = ClaimSpec() };

    // What the run was made from is kept with it, so a pod that is gone is made again without the loop having to say it twice.
    public V1ConfigMap Owner(Guid ownerId, RunEnvironmentRequest? request = null) => new()
    {
        Metadata = Meta(ownerId),
        Data = new Dictionary<string, string> { ["run"] = ownerId.ToString("N") }
            .Concat(request is null ? [] : RequestData(request)).ToDictionary(p => p.Key, p => p.Value),
    };

    /// <summary>The merge patch that gives an owner made before the request was kept the request.</summary>
    public static V1Patch RequestPatch(RunEnvironmentRequest request)
        => new(new { data = RequestData(request).ToDictionary(p => p.Key, p => p.Value) }, V1Patch.PatchType.MergePatch);

    private static IEnumerable<KeyValuePair<string, string>> RequestData(RunEnvironmentRequest request) =>
    [
        new("basePath", request.BasePath), new("branch", request.BranchName), new("repository", request.RepositoryId.ToString("D")),
        new("dns", request.Dns ? "true" : "false"), new("keepPreviewData", request.KeepPreviewData ? "true" : "false"),
    ];

    /// <summary>The request a run's owner ConfigMap was made with; null for one made without (an older run).</summary>
    public static RunEnvironmentRequest? RequestOf(Guid ownerId, V1ConfigMap owner)
        => owner.Data is { } d && d.TryGetValue("basePath", out var basePath) && d.TryGetValue("branch", out var branch)
            && d.TryGetValue("repository", out var repository) && Guid.TryParse(repository, out var repositoryId)
            ? new RunEnvironmentRequest(ownerId, basePath, branch, repositoryId, d.TryGetValue("dns", out var dns) && dns == "true", d.TryGetValue("keepPreviewData", out var keep) && keep == "true")
            : null;

    public static V1OwnerReference OwnerReference(V1ConfigMap owner) => new()
    {
        ApiVersion = "v1",
        Kind = "ConfigMap",
        Name = owner.Metadata.Name,
        Uid = owner.Metadata.Uid,
        BlockOwnerDeletion = true,
    };

    public V1PersistentVolumeClaim Claim(Guid ownerId, V1OwnerReference owner) => new()
    {
        Metadata = Meta(ownerId, owner),
        Spec = ClaimSpec(),
    };

    private V1PersistentVolumeClaimSpec ClaimSpec() => new()
    {
        AccessModes = ["ReadWriteOnce"],
        StorageClassName = options.StorageClass,
        Resources = new V1VolumeResourceRequirements
        {
            Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new(options.StorageSize) },
        },
    };

    /// <summary>
    /// Egress only to ILD (the gateway, the MCP endpoint and the egress proxy), and to DNS when <paramref name="dns"/>; ingress only from ILD.
    /// What a run's pod can reach beyond that is reached through ILD's egress proxy, which alone resolves names.
    /// </summary>
    public V1NetworkPolicy Policy(Guid ownerId, V1OwnerReference owner, bool dns = false)
        => Policy(Meta(ownerId, owner), new Dictionary<string, string> { [RunPaths.RunLabel] = ownerId.ToString("N"), [RunPaths.InstanceLabel] = options.Instance }, dns);

    /// <summary>
    /// What a run's pod has while a launch is in network mode Off: egress to anywhere, DNS included. NetworkPolicies add up, so this
    /// opens what <see cref="Policy(Guid, V1OwnerReference, bool)"/> closes; deleting it closes it again. It belongs to the run's owner like the others.
    /// </summary>
    public V1NetworkPolicy OpenPolicy(Guid ownerId, V1OwnerReference owner)
    {
        var meta = Meta(ownerId, owner);
        meta.Name = RunNames.OpenPolicy(meta.Name);
        meta.Labels[RunPaths.RoleLabel] = RunPaths.OpenRole;
        return Open(meta, new Dictionary<string, string> { [RunPaths.RunLabel] = ownerId.ToString("N"), [RunPaths.InstanceLabel] = options.Instance });
    }

    /// <summary>The same for the chat pod. It has no run label and no owner: it is deleted when the route goes, and the sweep removes one that outlived its pod.</summary>
    public V1NetworkPolicy ChatOpenPolicy()
    {
        var meta = ChatMeta();
        meta.Name = RunNames.OpenPolicy(meta.Name);
        meta.Labels[RunPaths.RoleLabel] = RunPaths.OpenRole;
        return Open(meta, new Dictionary<string, string> { [RunPaths.RoleLabel] = RunPaths.ChatRole, [RunPaths.InstanceLabel] = options.Instance });
    }

    private static V1NetworkPolicy Open(V1ObjectMeta metadata, IDictionary<string, string> podLabels) => new()
    {
        Metadata = metadata,
        Spec = new V1NetworkPolicySpec
        {
            PodSelector = new V1LabelSelector { MatchLabels = podLabels },
            PolicyTypes = ["Egress"],
            Egress = [new V1NetworkPolicyEgressRule()],
        },
    };

    /// <summary>The same rules for the chat pod, which no run's policy selects.</summary>
    public V1NetworkPolicy ChatPolicy()
        => Policy(ChatMeta(), new Dictionary<string, string> { [RunPaths.RoleLabel] = RunPaths.ChatRole, [RunPaths.InstanceLabel] = options.Instance }, dns: false);

    /// <summary>
    /// The installer's NetworkPolicy: egress only to the egress proxy on ILD's pod, no DNS and no ingress. Its downloads reach whatever
    /// the proxy lets through under the installers' scope, and an install script reaches nothing else.
    /// </summary>
    public V1NetworkPolicy ToolsPolicy(Guid installer)
    {
        var name = RunNames.Installer(installer);
        return new V1NetworkPolicy
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = namespaceName,
                Labels = new Dictionary<string, string>(RunNames.Labels(options.Instance, RunPaths.ToolsRole)) { [KubernetesNames.InstallerLabel] = installer.ToString("N") },
            },
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { [KubernetesNames.InstallerLabel] = installer.ToString("N") } },
                PolicyTypes = ["Ingress", "Egress"],
                Egress =
                [
                    new V1NetworkPolicyEgressRule
                    {
                        To = [new V1NetworkPolicyPeer { PodSelector = new V1LabelSelector { MatchLabels = new Dictionary<string, string>(options.IldPodSelector) }, NamespaceSelector = IldNamespaceSelector() }],
                        Ports = [new V1NetworkPolicyPort { Protocol = "TCP", Port = options.EgressProxyPort }],
                    },
                ],
                Ingress = [],
            },
        };
    }

    private V1NetworkPolicy Policy(V1ObjectMeta metadata, IDictionary<string, string> podLabels, bool dns)
    {
        var ild = new V1LabelSelector { MatchLabels = new Dictionary<string, string>(options.IldPodSelector) };
        var ports = new List<V1NetworkPolicyPort> { new() { Protocol = "TCP", Port = options.IldPort } };
        if (options.EgressProxyPort > 0)
            ports.Add(new V1NetworkPolicyPort { Protocol = "TCP", Port = options.EgressProxyPort });
        if (options.GatewayTlsPort > 0)
            ports.Add(new V1NetworkPolicyPort { Protocol = "TCP", Port = options.GatewayTlsPort });

        return new V1NetworkPolicy
        {
            Metadata = metadata,
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = new V1LabelSelector { MatchLabels = podLabels },
                PolicyTypes = ["Ingress", "Egress"],
                Egress =
                [
                    new V1NetworkPolicyEgressRule
                    {
                        To = [new V1NetworkPolicyPeer { PodSelector = ild, NamespaceSelector = IldNamespaceSelector() }],
                        Ports = ports,
                    },
                    .. dns ? new[] { DnsEgress() } : [],
                ],
                Ingress =
                [
                    new V1NetworkPolicyIngressRule
                    {
                        FromProperty = [new V1NetworkPolicyPeer { PodSelector = ild, NamespaceSelector = IldNamespaceSelector() }],
                    },
                ],
            },
        };
    }

    // ILD's own namespace and no other: a pod elsewhere in the cluster with ILD's labels is not ILD.
    private V1LabelSelector IldNamespaceSelector()
        => new() { MatchLabels = new Dictionary<string, string> { ["kubernetes.io/metadata.name"] = options.IldNamespace } };

    /// <summary>
    /// What the sign-in pod needs now that the namespace denies by default: egress to anywhere, so the OAuth flow can reach the
    /// provider, and no ingress. It is made before the pod and deleted after the pod is gone.
    /// </summary>
    public V1NetworkPolicy SignInPolicy(string name, Guid id) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name,
            NamespaceProperty = namespaceName,
            Labels = RunNames.Labels(options.Instance, RunPaths.SignInRole),
        },
        Spec = new V1NetworkPolicySpec
        {
            PodSelector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { [KubernetesNames.SignInLabel] = id.ToString("N") } },
            PolicyTypes = ["Ingress", "Egress"],
            Egress = [new V1NetworkPolicyEgressRule()],
            Ingress = [],
        },
    };

    private static V1NetworkPolicyEgressRule DnsEgress()
        => new()
        {
            To =
            [
                new V1NetworkPolicyPeer
                {
                    PodSelector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["k8s-app"] = "kube-dns" } },
                    NamespaceSelector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["kubernetes.io/metadata.name"] = "kube-system" } },
                },
            ],
            Ports = [new V1NetworkPolicyPort { Protocol = "UDP", Port = 53 }, new V1NetworkPolicyPort { Protocol = "TCP", Port = 53 }],
        };

    private List<string> IldNames()
    {
        var host = new Uri(options.IldUrl).Host;
        var facing = ExecRunHost.RunFacingHost(host);
        return facing == host ? [host] : [host, facing];
    }

    // A cluster autoscaler that drains a node on scale-down skips a pod with this annotation: a run's pod holds the run's work, and a node
    // that is removed under it takes the pod object with it.
    internal const string SafeToEvictAnnotation = "cluster-autoscaler.kubernetes.io/safe-to-evict";

    public V1Pod Pod(Guid ownerId, V1OwnerReference owner, RunToolEnvironment? tools = null, string? ildAddress = null, bool dns = false, string? nameServer = null)
    {
        var pod = BuildPod(Meta(ownerId, owner), RunPaths.RunRoot(ownerId), RunPaths.HomeDirectory(ownerId), RunNames.Run(ownerId), tools?.For(RunPaths.HomeDirectory(ownerId)), tools?.Zones ?? [], limits: true, ildAddress, dns, nameServer);
        pod.Metadata.Annotations = new Dictionary<string, string> { [SafeToEvictAnnotation] = "false" };
        if (options.RunNodeSelector.Count > 0) pod.Spec.NodeSelector = options.RunNodeSelector.ToDictionary(p => p.Key, p => p.Value);
        if (options.RunTolerationSeconds is { } seconds)
            pod.Spec.Tolerations = new[] { "node.kubernetes.io/not-ready", "node.kubernetes.io/unreachable" }
                .Select(key => new V1Toleration { Key = key, OperatorProperty = "Exists", Effect = "NoExecute", TolerationSeconds = seconds }).ToList();
        return pod;
    }

    // The zones every chat mounts: the agents' and the shared toolchains, never a repository's own tools.
    private static readonly RunToolMount[] ChatToolZones = [new(RunToolZone.Agents), new(RunToolZone.Toolchains)];

    /// <summary>
    /// The pod that keeps every chat, long-lived: the claim of the chats at their root, and the agents' and toolchain
    /// zones of the store's claim read-only whenever there is a store, since a pod's volumes are fixed when it is made and
    /// a chat may need toolchains later. No CPU or memory limit, as chats had in ILD's own container.
    /// </summary>
    public V1Pod ChatPod(string? ildAddress = null, string? nameServer = null)
    {
        var pod = BuildPod(ChatMeta(), RunPaths.ChatsRoot, null, RunNames.Chat(options.Instance), [], options.UsesToolStore ? ChatToolZones : [], limits: false, ildAddress, dns: false, nameServer);
        pod.Metadata.Annotations = new Dictionary<string, string> { [ChatSpecStamp.Key] = ChatSpecStamp.Of(k8s.KubernetesJson.Serialize(pod.Spec)) };
        return pod;
    }

    private V1Pod BuildPod(V1ObjectMeta metadata, string root, string? home, string claimName, IEnumerable<string>? toolEnvironment, IReadOnlyList<RunToolMount> zones, bool limits, string? ildAddress, bool dns, string? nameServer = null)
    {
        return new V1Pod
        {
            Metadata = metadata,
            Spec = new V1PodSpec
            {
                AutomountServiceAccountToken = false,
                // Names are resolved only by ILD's egress proxy: ILD's own name is pinned in the pod, and the nameserver is nothing.
                // Nothing but the cluster's own DNS, which the policy lets a pod reach only while it has the direct route (network
                // mode Off): a pod's DNS cannot be changed once it is made, and what a launch in Off reaches by name needs it.
                DnsPolicy = dns ? null : "None",
                DnsConfig = dns ? null : new V1PodDNSConfig
                {
                    Nameservers = [nameServer ?? "127.0.0.1"],
                    Options = nameServer is null ? null : [new V1PodDNSConfigOption { Name = "timeout", Value = "1" }, new V1PodDNSConfigOption { Name = "attempts", Value = "1" }],
                },
                HostAliases = ildAddress is null ? null : [new V1HostAlias { Ip = ildAddress, Hostnames = IldNames() }],
                EnableServiceLinks = false,
                RestartPolicy = "Never",
                TerminationGracePeriodSeconds = 5,
                TopologySpreadConstraints =
                [
                    new V1TopologySpreadConstraint
                    {
                        MaxSkew = 1,
                        TopologyKey = "kubernetes.io/hostname",
                        WhenUnsatisfiable = "ScheduleAnyway",
                        LabelSelector = new V1LabelSelector
                        {
                            MatchLabels = new Dictionary<string, string>
                            {
                                [RunPaths.ManagedByLabel] = RunPaths.ManagedByValue,
                                [RunPaths.InstanceLabel] = options.Instance,
                            },
                        },
                    },
                ],
                SecurityContext = new V1PodSecurityContext
                {
                    RunAsNonRoot = true,
                    RunAsUser = RunnerUid,
                    RunAsGroup = RunnerUid,
                    FsGroup = RunnerUid,
                    SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" },
                },
                Containers =
                [
                    new V1Container
                    {
                        Name = RunnerContainer,
                        Image = options.RunnerImage,
                        Command = ["/usr/bin/tini", "--", "sleep", "infinity"],
                        WorkingDir = root,
                        Env =
                        [
                            .. home is null ? [] : new[] { new V1EnvVar { Name = "HOME", Value = home } },
                            .. options.GitIdentity.Select(e => new V1EnvVar { Name = e.Key, Value = e.Value }),
                            .. (toolEnvironment ?? []).Select(EnvVar),
                        ],
                        SecurityContext = new V1SecurityContext
                        {
                            AllowPrivilegeEscalation = false,
                            Capabilities = new V1Capabilities { Drop = ["ALL"] },
                        },
                        Resources = new V1ResourceRequirements
                        {
                            Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new(options.CpuRequest), ["memory"] = new(options.MemoryRequest) },
                            Limits = limits ? Limits() : null,
                        },
                        VolumeMounts =
                        [
                            new V1VolumeMount { Name = WorkVolume, MountPath = root },
                            new V1VolumeMount { Name = StateVolume, MountPath = RunPaths.StateDirectory },
                            .. ToolMounts(zones, readOnly: true),
                        ],
                    },
                ],
                Volumes =
                [
                    new V1Volume { Name = WorkVolume, PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = claimName } },
                    new V1Volume { Name = StateVolume, EmptyDir = new V1EmptyDirVolumeSource { Medium = "Memory", SizeLimit = new ResourceQuantity("64Mi") } },
                    .. ToolVolumes(zones, readOnly: true),
                ],
            },
        };
    }

    // A limit that is 0 is left out: no limit.
    private Dictionary<string, ResourceQuantity>? Limits()
    {
        var limits = new Dictionary<string, ResourceQuantity>();
        if (options.CpuLimit is { } cpu) limits["cpu"] = new(cpu);
        if (options.MemoryLimit is { } memory) limits["memory"] = new(memory);
        return limits.Count == 0 ? null : limits;
    }

    // The installer and sign-in pods ask for what a run does, so a namespace that requires requests accepts them; no limits.
    private V1ResourceRequirements Requests() => new()
    {
        Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new(options.CpuRequest), ["memory"] = new(options.MemoryRequest) },
    };

    // Each zone of the store is a folder of its claim (a subPath), so a pod mounts only the zones it names and never the
    // claim's root, where every repository's folder is.
    private IEnumerable<V1VolumeMount> ToolMounts(IEnumerable<RunToolMount> zones, bool readOnly)
        => options.ToolsClaim is null
            ? []
            : zones.Select(z => new V1VolumeMount { Name = ToolsVolume, MountPath = z.MountPath, SubPath = z.SubPath, ReadOnlyProperty = readOnly });

    private IEnumerable<V1Volume> ToolVolumes(IReadOnlyList<RunToolMount> zones, bool readOnly)
        => zones.Count > 0 && options.ToolsClaim is { } claim
            ? [new V1Volume { Name = ToolsVolume, PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = claim, ReadOnlyProperty = readOnly } }]
            : [];

    /// <summary>
    /// A short-lived pod of the runner image that mounts only the zone being installed writable (and the shared
    /// toolchains read-only for a repository's), which ILD runs the store's install script in. A first container of ILD's
    /// own, which runs a fixed <c>mkdir</c> and no repository's code, makes the zone's folder first, since the kubelet
    /// would make it as root. The pod carries no run label, so the sweep never takes it for a run's; its own
    /// <see cref="ToolsPolicy"/> lets it reach ILD's egress proxy and nothing else.
    /// </summary>
    public V1Pod ToolsInstaller(Guid installer, RunToolMount writable, RunToolMount? shared, IEnumerable<string> environment, string? ildAddress) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = RunNames.Installer(installer),
            NamespaceProperty = namespaceName,
            Labels = new Dictionary<string, string>(RunNames.Labels(options.Instance, RunPaths.ToolsRole)) { [KubernetesNames.InstallerLabel] = installer.ToString("N") },
        },
        Spec = new V1PodSpec
        {
            AutomountServiceAccountToken = false,
            // Names are resolved only by ILD's egress proxy: ILD's own name is pinned in the pod, and the nameserver is nothing.
            DnsPolicy = "None",
            DnsConfig = new V1PodDNSConfig { Nameservers = ["127.0.0.1"] },
            HostAliases = ildAddress is null ? null : [new V1HostAlias { Ip = ildAddress, Hostnames = IldNames() }],
            EnableServiceLinks = false,
            RestartPolicy = "Never",
            TerminationGracePeriodSeconds = 0,
            SecurityContext = new V1PodSecurityContext
            {
                RunAsNonRoot = true,
                RunAsUser = RunnerUid,
                RunAsGroup = RunnerUid,
                FsGroup = RunnerUid,
                SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" },
            },
            InitContainers =
            [
                new V1Container
                {
                    Name = "make-zone",
                    Image = options.RunnerImage,
                    Command = ["mkdir", "-p", $"/claim/{writable.SubPath}"],
                    SecurityContext = new V1SecurityContext
                    {
                        AllowPrivilegeEscalation = false,
                        Capabilities = new V1Capabilities { Drop = ["ALL"] },
                    },
                    Resources = Requests(),
                    VolumeMounts = [new V1VolumeMount { Name = ToolsVolume, MountPath = "/claim" }],
                },
            ],
            Containers =
            [
                new V1Container
                {
                    Name = RunnerContainer,
                    Image = options.RunnerImage,
                    Command = ["/usr/bin/tini", "--", "sleep", "infinity"],
                    Env = [.. environment.Select(EnvVar)],
                    SecurityContext = new V1SecurityContext
                    {
                        AllowPrivilegeEscalation = false,
                        Capabilities = new V1Capabilities { Drop = ["ALL"] },
                    },
                    Resources = Requests(),
                    VolumeMounts =
                    [
                        .. ToolMounts([writable], readOnly: false),
                        .. shared is null ? [] : ToolMounts([shared], readOnly: true),
                    ],
                },
            ],
            Volumes = [new V1Volume { Name = ToolsVolume, PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = options.ToolsClaim } }],
        },
    };

    /// <summary>
    /// A short-lived pod of the runner image for a provider's sign-in terminal: the store's claim read-only when the CLI
    /// comes from it, and nothing of a run or chat. It carries no run label, so no run's NetworkPolicy selects it; its own SignInPolicy lets the
    /// OAuth flow reach the provider, and the sweep finds it by its role.
    /// </summary>
    public V1Pod SignIn(string name, Guid id, string home, IEnumerable<string> toolEnvironment, IReadOnlyList<RunToolMount> zones) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name,
            NamespaceProperty = namespaceName,
            Labels = new Dictionary<string, string>(RunNames.Labels(options.Instance, RunPaths.SignInRole)) { [KubernetesNames.SignInLabel] = id.ToString("N") },
        },
        Spec = new V1PodSpec
        {
            AutomountServiceAccountToken = false,
            EnableServiceLinks = false,
            RestartPolicy = "Never",
            TerminationGracePeriodSeconds = 0,
            SecurityContext = new V1PodSecurityContext
            {
                RunAsNonRoot = true,
                RunAsUser = RunnerUid,
                RunAsGroup = RunnerUid,
                FsGroup = RunnerUid,
                SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" },
            },
            Containers =
            [
                new V1Container
                {
                    Name = RunnerContainer,
                    Image = options.RunnerImage,
                    Command = ["/usr/bin/tini", "--", "sleep", "infinity"],
                    Env = [new V1EnvVar { Name = "HOME", Value = home }, .. toolEnvironment.Select(EnvVar)],
                    SecurityContext = new V1SecurityContext
                    {
                        AllowPrivilegeEscalation = false,
                        Capabilities = new V1Capabilities { Drop = ["ALL"] },
                    },
                    Resources = Requests(),
                    VolumeMounts = [.. ToolMounts(zones, readOnly: true)],
                },
            ],
            Volumes = [.. ToolVolumes(zones, readOnly: true)],
        },
    };

    private static V1EnvVar EnvVar(string assignment)
    {
        var split = assignment.IndexOf('=');
        return new V1EnvVar { Name = assignment[..split], Value = assignment[(split + 1)..] };
    }
}
