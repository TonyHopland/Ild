using ILD.Core.Services.RunHosts.Isolated;

namespace ILD.Core.Services.RunHosts.Kubernetes;

/// <summary>The labels only a Kubernetes run's objects have; their names and shared labels are <see cref="RunNames"/>.</summary>
public static class KubernetesNames
{
    /// <summary>The label that names one installer, so its NetworkPolicy selects that pod alone.</summary>
    public const string InstallerLabel = "ild/installer";

    /// <summary>The label that names one sign-in pod, so its NetworkPolicy selects that pod alone.</summary>
    public const string SignInLabel = "ild/signin";
}
