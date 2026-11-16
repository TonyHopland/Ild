namespace ILD.Core.Services.RunHosts.Isolated;

/// <summary>Which environment holds a call: a run's pod or container, the shared chat container's folder of a chat, or neither, which is ILD's own disk.</summary>
public enum RunHolderKind
{
    OwnDisk,
    Run,
    Chat,
}

/// <summary>The environment a call belongs to, resolved once by <see cref="RunHolders"/>; the hosts consume it and never re-derive it from strings.</summary>
public readonly record struct RunHolder(RunHolderKind Kind, Guid Id)
{
    /// <summary>ILD's own disk: a run or chat from before containers, a scheduled job. It may be read and deleted, never executed in.</summary>
    public static RunHolder OwnDisk => default;

    public static RunHolder Run(Guid id) => new(RunHolderKind.Run, id);

    public static RunHolder Chat(Guid id) => new(RunHolderKind.Chat, id);

    public static implicit operator RunHolder(ExecOwner owner) => owner.Chat ? Chat(owner.Id) : Run(owner.Id);

    public bool IsOwnDisk => Kind == RunHolderKind.OwnDisk;

    /// <summary>The environment to exec in. Throws for <see cref="OwnDisk"/>, which has none.</summary>
    public ExecOwner ToExecOwner() => Kind switch
    {
        RunHolderKind.Run => new ExecOwner(Id, Chat: false),
        RunHolderKind.Chat => new ExecOwner(Id, Chat: true),
        _ => throw new InvalidOperationException("ILD's own disk has no environment to run in"),
    };
}

/// <summary>
/// Resolves which run or chat a path or a command belongs to. Paths stay the format a call names its environment by
/// (<c>/ild-run/&lt;run id&gt;</c>, <c>/ild-chat/&lt;chat id&gt;</c>, ADR-0032 and ADR-0034); this is the one place that reads them, and the one place the
/// precedence for a command is written:
/// <list type="number">
/// <item><see cref="RunCommand.RunIn"/>: the run or chat it must run in. A routing host holds the command there or refuses it (never ILD's own disk); an isolated host alone falls through when the engine holds nothing of it.</item>
/// <item>No working directory and an <see cref="RunCommand.OwnerId"/>: the id alone says whose it is, a run's or a chat's, found by asking the engine.</item>
/// <item>The directory: a chat's folder (when the host keeps chats) is the chat's, whatever id its caller names to kill it by; then <see cref="RunCommand.OwnerId"/> as a run; then the run the directory is in.</item>
/// </list>
/// Other invariants of <see cref="RunCommand"/>: <see cref="RunCommand.OwnerId"/> makes <see cref="IRunCommands.KillAllAsync"/> end the command and
/// <see cref="RunCommand.RunIn"/> does not, so a preview outlives a retry; and <see cref="RunCommand.Files"/> are staged into the run only when
/// <see cref="IsStageable"/> says so.
/// </summary>
public static class RunHolders
{
    /// <summary>The holder <paramref name="path"/> names, whatever this host keeps: a run's, else a chat's, else <see cref="RunHolder.OwnDisk"/>.</summary>
    public static RunHolder Named(string? path)
        => RunPaths.OwnerOf(path) is { } run ? RunHolder.Run(run)
            : RunPaths.ChatOf(path) is { } chat ? RunHolder.Chat(chat)
            : RunHolder.OwnDisk;

    /// <summary>The holder <paramref name="path"/> is in on a host that keeps chats only when <paramref name="hostsChats"/>: a chat's folder on a host that does not is on ILD's own disk.</summary>
    public static RunHolder FromPath(string? path, bool hostsChats)
        => Named(path) is { Kind: RunHolderKind.Chat } && !hostsChats ? RunHolder.OwnDisk : Named(path);

    /// <summary>
    /// Whether a file a launch wrote may be staged into the run <paramref name="ownerId"/>: it sits in <paramref name="agentReadRoot"/>
    /// and has the owner's id in its name, so a file another run wrote or one the agent shaped is never copied in.
    /// </summary>
    public static bool IsStageable(string agentReadRoot, string localPath, Guid ownerId)
    {
        var relative = Path.GetRelativePath(agentReadRoot, localPath);
        var inside = relative != "." && !Path.IsPathRooted(relative)
            && !relative.Split(Path.DirectorySeparatorChar).Contains("..");
        return inside && relative.Contains(ownerId.ToString("N"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The holder of <paramref name="command"/>. With <paramref name="routed"/> a command that no run or chat holds resolves to <see cref="RunHolder.OwnDisk"/>
    /// (or is refused), because it must never start on ILD's own machine; without it (an isolated host called directly) it falls back to the
    /// <see cref="RunCommand.OwnerId"/> as a run, and throws when there is none. <paramref name="heldBy"/> asks the engine, once.
    /// </summary>
    public static async Task<RunHolder> ForCommandAsync(
        RunCommand command, bool hostsChats, bool routed, string hostName,
        Func<Guid, CancellationToken, Task<ExecOwner?>> heldBy, CancellationToken ct)
    {
        var directory = command.Start.WorkingDirectory;
        if (command.RunIn is { } runIn)
        {
            if (routed && command.AsAgent && !string.IsNullOrEmpty(directory) && Named(Path.GetFullPath(directory)).IsOwnDisk)
                throw new PredatesContainersException();
            var held = await heldBy(runIn, ct);
            if (routed)
            {
                if (held is not { } found)
                    throw new InvalidOperationException($"refusing to run {command.Start.FileName} in {directory}: the {hostName} run host holds no run or chat {runIn:N}, and a command of a run never runs on ILD's own machine");
                var root = found.Chat ? RunPaths.ChatRoot(runIn) : RunPaths.RunRoot(runIn);
                if (!string.IsNullOrEmpty(directory) && !Within(Path.GetFullPath(directory), root))
                    throw new InvalidOperationException($"refusing to run {command.Start.FileName} in {directory}, which is outside run {runIn:N}");
                return found;
            }
            if (held is { } owner) return owner;
        }
        else if (string.IsNullOrEmpty(directory) && command.OwnerId != Guid.Empty)
        {
            if (await heldBy(command.OwnerId, ct) is { } owner) return owner;
            if (routed) return RunHolder.OwnDisk;
        }

        var named = FromPath(directory, hostsChats);
        if (routed && named.IsOwnDisk) return RunHolder.OwnDisk;
        if (named.Kind == RunHolderKind.Chat) return named;
        if (command.OwnerId != Guid.Empty) return RunHolder.Run(command.OwnerId);
        return named;
    }

    private static bool Within(string path, string root) => path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
}
