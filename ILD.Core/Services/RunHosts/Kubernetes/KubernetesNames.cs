using ILD.Core.Services.RunHosts.Isolated;

namespace ILD.Core.Services.RunHosts.Kubernetes;

/// <summary>What a Kubernetes run's objects are called.</summary>
public static class KubernetesNames
{
    /// <summary>The name of a toolchain installer pod.</summary>
    public static string ToolsName(Guid id) => $"ild-tools-{id:N}";

    /// <summary>The label that names one installer, so its NetworkPolicy selects that pod alone.</summary>
    public const string InstallerLabel = "ild/installer";

    /// <summary>The annotation that says which pod spec the chat pod was made from.</summary>
    public const string SpecAnnotation = "ild/spec";

    /// <summary>The role of a provider sign-in pod, which the sweep removes once ILD no longer has it open.</summary>
    public const string SignInRole = "signin";

    /// <summary>The name of a provider sign-in pod.</summary>
    public static string SignInName(Guid id) => $"ild-signin-{id:N}";

    /// <summary>The name of a run's Secret, volume claim, network policy and pod.</summary>
    public static string ObjectName(Guid ownerId) => $"ild-run-{ownerId:N}";

    /// <summary>The name of the chat pod, its volume claim and its network policy: one of each for the instance.</summary>
    public static string ChatName(string instance) => $"ild-chat-{instance}";

    /// <summary>The name of the NetworkPolicy that gives a pod a direct route (network mode Off), after the policy <paramref name="objectName"/> of the same pod.</summary>
    public static string OpenName(string objectName) => $"{objectName}-open";

    public static string SelectorFor(string instance) => $"{RunPaths.ManagedByLabel}={RunPaths.ManagedByValue},{RunPaths.InstanceLabel}={instance}";

}
