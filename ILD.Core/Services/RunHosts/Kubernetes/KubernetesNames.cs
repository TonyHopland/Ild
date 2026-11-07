using ILD.Core.Services.RunHosts.Isolated;

namespace ILD.Core.Services.RunHosts.Kubernetes;

/// <summary>What a run's objects are called and labelled, and where its files sit in the pod.</summary>
public static class KubernetesNames
{
    public const string ManagedByLabel = RunPaths.ManagedByLabel;
    public const string ManagedByValue = RunPaths.ManagedByValue;
    public const string InstanceLabel = RunPaths.InstanceLabel;
    public const string RunLabel = RunPaths.RunLabel;
    public const string RunsRoot = RunPaths.RunsRoot;
    public const string StateDirectory = RunPaths.StateDirectory;

    /// <summary>The name of a run's Secret, volume claim, network policy and pod.</summary>
    public static string ObjectName(Guid ownerId) => $"ild-run-{ownerId:N}";

    public static string RunRoot(Guid ownerId) => RunPaths.RunRoot(ownerId);

    public static string WorkingDirectory(Guid ownerId) => RunPaths.WorkingDirectory(ownerId);

    public static string HomeDirectory(Guid ownerId) => RunPaths.HomeDirectory(ownerId);

    public static string SelectorFor(string instance) => $"{ManagedByLabel}={ManagedByValue},{InstanceLabel}={instance}";

    public static Guid? OwnerOf(string? path) => RunPaths.OwnerOf(path);
}
