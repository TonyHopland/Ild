using System.Globalization;

namespace ILD.Core.Services.RunHosts;

/// <summary>
/// How much ILD reads out of a run's container or pod before it stops: what the run produces is not trusted to be small.
/// Each limit is a number of megabytes in an environment variable; anything malformed or not positive falls back to the default.
/// </summary>
public sealed class RunReadLimits
{
    public const string MaxFileMbVariable = "ILD_MAX_RUN_FILE_MB";
    public const string MaxOutputMbVariable = "ILD_MAX_RUN_OUTPUT_MB";
    public const string MaxBundleMbVariable = "ILD_MAX_BUNDLE_MB";
    public const string MaxPreviewDataMbVariable = "ILD_MAX_PREVIEW_DATA_MB";

    public const long Megabyte = 1024 * 1024;

    public const int DefaultMaxFileMb = 64;
    public const int DefaultMaxOutputMb = 16;
    public const int DefaultMaxBundleMb = 2048;
    public const int DefaultMaxPreviewDataMb = 1024;

    /// <summary>A file read whole or by range out of a run.</summary>
    public long MaxFileBytes { get; init; } = DefaultMaxFileMb * Megabyte;

    /// <summary>The output of a command that ILD keeps in memory, or in a log.</summary>
    public long MaxOutputBytes { get; init; } = DefaultMaxOutputMb * Megabyte;

    /// <summary>A bundle taken from a run.</summary>
    public long MaxBundleBytes { get; init; } = DefaultMaxBundleMb * Megabyte;

    /// <summary>The archive of a run's preview data taken back from it.</summary>
    public long MaxPreviewDataBytes { get; init; } = DefaultMaxPreviewDataMb * Megabyte;

    public static RunReadLimits Default { get; } = new();

    public static RunReadLimits FromEnvironment(Func<string, string?>? readVariable = null)
    {
        var read = readVariable ?? Environment.GetEnvironmentVariable;
        return new RunReadLimits
        {
            MaxFileBytes = Megabytes(read(MaxFileMbVariable), DefaultMaxFileMb),
            MaxOutputBytes = Megabytes(read(MaxOutputMbVariable), DefaultMaxOutputMb),
            MaxBundleBytes = Megabytes(read(MaxBundleMbVariable), DefaultMaxBundleMb),
            MaxPreviewDataBytes = Megabytes(read(MaxPreviewDataMbVariable), DefaultMaxPreviewDataMb),
        };
    }

    private static long Megabytes(string? raw, int fallbackMb)
        => (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) && mb > 0 ? mb : fallbackMb) * Megabyte;
}

/// <summary>What a run produced went past a limit of <see cref="RunReadLimits"/>.</summary>
public sealed class ReadLimitExceededException(long limit)
    : IOException($"more than {limit / RunReadLimits.Megabyte} MB came out of the run, which is the most ILD reads")
{
    public long Limit { get; } = limit;
}

/// <summary>A write-only stream that passes on at most <c>cap</c> bytes: the write that would pass it throws, and nothing of it is written.</summary>
public sealed class CappedWriteStream(Stream inner, long cap) : Stream
{
    private long _written;

    public long Written => _written;

    private void Take(int count)
    {
        if (count > cap - _written) throw new ReadLimitExceededException(cap);
        _written += count;
    }

    public override void Write(byte[] buffer, int offset, int count) { Take(count); inner.Write(buffer, offset, count); }

    public override void Write(ReadOnlySpan<byte> buffer) { Take(buffer.Length); inner.Write(buffer); }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Take(buffer.Length);
        return inner.WriteAsync(buffer, cancellationToken);
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>A write-only stream that keeps the first <c>cap</c> bytes and discards the rest, without failing the writer: for what is only read by a person.</summary>
internal sealed class TruncatingWriteStream(Stream inner, long cap) : Stream
{
    private long _written;

    public bool Truncated { get; private set; }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var keep = (int)Math.Min(buffer.Length, cap - _written);
        if (keep > 0) inner.Write(buffer[..keep]);
        _written += keep;
        if (keep < buffer.Length) Truncated = true;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void Flush() { }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
