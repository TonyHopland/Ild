using ILD.Core.Services.RunHosts.Isolated;
using System.Diagnostics;
using ILD.Data.Enums;

namespace ILD.Core.Services.RunHosts;

/// <summary>
/// Where a run's processes and files live. Everything the orchestrator does that
/// depends on that — starting the agent, running a Cmd node, opening a terminal, moving
/// code in and out, reading the transcripts a CLI wrote, finding a preview's port — goes
/// through this host, so a backend that puts the run somewhere else (a container,
/// a Kubernetes pod) plugs in beside <c>local</c>, today's uid split, without the
/// callers changing. Paths in it are the host's own: the orchestrator never assumes it
/// can open one itself. It is one host with one backend; what a caller depends on is the
/// role it uses (<see cref="IRunCommands"/>, <see cref="IRunFiles"/> and the others in RunHostRoles.cs), which this puts together.
/// </summary>
public interface IRunHost : IRunWorkspace, IRunReclaim, IRunChatFolders, IRunChats, IRunRepositories, IRunShells, IRunLeftovers;

/// <summary>Which backend holds the runs (<see cref="RunHostOptions.Detect"/>).</summary>
public enum RunHostKind
{
    Local,
    Container,
    Kubernetes,
}

/// <summary>
/// The handle of a run's environment: whose it is and its working directory, in the host's own paths. Deleting it with
/// <see cref="KeptPreviewDataOf"/> puts the run's copy of the preview data back in that repository's store first, when the run is still running.
/// </summary>
public sealed record RunEnvironment(Guid OwnerId, string WorkingDirectory, Guid? KeptPreviewDataOf = null);

/// <summary>What <see cref="IRunLifecycle.CreateAsync"/> makes an environment from: the base checkout and the run's branch. <see cref="KeepPreviewData"/> gives the run a copy of the preview data its repository keeps between runs.</summary>
public sealed record RunEnvironmentRequest(Guid OwnerId, string BasePath, string BranchName, Guid RepositoryId, bool Dns = false, bool KeepPreviewData = false);

/// <summary>
/// A command to start. <see cref="Start"/> carries the program, arguments, working directory and which streams are
/// redirected; its <see cref="ProcessStartInfo.Environment"/> is ignored. The command's whole environment is
/// <see cref="Environment"/>, the variables the caller means to set, merged by the host (<see cref="CommandEnvironment"/>).
/// An agent CLI launch (<see cref="Agent"/>) is also given the provider's egress rules under <see cref="NetworkMode"/>.
/// <see cref="OwnerId"/> is the run or chat it belongs to, for <see cref="IRunCommands.KillAllAsync"/>.
/// </summary>
public sealed record RunCommand
{
    private RunCommand(ProcessStartInfo start, Guid ownerId, bool asAgent, Guid? providerId, NetworkMode networkMode, IReadOnlyDictionary<string, string?>? environment, IReadOnlyList<string>? files = null)
    {
        Start = start;
        OwnerId = ownerId;
        AsAgent = asAgent;
        ProviderId = providerId;
        NetworkMode = networkMode;
        Environment = environment;
        Files = files ?? [];
    }

    public ProcessStartInfo Start { get; }
    public Guid OwnerId { get; }
    public bool AsAgent { get; }
    public Guid? ProviderId { get; }
    public NetworkMode NetworkMode { get; }

    /// <summary>The variables the caller sets, and with a null value unsets; nothing else of the caller's reaches the command.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; }

    /// <summary>The proxy URL the local host points a prepared command at, as its host-owned proxy variables; an isolated host chooses its own. Null for none.</summary>
    public string? EgressProxy { get; init; }

    /// <summary>
    /// The run or chat the command must run in, for a command the agent can shape (a Cmd node, a preview step). An isolated host refuses it
    /// unless it holds that run and the working directory is in it; it is never started on ILD's own machine instead. Unlike
    /// <see cref="OwnerId"/> it does not make <see cref="IRunCommands.KillAllAsync"/> end the command, so a preview outlives a retry.
    /// </summary>
    public Guid? RunIn { get; init; }

    /// <summary>
    /// The files in the agent read root that the caller wrote for this command and its arguments or environment point at.
    /// A host that cannot share ILD's disk with the command copies exactly these in; text is never searched for paths.
    /// </summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>An agent CLI launch: the host gives it the provider's egress rules and environment.</summary>
    public static RunCommand Agent(
        ProcessStartInfo start, Guid ownerId, Guid? providerId, NetworkMode networkMode, IReadOnlyDictionary<string, string?>? environment, IReadOnlyList<string>? files = null)
        => new(start, ownerId, asAgent: true, providerId, networkMode, environment, files);

    /// <summary>
    /// A command that is not an agent CLI (a Cmd node, a preview, a git command in a chat clone). <paramref name="networkMode"/> is the mode it
    /// is launched in: a host that runs it in an environment of its own gives it a proxy in every mode but Off, which gets a direct route (a command
    /// ILD starts for itself is proxied, which Monitor says). <paramref name="environment"/> is what the caller sets.
    /// </summary>
    public static RunCommand Prepared(
        ProcessStartInfo prepared, Guid ownerId = default, IReadOnlyDictionary<string, string?>? environment = null, IReadOnlyList<string>? files = null, NetworkMode networkMode = NetworkMode.Monitor)
        => new(prepared, ownerId, asAgent: false, providerId: null, networkMode, environment, files);
}

/// <summary>The outcome of a command that moved code.</summary>
public sealed record RunHostResult(int ExitCode, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>What a short command printed and how it ended.</summary>
public sealed record RunHostOutput(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Streams a short command reads its stdin from and writes its stdout to instead of the collected text.</summary>
public sealed record RunToolStreams(Stream? StandardInputFrom = null, Stream? StandardOutputTo = null);

/// <summary>Something a run left in a host, named so it can be shown and deleted.</summary>
public sealed record RunLeftover(string Kind, string Location)
{
    /// <summary>What the leftover is, when the host that found it knew; without one, the host reads it from <see cref="Location"/>.</summary>
    public Isolated.LeftoverTarget? Target { get; init; }
}

/// <summary>A command running in a host.</summary>
public interface IRunProcess : IDisposable
{
    int Id { get; }
    StreamReader StandardOutput { get; }
    StreamReader StandardError { get; }
    StreamWriter StandardInput { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken ct = default);

    /// <summary>
    /// Kill the command and everything it started. Never throws; unlike a blind kill it says whether
    /// it worked, because an agent that could not be killed goes on writing the worktree.
    /// True when the command is gone, killed or already ended.
    /// </summary>
    bool TryKill();

    /// <summary>As <see cref="TryKill"/>, but the reason when it fails.</summary>
    bool TryKill(out string? failure);

    /// <summary>Why the run's container ended when that is what ended the command, read when it did; null when the command ended on its own or the host cannot say.</summary>
    RunTermination? Termination => null;
}
