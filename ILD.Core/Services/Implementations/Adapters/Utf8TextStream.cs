using System.Text;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// A string's UTF-8 bytes, encoded a read at a time rather than copied out whole: the same
/// bytes <see cref="Encoding.UTF8"/> gives, without a BOM.
/// </summary>
internal sealed class Utf8TextStream(string text) : Stream
{
    private readonly Encoder _encoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetEncoder();
    private readonly byte[] _carry = new byte[4];
    private int _position;
    private bool _completed;
    private int _carryStart;
    private int _carryLength;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        if (_carryLength == 0 && !_completed && buffer.Length < _carry.Length)
        {
            // A character can take four bytes, more than this read has room for: it is encoded
            // into the carry and handed out from there.
            _carryStart = 0;
            _carryLength = Encode(_carry);
        }
        if (_carryLength > 0)
        {
            var count = Math.Min(buffer.Length, _carryLength);
            _carry.AsSpan(_carryStart, count).CopyTo(buffer);
            _carryStart += count;
            _carryLength -= count;
            return count;
        }
        return _completed ? 0 : Encode(buffer);
    }

    private int Encode(Span<byte> bytes)
    {
        _encoder.Convert(text.AsSpan(_position), bytes, flush: true, out var charsUsed, out var bytesUsed, out _completed);
        _position += charsUsed;
        return bytesUsed;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Read(buffer.Span));

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
