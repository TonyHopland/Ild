namespace ILD.Core.Services.RunHosts.Kubernetes;

/// <summary>What a run's objects are called and labelled, and where its files sit in the pod.</summary>
public static class KubernetesNames
{
    public const string ManagedByLabel = "app.kubernetes.io/managed-by";
    public const string ManagedByValue = "ild";
    public const string InstanceLabel = "ild/instance";
    public const string RunLabel = "ild/run";

    /// <summary>Every run's files sit under this directory of its pod, in a directory of its own.</summary>
    public const string RunsRoot = "/ild-run";

    public const string StateDirectory = "/run/ild";
    public const string ProcessDirectory = StateDirectory + "/procs";
    public const string AgentReadDirectory = StateDirectory + "/agent-read";

    /// <summary>The name of a run's Secret, volume claim, network policy and pod.</summary>
    public static string ObjectName(Guid ownerId) => $"ild-run-{ownerId:N}";

    public static string RunRoot(Guid ownerId) => $"{RunsRoot}/{ownerId:N}";

    public static string WorkingDirectory(Guid ownerId) => $"{RunRoot(ownerId)}/repo";

    public static string HomeDirectory(Guid ownerId) => $"{RunRoot(ownerId)}/home";

    public static string SelectorFor(string instance) => $"{ManagedByLabel}={ManagedByValue},{InstanceLabel}={instance}";

    /// <summary>The run that holds <paramref name="path"/>, or null when it is not a path in a pod.</summary>
    public static Guid? OwnerOf(string? path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith(RunsRoot + "/", StringComparison.Ordinal)) return null;
        var rest = path[(RunsRoot.Length + 1)..];
        var slash = rest.IndexOf('/');
        var name = slash < 0 ? rest : rest[..slash];
        return Guid.TryParseExact(name, "N", out var id) ? id : null;
    }
}
