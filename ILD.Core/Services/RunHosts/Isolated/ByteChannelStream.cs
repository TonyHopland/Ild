using System.Threading.Channels;

namespace ILD.Core.Services.RunHosts.Isolated;

/// <summary>
/// A readable stream fed by another task: what a pod's command printed, handed to the caller as it arrives, ended by
/// <see cref="Complete"/>. Bounded: a producer that gets ahead of the reader waits in <see cref="AppendAsync"/> until
/// the reader catches up, so a command that prints without end cannot grow ILD's memory. Disposing the stream releases
/// a waiting producer and drops what it was holding.
/// </summary>
internal sealed class ByteChannelStream : Stream
{
    /// <summary>Chunks held before a producer has to wait.</summary>
    public const int Capacity = 64;

    private readonly Channel<byte[]> _chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly CancellationTokenSource _gone = new();
    private byte[] _current = [];
    private int _offset;

    public async ValueTask AppendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_gone.IsCancellationRequested) return;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _gone.Token);
            await _chunks.Writer.WriteAsync(data.ToArray(), linked.Token);
        }
        catch (OperationCanceledException) when (_gone.IsCancellationRequested && !ct.IsCancellationRequested) { }
        catch (ChannelClosedException) { }
    }

    public void Complete() => _chunks.Writer.TryComplete();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _gone.Cancel(); } catch (ObjectDisposedException) { }
            _chunks.Writer.TryComplete();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_offset >= _current.Length)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
            if (_chunks.Reader.TryRead(out var next))
            {
                _current = next;
                _offset = 0;
            }
        }

        var count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
