using System.Text;

namespace ILD.Core.Services.RunHosts.Isolated;

/// <summary>A command running in a run's pod or container, over one exec stream. A stream that ends without an exit status is a command that failed.</summary>
internal sealed class ExecRunProcess : IRunProcess
{
    /// <summary>What <see cref="ExitCode"/> is when the stream to the run ended before the command reported how it did.</summary>
    internal const int ConnectionLostExitCode = -1;

    private static int _nextId;

    private readonly IExecStream _exec;
    private readonly string _where;
    private readonly Func<string, CancellationToken, Task<bool>> _kill;
    private readonly ByteChannelStream _stdout = new();
    private readonly ByteChannelStream _stderr = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _exitCode;

    private readonly Action? _onExit;

    /// <param name="onExit">Called once the command has ended, whatever way it did.</param>
    public ExecRunProcess(IExecStream exec, string where, string commandId, bool redirectInput, Func<string, CancellationToken, Task<bool>> kill, Action? onExit = null)
    {
        _onExit = onExit;
        _exec = exec;
        _where = where;
        CommandId = commandId;
        _kill = kill;
        Id = Interlocked.Increment(ref _nextId);
        StandardOutput = new StreamReader(_stdout, Encoding.UTF8);
        StandardError = new StreamReader(_stderr, Encoding.UTF8);
        StandardInput = redirectInput
            ? new StreamWriter(new StdinStream(exec), new UTF8Encoding(false)) { AutoFlush = true }
            : StreamWriter.Null;
        _ = Task.Run(PumpAsync);
    }

    /// <summary>The name the command records its process group under in the run.</summary>
    public string CommandId { get; }

    public int Id { get; }
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }
    public StreamWriter StandardInput { get; }
    public bool HasExited => _exited.Task.IsCompleted;

    public int ExitCode => HasExited ? _exitCode : throw new InvalidOperationException("the command has not exited");

    public Task WaitForExitAsync(CancellationToken ct = default) => _exited.Task.WaitAsync(ct);

    public bool TryKill() => TryKill(out _);

    public bool TryKill(out string? failure)
    {
        failure = null;
        if (HasExited) return true;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (_kill(CommandId, limit.Token).GetAwaiter().GetResult()) return true;
            failure = $"the {_where} did not confirm that the command was gone";
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        return HasExited;
    }

    private async Task PumpAsync()
    {
        int? status = null;
        try
        {
            var stdout = Forward(_exec.Stdout, _stdout);
            var stderr = _exec.Stderr is null ? Task.CompletedTask : Forward(_exec.Stderr, _stderr);
            status = await _exec.Status;
            await Task.WhenAll(stdout, stderr);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A status that cannot be read is a lost connection.
            status = null;
        }
        finally
        {
            if (status is null)
                _stderr.Append(Encoding.UTF8.GetBytes($"the connection to the run's {_where} ended before the command did\n"));
            _exitCode = status ?? ConnectionLostExitCode;
            _stdout.Complete();
            _stderr.Complete();
            _onExit?.Invoke();
            _exited.TrySetResult();
        }
    }

    private static async Task Forward(Stream from, ByteChannelStream to)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer)) > 0)
                to.Append(buffer.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.WebSockets.WebSocketException)
        {
            // The connection broke: the status says it, or its absence does.
        }
    }

    public void Dispose() => _exec.Dispose();

    private sealed class StdinStream(IExecStream exec) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count)
            => exec.WriteStdinAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => exec.WriteStdinAsync(buffer.AsMemory(offset, count), cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => new(exec.WriteStdinAsync(buffer, cancellationToken));

        protected override void Dispose(bool disposing)
        {
            if (!disposing) return;
            try { exec.CloseStdinAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.WebSockets.WebSocketException or InvalidOperationException) { }
            base.Dispose(disposing);
        }

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
