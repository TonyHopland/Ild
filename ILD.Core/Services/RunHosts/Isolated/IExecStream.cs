namespace ILD.Core.Services.RunHosts.Isolated;

/// <summary>
/// One command running inside a run's pod or container, over a single stream: its stdin, stdout and stderr and
/// the exit status the runtime reports when it ends. What a transport (a Kubernetes exec websocket, a Docker exec
/// connection) has to give the run hosts that share <see cref="ExecRunHost"/>.
/// </summary>
public interface IExecStream : IDisposable
{
    Stream Stdout { get; }

    /// <summary>Null for a terminal, whose stderr is mixed into stdout.</summary>
    Stream? Stderr { get; }

    /// <summary>The exit code of the command; null when the stream ended without one, which means it was dropped.</summary>
    Task<int?> Status { get; }

    Task WriteStdinAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>Send end-of-file on stdin, so a command that reads its input to the end can finish.</summary>
    Task CloseStdinAsync(CancellationToken ct = default);

    Task ResizeAsync(int cols, int rows, CancellationToken ct = default);
}
