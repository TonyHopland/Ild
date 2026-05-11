using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations.Executors;

/// <summary>
/// Per-node timeout linked-token. Encapsulates the "300s default, derived
/// linked CTS, distinguish timeout-vs-caller-cancel" pattern that previously
/// lived (copied) in <see cref="CmdNodeExecutor"/> and
/// <see cref="AINodeExecutor"/>.
///
/// Usage:
/// <code>
///   using var t = NodeTimeoutScope.From(ctx);
///   try { await DoWork(t.Token); }
///   catch (OperationCanceledException) when (t.TimedOut) {
///       return new NodeOutcome.Failed($"command timed out after {t.Duration}");
///   }
/// </code>
///
/// Owning this in one place means new timeout-bearing executors (or new
/// callers) cannot accidentally diverge on the default, the linkage, or the
/// "is this a timeout or a caller cancel?" check.
/// </summary>
internal sealed class NodeTimeoutScope : IDisposable
{
    public const double DefaultSeconds = 300;

    public TimeSpan Duration { get; }
    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// True iff this scope's timeout fired and the original caller token
    /// was not itself cancelled. Use to distinguish a deadline expiry from
    /// an external cancellation when handling
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public bool TimedOut
        => _cts.IsCancellationRequested && !_caller.IsCancellationRequested;

    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _caller;

    private NodeTimeoutScope(TimeSpan duration, CancellationToken caller)
    {
        Duration = duration;
        _caller = caller;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(caller);
        _cts.CancelAfter(duration);
    }

    public static NodeTimeoutScope From(NodeExecutionContext ctx)
        => From(ctx.Node.TimeoutSeconds, ctx.CancellationToken);

    public static NodeTimeoutScope From(double nodeTimeoutSeconds, CancellationToken caller)
    {
        var secs = nodeTimeoutSeconds > 0 ? nodeTimeoutSeconds : DefaultSeconds;
        return new NodeTimeoutScope(TimeSpan.FromSeconds(secs), caller);
    }

    public void Dispose() => _cts.Dispose();
}
