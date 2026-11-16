using System.Diagnostics;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.DTOs;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>What one launch of a CLI is started with.</summary>
/// <param name="Environment">What the adapter sets for the launch; a null value unsets.</param>
/// <param name="AgentFiles">The files in the agent read root that the adapter wrote for the launch and that <paramref name="StartInfo"/> points at.</param>
public sealed record AgentLaunch(
    ProcessStartInfo StartInfo,
    IReadOnlyDictionary<string, string?>? Environment = null,
    IEnumerable<string?>? AgentFiles = null);

/// <summary>What one launch's standard output says, in the terms every CLI's stream is read into.</summary>
/// <param name="Text">What the agent answered, as the CLI's stream carried it.</param>
/// <param name="SessionId">The session the stream named, if it did.</param>
/// <param name="ProviderError">The provider's own message when the stream reported a failed turn, apart from the agent's narration.</param>
/// <param name="SawJsonEvents">Whether the output was an event stream at all; when it was not, the raw output stands in for a missing answer.</param>
/// <param name="RawStdout">The whole of standard output.</param>
/// <param name="Truncation">The error to fail with when the CLI ended its stream before its turn did, which a clean exit cannot make up for; null when it did not.</param>
public sealed record StreamResult(
    string Text,
    string? SessionId,
    string? ProviderError,
    bool SawJsonEvents,
    string RawStdout,
    string? Truncation = null);

/// <summary>
/// What is particular to one CLI in one agent turn: how it is launched, how its output reads, and where its session lives.
/// <see cref="CliAgentAdapterBase.ExecuteAsync"/> drives it, and owns everything every CLI shares: the worktree check, the model
/// gateway, starting the process, the timeouts and the kill, the processes the agent left running, the managed session (fork,
/// restore, save) and what a finished turn is classified as.
/// </summary>
public abstract class CliAgentTurn : IAsyncDisposable
{
    /// <summary>The CLI being started, named when it cannot be.</summary>
    public abstract string BinaryPath { get; }

    /// <summary>What has to be in place before the launch: config files. The session is the base's (see <see cref="RestoreSessionAsync"/>).</summary>
    public virtual Task PrepareAsync() => Task.CompletedTask;

    /// <summary>
    /// The session the CLI is launched to resume, or null for a new one: what <see cref="ReadSessionAsync"/> made of the
    /// execution's session once it was forked and restored. Set by the base before the launch.
    /// </summary>
    public string? ResumeSessionId { get; internal set; }

    /// <summary>Whether <paramref name="sessionId"/> may be looked up and stored; a CLI whose ids become part of a file name refuses what would not be one.</summary>
    public virtual bool IsSessionId(string sessionId) => true;

    /// <summary>Brings the stored session back to where the CLI resumes it from, once it was forked. Runs for a managed session only.</summary>
    /// <returns>False when there is nothing to resume, so the CLI starts a new session. A failure that must end the turn throws <see cref="Failed"/>.</returns>
    public virtual Task<bool> RestoreSessionAsync(string sessionId) => Task.FromResult(true);

    /// <summary>
    /// Reads what the turn needs of the session it resumes (the total to count on from, whether the CLI has it), after the fork and
    /// the restore. Runs for every turn, with null when there is no session to resume.
    /// </summary>
    /// <returns>The session to launch the CLI on: <paramref name="sessionId"/>, or null when the CLI cannot resume it.</returns>
    public virtual Task<string?> ReadSessionAsync(string? sessionId) => Task.FromResult(sessionId);

    /// <summary>Stores the session the CLI left, after a managed turn that exited cleanly. Saving is observational: it never fails the turn.</summary>
    /// <returns>What the stored session says the agent answered, for a CLI whose stream can come without it; otherwise null.</returns>
    public virtual Task<string?> SaveSessionAsync(string sessionId) => Task.FromResult<string?>(null);

    /// <summary>The launch. A failure to build it is reported as a failure to start the CLI.</summary>
    public abstract Task<AgentLaunch> LaunchAsync();

    /// <summary>Another launch to try after <paramref name="failure"/> stopped the first from starting, or null to give up.</summary>
    public virtual Task<AgentLaunch?> RetryLaunchAsync(Exception failure) => Task.FromResult<AgentLaunch?>(null);

    /// <summary>Writes what the CLI reads from standard input, and closes it where the CLI waits for that.</summary>
    public virtual Task SendInputAsync(IRunProcess process) => Task.CompletedTask;

    /// <summary>Reads standard output to its end, keeping what <see cref="ParseStream"/> needs of it.</summary>
    public abstract Task ReadStdoutAsync(StreamReader stdout);

    public virtual Task<string> ReadStderrAsync(StreamReader stderr, CancellationToken ct) => stderr.ReadToEndAsync(ct);

    /// <summary>Called once the CLI was killed for outliving the turn, with the still running read of its standard output.</summary>
    public virtual Task AfterTimeoutAsync(Task stdout) => Task.CompletedTask;

    /// <summary>What <see cref="ReadStdoutAsync"/> kept, in the terms the base classifies the turn from. Called once, after the read.</summary>
    public abstract StreamResult ParseStream();

    /// <summary>
    /// Whether a clean exit whose output was not an event stream and was blank is a failure. It is for the CLIs that have always
    /// failed it; the others have always passed the blank output on as the answer.
    /// </summary>
    public virtual bool BlankOutputFails => false;

    /// <summary>What a turn that succeeded used; null when the CLI does not say.</summary>
    public virtual Task<TokenUsage?> UsageAsync() => Task.FromResult<TokenUsage?>(null);

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
