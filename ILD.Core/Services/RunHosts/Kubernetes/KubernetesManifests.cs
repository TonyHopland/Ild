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
        => BuildPod(Meta(ownerId, owner), KubernetesNames.RunRoot(ownerId), KubernetesNames.HomeDirectory(ownerId), KubernetesNames.ObjectName(ownerId), tools?.For(KubernetesNames.HomeDirectory(ownerId)), tools is { Path.Count: > 0 }, limits: true);

    /// <summary>
    /// The pod that keeps every chat, long-lived: the claim of the chats at their root, and the store's claim read-only
    /// whenever there is a store, since a pod's volumes are fixed when it is made and a chat may need toolchains later.
    /// No CPU or memory limit, as chats had in ILD's own container.
    /// </summary>
    public V1Pod ChatPod()
        => BuildPod(ChatMeta(), RunPaths.ChatsRoot, null, KubernetesNames.ChatName(options.Instance), [], options.UsesToolStore, limits: false);

    private V1Pod BuildPod(V1ObjectMeta metadata, string root, string? home, string claimName, IEnumerable<string>? toolEnvironment, bool mountTools, bool limits)
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
                            Limits = limits ? new Dictionary<string, ResourceQuantity> { ["cpu"] = new(options.CpuLimit), ["memory"] = new(options.MemoryLimit) } : null,
                        },
                        VolumeMounts =
                        [
                            new V1VolumeMount { Name = WorkVolume, MountPath = root },
                            new V1VolumeMount { Name = StateVolume, MountPath = KubernetesNames.StateDirectory },
                            .. (mountTools && options.ToolsClaim is not null
                                ? [new V1VolumeMount { Name = ToolsVolume, MountPath = RunToolStore.Root, ReadOnlyProperty = true }]
                                : Array.Empty<V1VolumeMount>()),
                        ],
                    },
                ],
                Volumes =
                [
                    new V1Volume { Name = WorkVolume, PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = claimName } },
                    new V1Volume { Name = StateVolume, EmptyDir = new V1EmptyDirVolumeSource { Medium = "Memory", SizeLimit = new ResourceQuantity("64Mi") } },
                    .. (mountTools && options.ToolsClaim is { } claim
                        ? [new V1Volume { Name = ToolsVolume, PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = claim, ReadOnlyProperty = true } }]
                        : Array.Empty<V1Volume>()),
                ],
            },
        };
    }

    /// <summary>
    /// A short-lived pod of the runner image with the store's claim mounted writable, which ILD runs the store's install
    /// script in. It carries no run label: no run's NetworkPolicy selects it, so it can download, and the sweep never
    /// takes it for a run's.
    /// </summary>
    public V1Pod ToolsInstaller(string name) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name,
            NamespaceProperty = namespaceName,
            Labels = new Dictionary<string, string>
            {
                [KubernetesNames.ManagedByLabel] = KubernetesNames.ManagedByValue,
                [KubernetesNames.InstanceLabel] = options.Instance,
                ["ild/role"] = "tools",
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
                    Env = [.. RunToolStore.InstallerEnvironment.Select(EnvVar)],
                    SecurityContext = new V1SecurityContext
                    {
                        AllowPrivilegeEscalation = false,
                        Capabilities = new V1Capabilities { Drop = ["ALL"] },
                    },
                    VolumeMounts = [new V1VolumeMount { Name = ToolsVolume, MountPath = RunToolStore.Root }],
                },
            ],
            Volumes = [new V1Volume { Name = ToolsVolume, PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = options.ToolsClaim } }],
        },
    };

    private static V1EnvVar EnvVar(string assignment)
    {
        var split = assignment.IndexOf('=');
        return new V1EnvVar { Name = assignment[..split], Value = assignment[(split + 1)..] };
    }
}
