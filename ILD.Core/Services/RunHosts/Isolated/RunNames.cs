namespace ILD.Core.Services.RunHosts.Isolated;

/// <summary>
/// What the objects ILD makes in a container engine or a cluster are called, and the labels they carry: one place for both
/// engines, so the sweep finds what either made. A name or label here is on objects that outlive an ILD upgrade; it never changes.
/// </summary>
public static class RunNames
{
    public const string RunPrefix = "ild-run-";
    public const string ChatPrefix = "ild-chat-";
    public const string InstallerPrefix = "ild-tools-";
    public const string SignInPrefix = "ild-signin-";

    /// <summary>The name of a run's container, volume and network, or its ConfigMap, volume claim, network policy and pod.</summary>
    public static string Run(Guid ownerId) => $"{RunPrefix}{ownerId:N}";

    /// <summary>The run <paramref name="name"/> is the <see cref="Run"/> name of, or false when it is not one.</summary>
    public static bool TryParseRun(string? name, out Guid ownerId)
    {
        ownerId = Guid.Empty;
        return name is not null && name.StartsWith(RunPrefix, StringComparison.Ordinal) && Guid.TryParseExact(name[RunPrefix.Length..], "N", out ownerId);
    }

    /// <summary>The name of the chat container or pod, its volume or claim and its network or policy: one of each for the instance.</summary>
    public static string Chat(string instance) => $"{ChatPrefix}{instance}";

    /// <summary>What a toolchain installer's pod or container is called.</summary>
    public static string Installer(Guid id) => $"{InstallerPrefix}{id:N}";

    /// <summary>What a provider sign-in's pod or container is called.</summary>
    public static string SignIn(Guid id) => $"{SignInPrefix}{id:N}";

    /// <summary>The name of the NetworkPolicy that gives a pod a direct route (network mode Off), after the policy <paramref name="objectName"/> of the same pod.</summary>
    public static string OpenPolicy(string objectName) => $"{objectName}-open";

    /// <summary>The one network every run and the chats shared before each had its own; what an older ILD left attached is still taken away.</summary>
    public static string SharedOpenNetwork(string instance) => $"ild-open-{instance}";

    /// <summary>The network that gives one owner (a run's id, or "chat") its direct route.</summary>
    public static string OpenNetwork(string instance, string ownerKey) => $"{SharedOpenNetwork(instance)}-{ownerKey}";

    /// <summary>The <c>key=value</c> pairs that select the objects of an instance, and of one role of it: the labels of <see cref="Labels"/>.</summary>
    public static string[] SelectorPairs(string instance, string? role = null)
    {
        var pairs = new List<string> { $"{RunPaths.ManagedByLabel}={RunPaths.ManagedByValue}", $"{RunPaths.InstanceLabel}={instance}" };
        if (role is not null) pairs.Add($"{RunPaths.RoleLabel}={role}");
        return [.. pairs];
    }

    /// <summary>The same as a Kubernetes label selector.</summary>
    public static string Selector(string instance, string? role = null) => string.Join(',', SelectorPairs(instance, role));

    /// <summary>
    /// The labels of an object ILD made: whose it is, and what it is for. A run's objects carry its id; the chat's, the sign-ins' and
    /// installers' carry none, so the sweep, which deletes what a run left, never takes them for a run's. The toolchain volumes carry no instance.
    /// </summary>
    public static Dictionary<string, string> Labels(string? instance, string? role = null, Guid? runId = null)
    {
        var labels = new Dictionary<string, string> { [RunPaths.ManagedByLabel] = RunPaths.ManagedByValue };
        if (instance is not null) labels[RunPaths.InstanceLabel] = instance;
        if (role is not null) labels[RunPaths.RoleLabel] = role;
        if (runId is { } run) labels[RunPaths.RunLabel] = run.ToString("N");
        return labels;
    }
}
