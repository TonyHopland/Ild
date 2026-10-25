namespace ILD.Data.Backup;

/// <summary>
/// A stream that is read or written once, front to back, with no position of its own. A subclass
/// overrides <see cref="ReadAsync(Memory{byte}, CancellationToken)"/> or
/// <see cref="WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/> and the rest is refused here.
/// </summary>
public abstract class ForwardOnlyStream(bool canRead, bool canWrite) : Stream
{
    public override bool CanRead => canRead;
    public override bool CanWrite => canWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
