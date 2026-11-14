using System.Text;

namespace ILD.Core.Services.Implementations.Files;

/// <summary>A write-only stream that keeps the first bytes written and only counts the rest.</summary>
internal sealed class CappedStream : Stream
{
    private readonly MemoryStream _kept = new();
    private readonly int _cap;

    public CappedStream(int cap) { _cap = cap; }

    public long OmittedBytes { get; private set; }

    public string Text => Encoding.UTF8.GetString(_kept.GetBuffer(), 0, (int)_kept.Length);

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var room = Math.Max(0, _cap - (int)_kept.Length);
        var take = Math.Min(room, buffer.Length);
        _kept.Write(buffer[..take]);
        OmittedBytes += buffer.Length - take;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        Write(buffer.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
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
