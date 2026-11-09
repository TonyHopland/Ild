using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Tools;
using k8s.Models;

namespace ILD.Core.Services.RunHosts.Kubernetes;

/// <summary>
/// The objects of one run. The Secret is the owner: the volume claim, the network policy and the pod carry an
/// owner reference to it, so deleting it is what removes the rest.
/// </summary>
internal sealed class KubernetesManifests(KubernetesRunHostOptions options, string namespaceName)
{
    internal const uint RunnerUid = 10001;
    private const string WorkVolume = "work";
    private const string StateVolume = "state";
    private const string RunnerContainer = "runner";
    private const string ToolsVolume = "tools";

    private IDictionary<string, string> Labels(Guid ownerId) => new Dictionary<string, string>
    {
        [KubernetesNames.ManagedByLabel] = KubernetesNames.ManagedByValue,
        [KubernetesNames.InstanceLabel] = options.Instance,
        [KubernetesNames.RunLabel] = ownerId.ToString("N"),
    };

    private V1ObjectMeta Meta(Guid ownerId, V1OwnerReference? owner = null) => new()
    {
        Name = KubernetesNames.ObjectName(ownerId),
        NamespaceProperty = namespaceName,
        Labels = Labels(ownerId),
        OwnerReferences = owner is null ? null : [owner],
    };

    // No run label: the sweep, which deletes what a run left, never takes the chat objects for a run's. No owner reference
    // either: the chat pod is replaced when it ends, and the claim, with the chats' folders, outlives it.
    private V1ObjectMeta ChatMeta() => new()
    {
        Name = KubernetesNames.ChatName(options.Instance),
        NamespaceProperty = namespaceName,
        Labels = new Dictionary<string, string>
        {
            [KubernetesNames.ManagedByLabel] = KubernetesNames.ManagedByValue,
            [KubernetesNames.InstanceLabel] = options.Instance,
            [KubernetesNames.RoleLabel] = KubernetesNames.ChatRole,
        },
    };

    public V1PersistentVolumeClaim ChatClaim() => new() { Metadata = ChatMeta(), Spec = ClaimSpec() };

    public V1Secret Secret(Guid ownerId) => new()
    {
        Metadata = Meta(ownerId),
        Type = "Opaque",
        StringData = new Dictionary<string, string> { ["run"] = ownerId.ToString("N") },
    };

    public static V1OwnerReference OwnerReference(V1Secret secret) => new()
    {
        ApiVersion = "v1",
        Kind = "Secret",
        Name = secret.Metadata.Name,
        Uid = secret.Metadata.Uid,
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
    /// Egress only to ILD (the gateway, the MCP endpoint and the egress proxy) and to DNS; ingress only from ILD.
    /// What a run's pod can reach beyond that is reached through ILD's egress proxy.
    /// </summary>
    public V1NetworkPolicy Policy(Guid ownerId, V1OwnerReference owner)
        => Policy(Meta(ownerId, owner), new Dictionary<string, string> { [KubernetesNames.RunLabel] = ownerId.ToString("N"), [KubernetesNames.InstanceLabel] = options.Instance });

    /// <summary>The same rules for the chat pod, which no run's policy selects.</summary>
    public V1NetworkPolicy ChatPolicy()
        => Policy(ChatMeta(), new Dictionary<string, string> { [KubernetesNames.RoleLabel] = KubernetesNames.ChatRole, [KubernetesNames.InstanceLabel] = options.Instance });

    private V1NetworkPolicy Policy(V1ObjectMeta metadata, IDictionary<string, string> podLabels)
    {
        var ild = new V1LabelSelector { MatchLabels = new Dictionary<string, string>(options.IldPodSelector) };
        var ports = new List<V1NetworkPolicyPort> { new() { Protocol = "TCP", Port = options.IldPort } };
        if (options.EgressProxyPort > 0)
            ports.Add(new V1NetworkPolicyPort { Protocol = "TCP", Port = options.EgressProxyPort });

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
                        To = [new V1NetworkPolicyPeer { PodSelector = ild, NamespaceSelector = new V1LabelSelector() }],
                        Ports = ports,
                    },
                    new V1NetworkPolicyEgressRule
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
                    },
                ],
                Ingress =
                [
                    new V1NetworkPolicyIngressRule
                    {
                        FromProperty = [new V1NetworkPolicyPeer { PodSelector = ild, NamespaceSelector = new V1LabelSelector() }],
                    },
                ],
            },
        };
    }

    public V1Pod Pod(Guid ownerId, V1OwnerReference owner, RunToolEnvironment? tools = null)
        => BuildPod(Meta(ownerId, owner), KubernetesNames.RunRoot(ownerId), KubernetesNames.HomeDirectory(ownerId), KubernetesNames.ObjectName(ownerId), tools?.For(KubernetesNames.HomeDirectory(ownerId)), tools?.Zones ?? [], limits: true);

    // The zones every chat mounts: the agents' and the shared toolchains, never a repository's own tools.
    private static readonly RunToolMount[] ChatToolZones = [new(RunToolZone.Agents), new(RunToolZone.Toolchains)];

    /// <summary>
    /// The pod that keeps every chat, long-lived: the claim of the chats at their root, and the agents' and toolchain
    /// zones of the store's claim read-only whenever there is a store, since a pod's volumes are fixed when it is made and
    /// a chat may need toolchains later. No CPU or memory limit, as chats had in ILD's own container.
    /// </summary>
    public V1Pod ChatPod()
    {
        var pod = BuildPod(ChatMeta(), RunPaths.ChatsRoot, null, KubernetesNames.ChatName(options.Instance), [], options.UsesToolStore ? ChatToolZones : [], limits: false);
        pod.Metadata.Annotations = new Dictionary<string, string> { [KubernetesNames.SpecAnnotation] = SpecOf(pod) };
        return pod;
    }

    /// <summary>
    /// A hash of a pod's spec: the runner image and everything ILD gives the pod. The chat pod is long-lived, so one whose
    /// annotation differs from what ILD would make now (a new runner image, other settings) is replaced.
    /// </summary>
    public static string SpecOf(V1Pod pod)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(k8s.KubernetesJson.Serialize(pod.Spec)));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private V1Pod BuildPod(V1ObjectMeta metadata, string root, string? home, string claimName, IEnumerable<string>? toolEnvironment, IReadOnlyList<RunToolMount> zones, bool limits)
    {
        return new V1Pod
        {
            Metadata = metadata,
            Spec = new V1PodSpec
            {
                AutomountServiceAccountToken = false,
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
                                [KubernetesNames.ManagedByLabel] = KubernetesNames.ManagedByValue,
                                [KubernetesNames.InstanceLabel] = options.Instance,
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
                            new V1VolumeMount { Name = StateVolume, MountPath = KubernetesNames.StateDirectory },
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
    /// would make it as root. The pod carries no run label: no run's NetworkPolicy selects it, so it can download, and
    /// the sweep never takes it for a run's.
    /// </summary>
    public V1Pod ToolsInstaller(string name, RunToolMount writable, RunToolMount? shared, IEnumerable<string> environment) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name,
            NamespaceProperty = namespaceName,
            Labels = new Dictionary<string, string>
            {
                [KubernetesNames.ManagedByLabel] = KubernetesNames.ManagedByValue,
                [KubernetesNames.InstanceLabel] = options.Instance,
                [KubernetesNames.RoleLabel] = KubernetesNames.ToolsRole,
            },
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
    /// comes from it, and nothing of a run or chat. It carries no run label, so no run's NetworkPolicy selects it and the
    /// OAuth flow can reach the provider, and the sweep finds it by its role.
    /// </summary>
    public V1Pod SignIn(string name, string home, IEnumerable<string> toolEnvironment, IReadOnlyList<RunToolMount> zones) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name,
            NamespaceProperty = namespaceName,
            Labels = new Dictionary<string, string>
            {
                [KubernetesNames.ManagedByLabel] = KubernetesNames.ManagedByValue,
                [KubernetesNames.InstanceLabel] = options.Instance,
                [KubernetesNames.RoleLabel] = KubernetesNames.SignInRole,
            },
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
