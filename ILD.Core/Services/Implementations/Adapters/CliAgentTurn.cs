using System.Diagnostics;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>What one launch of a CLI is started with.</summary>
/// <param name="Environment">What the adapter sets for the launch; a null value unsets.</param>
/// <param name="AgentFiles">The files in the agent read root that the adapter wrote for the launch and that <paramref name="StartInfo"/> points at.</param>
public sealed record AgentLaunch(
    ProcessStartInfo StartInfo,
    IReadOnlyDictionary<string, string?>? Environment = null,
    IEnumerable<string?>? AgentFiles = null);

/// <summary>
/// What is particular to one CLI in one agent turn: how it is launched, how its output reads and what result that makes.
/// <see cref="CliAgentAdapterBase.ExecuteAsync"/> drives it, and owns everything every CLI shares: the worktree check, the model
/// gateway, starting the process, the timeouts and the kill, and the processes the agent left running.
/// </summary>
public abstract class CliAgentTurn : IAsyncDisposable
{
    /// <summary>The CLI being started, named when it cannot be.</summary>
    public abstract string BinaryPath { get; }

    /// <summary>What has to be in place before the launch: config files, a forked or restored session.</summary>
    public virtual Task PrepareAsync() => Task.CompletedTask;

    /// <summary>The launch. A failure to build it is reported as a failure to start the CLI.</summary>
    public abstract Task<AgentLaunch> LaunchAsync();

    /// <summary>Another launch to try after <paramref name="failure"/> stopped the first from starting, or null to give up.</summary>
    public virtual Task<AgentLaunch?> RetryLaunchAsync(Exception failure) => Task.FromResult<AgentLaunch?>(null);

    /// <summary>Writes what the CLI reads from standard input, and closes it where the CLI waits for that.</summary>
    public virtual Task SendInputAsync(IRunProcess process) => Task.CompletedTask;

    /// <summary>Reads standard output to its end, keeping what <see cref="CompleteAsync"/> needs of it.</summary>
    public abstract Task ReadStdoutAsync(StreamReader stdout);

    public virtual Task<string> ReadStderrAsync(StreamReader stderr, CancellationToken ct) => stderr.ReadToEndAsync(ct);

    /// <summary>Called once the CLI was killed for outliving the turn, with the still running read of its standard output.</summary>
    public virtual Task AfterTimeoutAsync(Task stdout) => Task.CompletedTask;

    /// <summary>The turn's result, from what was read and how the CLI exited.</summary>
    public abstract Task<NodeExecutionResult> CompleteAsync(string stderr, int exitCode);

    /// <summary>
    /// For the stream reader to await when the CLI says its turn is over, while the CLI is still there to be told apart from what it
    /// started. Null when nothing is listening.
    /// </summary>
    public Func<Task>? TurnEnded { get; internal set; }

    /// <summary>Removes what the turn wrote for the launch. Runs however the turn ended, and must not throw.</summary>
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Ends the turn with <paramref name="error"/> from anywhere in it.</summary>
    protected static AgentTurnFailedException Failed(string error) => new(NodeExecutionResult.Fail(error));
}

/// <summary>Thrown by a <see cref="CliAgentTurn"/> to end the turn with <see cref="Result"/> as it stands.</summary>
public sealed class AgentTurnFailedException(NodeExecutionResult result) : Exception(result.Error)
{
    public NodeExecutionResult Result { get; } = result;
}
