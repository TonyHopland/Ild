using Porta.Pty;

namespace ILD.Core.Services.RunHosts.Isolated;

/// <summary>A terminal in a run (an exec with a TTY) behind the interface the terminal bridge reads and writes.</summary>
internal sealed class ExecPty : IPtyConnection
{
    private readonly IExecStream _exec;
    private readonly ByteChannelStream _output = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly OutputStream _input;
    private int _exitRaised;

    public ExecPty(IExecStream exec)
    {
        _exec = exec;
        _input = new OutputStream(exec);
        _ = Task.Run(PumpAsync);
    }

    public event EventHandler<PtyExitedEventArgs>? ProcessExited;

    public Stream ReaderStream => _output;
    public Stream WriterStream => _input;
    public bool SupportsCancellableRead => true;
    public int Pid => 0;
    public int ExitCode => _exited.Task.IsCompleted ? _exited.Task.Result : 0;

    public bool WaitForExit(int milliseconds)
        => _exited.Task.Wait(milliseconds);

    public void Kill() => _exec.Dispose();

    public void Resize(int cols, int rows)
        => _exec.ResizeAsync(cols, rows).GetAwaiter().GetResult();

    public void Dispose() => _exec.Dispose();

    // Porta.Pty constructs these itself; its constructor is internal.
    private static PtyExitedEventArgs ExitedWith(int code)
        => (PtyExitedEventArgs)Activator.CreateInstance(
            typeof(PtyExitedEventArgs), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, [code], null)!;

    private async Task PumpAsync()
    {
        int? status = null;
        try
        {
            var buffer = new byte[16 * 1024];
            try
            {
                int read;
                while ((read = await _exec.Stdout.ReadAsync(buffer)) > 0)
                    await _output.AppendAsync(buffer.AsMemory(0, read));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.WebSockets.WebSocketException)
            {
                // Read as the end of the terminal.
            }

            status = await _exec.Status;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A status that cannot be read is a lost connection.
        }
        finally
        {
            var code = status ?? ExecRunProcess.ConnectionLostExitCode;
            _exited.TrySetResult(code);
            _output.Complete();
            if (Interlocked.Exchange(ref _exitRaised, 1) == 0)
                ProcessExited?.Invoke(this, ExitedWith(code));
        }
    }

    private sealed class OutputStream(IExecStream exec) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count)
            => exec.WriteStdinAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => exec.WriteStdinAsync(buffer.AsMemory(offset, count), cancellationToken);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
