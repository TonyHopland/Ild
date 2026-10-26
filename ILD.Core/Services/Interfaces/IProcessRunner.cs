namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Spawns external processes. Centralises argument escaping, timeout handling,
/// and process-tree teardown so executors and repository helpers don't each
/// re-implement <see cref="System.Diagnostics.Process"/> plumbing.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Run <paramref name="fileName"/> with <paramref name="args"/> and capture
    /// stdout / stderr. If the operation is cancelled (e.g. via timeout) the
    /// process tree is killed and <see cref="OperationCanceledException"/> is
    /// thrown to the caller.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory = null,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        ProcessRunOptions? options = null);
}

public readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>The unprivileged user a child crosses to (see <c>AgentIsolation.Route</c>).</summary>
public sealed record AgentIdentity(string User, string Group, string Home);

/// <summary>
/// Whose child a process is. With no options it is the orchestrator's own and
/// inherits its environment minus the secrets an agent must never see.
/// </summary>
/// <param name="AsAgent">Run as this user, through the same crossing the agent CLIs take.</param>
/// <param name="CleanEnvironment">Start from an empty environment: the child gets only what the caller passes.</param>
/// <param name="InheritOrchestratorSecrets">Keep the orchestrator's secrets in the inherited environment. No production caller sets it.</param>
/// <param name="AgentScoped">The child works on agent-written input: it inherits neither the orchestrator's secrets, topology nor git credentials, whether or not the uid crosses.</param>
/// <param name="StandardOutputTo">Receives stdout verbatim instead of <see cref="ProcessResult.StdOut"/>, for output that is not text.</param>
public sealed record ProcessRunOptions(
    AgentIdentity? AsAgent = null,
    bool CleanEnvironment = false,
    bool InheritOrchestratorSecrets = false,
    bool AgentScoped = false,
    Stream? StandardOutputTo = null);
