namespace ILD.Core.Services.RunHosts.Isolated;

/// <summary>Where a run's files sit in its pod or container, and the labels every object of a run carries.</summary>
public static class RunPaths
{
    public const string ManagedByLabel = "app.kubernetes.io/managed-by";
    public const string ManagedByValue = "ild";
    public const string InstanceLabel = "ild/instance";
    public const string RunLabel = "ild/run";

    /// <summary>Every run's files sit under this directory of its pod or container, in a directory of its own.</summary>
    public const string RunsRoot = "/ild-run";

    public const string StateDirectory = "/run/ild";
    public const string ProcessDirectory = StateDirectory + "/procs";
    public const string AgentReadDirectory = StateDirectory + "/agent-read";

    /// <summary>Every chat's files sit under this directory of the shared chat container, in a directory of its own.</summary>
    public const string ChatsRoot = "/ild-chat";
    public const string ChatRole = "chat";
    public const string RoleLabel = "ild/role";

    /// <summary>The role of a toolchain installer's pod or container, which the sweep removes once ILD no longer has it open.</summary>
    public const string ToolsRole = "tools";

    /// <summary>The role of the network (or NetworkPolicy) that gives a run or chat a direct route in network mode Off.</summary>
    public const string OpenRole = "open";

    public static string RunRoot(Guid ownerId) => $"{RunsRoot}/{ownerId:N}";

    public static string WorkingDirectory(Guid ownerId) => $"{RunRoot(ownerId)}/repo";

    public static string HomeDirectory(Guid ownerId) => $"{RunRoot(ownerId)}/home";

    public static string ChatRoot(Guid chatId) => $"{ChatsRoot}/{chatId:N}";

    public static string ChatHome(Guid chatId) => $"{ChatRoot(chatId)}/home";

    /// <summary>The chat that holds <paramref name="path"/>, or null when it is not a path in the chat container.</summary>
    public static Guid? ChatOf(string? path) => OwnerIn(path, ChatsRoot);

    /// <summary>The run that holds <paramref name="path"/>, or null when it is not a path in a run's pod or container.</summary>
    public static Guid? OwnerOf(string? path) => OwnerIn(path, RunsRoot);

    private static Guid? OwnerIn(string? path, string root)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith(root + "/", StringComparison.Ordinal)) return null;
        var rest = path[(root.Length + 1)..];
        var slash = rest.IndexOf('/');
        var name = slash < 0 ? rest : rest[..slash];
        return Guid.TryParseExact(name, "N", out var id) ? id : null;
    }
}
