using System.Reflection;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Local;

namespace ILD.Tests;

/// <summary>What a command run for ILD prints is read up to a limit: stdout that ILD parses fails there, stderr is cut.</summary>
public sealed class AgentProcessLimitsTests
{
    /// <summary>Prints a letter forever; counts what it handed out.</summary>
    private sealed class EndlessStream(byte letter) : Stream
    {
        public long Produced;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = Math.Min(buffer.Length, 4096);
            buffer.Span[..count].Fill(letter);
            Produced += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
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

    private sealed class FakeProcess(Stream stdout, Stream stderr, bool exitsWithoutKill) : IRunProcess
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Killed;
        public int Id => 1;
        public StreamReader StandardOutput { get; } = new(stdout, Encoding.UTF8);
        public StreamReader StandardError { get; } = new(stderr, Encoding.UTF8);
        public StreamWriter StandardInput { get; } = new(Stream.Null);
        public bool HasExited => _exited.Task.IsCompleted;
        public int ExitCode => 0;
        public Task WaitForExitAsync(CancellationToken ct = default) => exitsWithoutKill ? Task.CompletedTask : _exited.Task.WaitAsync(ct);
        public bool TryKill() => TryKill(out _);

        public bool TryKill(out string? failure)
        {
            Killed++;
            _exited.TrySetResult();
            failure = null;
            return true;
        }

        public void Dispose() { }
    }

    private static RunReadLimits Limits => new() { MaxOutputBytes = 100 * 1024 };

    [Fact]
    public async Task Endless_stdout_kills_the_process_and_fails_at_the_limit()
    {
        var stdout = new EndlessStream((byte)'o');
        using var process = new FakeProcess(stdout, Stream.Null, exitsWithoutKill: false);

        await Assert.ThrowsAsync<ReadLimitExceededException>(
            () => AgentProcess.RunAsync(process, null, TestContext.Current.CancellationToken, Limits).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        Assert.Equal(1, process.Killed);
        Assert.InRange(stdout.Produced, 100 * 1024, 100 * 1024 + 2 * 8192 + 4096);
    }

    [Fact]
    public async Task Endless_stderr_is_cut_with_a_marker_while_the_process_runs_to_its_end()
    {
        using var process = new FakeProcess(Stream.Null, new CappedEndless(200 * 1024), exitsWithoutKill: true);

        var result = await AgentProcess.RunAsync(process, null, TestContext.Current.CancellationToken, Limits);

        Assert.Contains("truncated", result.Stderr);
        Assert.InRange(result.Stderr.Length, 64 * 1024, 64 * 1024 + 64);
    }

    [Fact]
    public async Task Output_within_the_limit_comes_back_whole()
    {
        using var process = new FakeProcess(new MemoryStream("fine\n"u8.ToArray()), new MemoryStream("warn\n"u8.ToArray()), exitsWithoutKill: true);

        var result = await AgentProcess.RunAsync(process, null, TestContext.Current.CancellationToken, Limits);

        Assert.Equal("fine\n", result.Stdout);
        Assert.Equal("warn\n", result.Stderr);
    }

    /// <summary>A finite stream of the size, so the test ends when it is read through.</summary>
    private sealed class CappedEndless(int length) : MemoryStream(new byte[length].Select(_ => (byte)'e').ToArray());

    [Fact]
    public void The_configured_bundle_limit_reaches_the_mirror()
    {
        var runner = new ProcessRunner();
        var manager = new RepositoryManager(runner, new LocalRunHost(runner), null, null, null,
            readEnvironment: name => name == RunReadLimits.MaxBundleMbVariable ? "7" : null);

        var mirrors = typeof(RepositoryManager).GetField("_mirrors", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
        var bytes = (long)mirrors.GetType().GetProperty("MaxBundleBytes")!.GetValue(mirrors)!;

        Assert.Equal(7 * RunReadLimits.Megabyte, bytes);
    }
}
