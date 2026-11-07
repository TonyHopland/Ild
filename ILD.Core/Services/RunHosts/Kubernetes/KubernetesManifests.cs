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
        Spec = new V1PersistentVolumeClaimSpec
        {
            AccessModes = ["ReadWriteOnce"],
            StorageClassName = options.StorageClass,
            Resources = new V1VolumeResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new(options.StorageSize) },
            },
        },
    };

    /// <summary>
    /// Egress only to ILD (the gateway, the MCP endpoint and the egress proxy) and to DNS; ingress only from ILD.
    /// What a run's pod can reach beyond that is reached through ILD's egress proxy.
    /// </summary>
    public V1NetworkPolicy Policy(Guid ownerId, V1OwnerReference owner)
    {
        var ild = new V1LabelSelector { MatchLabels = new Dictionary<string, string>(options.IldPodSelector) };
        var ports = new List<V1NetworkPolicyPort> { new() { Protocol = "TCP", Port = options.IldPort } };
        if (options.EgressProxyPort > 0)
            ports.Add(new V1NetworkPolicyPort { Protocol = "TCP", Port = options.EgressProxyPort });

        return new V1NetworkPolicy
        {
            Metadata = Meta(ownerId, owner),
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { [KubernetesNames.RunLabel] = ownerId.ToString("N"), [KubernetesNames.InstanceLabel] = options.Instance } },
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

    public V1Pod Pod(Guid ownerId, V1OwnerReference owner)
    {
        var root = KubernetesNames.RunRoot(ownerId);
        return new V1Pod
        {
            Metadata = Meta(ownerId, owner),
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
                        Env = [new V1EnvVar { Name = "HOME", Value = KubernetesNames.HomeDirectory(ownerId) }, .. options.GitIdentity.Select(e => new V1EnvVar { Name = e.Key, Value = e.Value })],
                        SecurityContext = new V1SecurityContext
                        {
                            AllowPrivilegeEscalation = false,
                            Capabilities = new V1Capabilities { Drop = ["ALL"] },
                        },
                        Resources = new V1ResourceRequirements
                        {
                            Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new(options.CpuRequest), ["memory"] = new(options.MemoryRequest) },
                            Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new(options.CpuLimit), ["memory"] = new(options.MemoryLimit) },
                        },
                        VolumeMounts =
                        [
                            new V1VolumeMount { Name = WorkVolume, MountPath = root },
                            new V1VolumeMount { Name = StateVolume, MountPath = KubernetesNames.StateDirectory },
                        ],
                    },
                ],
                Volumes =
                [
                    new V1Volume { Name = WorkVolume, PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = KubernetesNames.ObjectName(ownerId) } },
                    new V1Volume { Name = StateVolume, EmptyDir = new V1EmptyDirVolumeSource { Medium = "Memory", SizeLimit = new ResourceQuantity("64Mi") } },
                ],
            },
        };
    }
}
