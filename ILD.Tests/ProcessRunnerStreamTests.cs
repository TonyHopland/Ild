using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

/// <summary>The streams <see cref="ProcessRunOptions"/> can attach to a child.</summary>
public class ProcessRunnerStreamTests
{
    private sealed class RefusingSink : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("no space left on device");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new IOException("no space left on device");
    }

    [Fact]
    public async Task Stdout_goes_to_the_sink_byte_for_byte()
    {
        using var sink = new MemoryStream();

        var result = await new ProcessRunner().RunAsync(
            "sh", ["-c", "printf '\\000\\377abc'"], ct: TestContext.Current.CancellationToken,
            options: new ProcessRunOptions(StandardOutputTo: sink));

        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(new byte[] { 0, 0xFF, (byte)'a', (byte)'b', (byte)'c' }, sink.ToArray());
    }

    [Fact]
    public async Task Stdin_is_read_from_the_stream_to_its_end()
    {
        using var source = new MemoryStream("from the stream"u8.ToArray());

        var result = await new ProcessRunner().RunAsync(
            "cat", [], ct: TestContext.Current.CancellationToken,
            options: new ProcessRunOptions(StandardInputFrom: source));

        Assert.Equal("from the stream", result.StdOut);
    }

    [Fact]
    public async Task A_child_that_stops_reading_its_input_is_not_an_error_of_the_runner()
    {
        using var source = new MemoryStream(new byte[4 * 1024 * 1024]);

        var result = await new ProcessRunner().RunAsync(
            "sh", ["-c", "exit 3"], ct: TestContext.Current.CancellationToken,
            options: new ProcessRunOptions(StandardInputFrom: source));

        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task A_sink_that_cannot_take_the_output_stops_the_child_instead_of_leaving_it_blocked()
    {
        var run = new ProcessRunner().RunAsync(
            "yes", [], ct: TestContext.Current.CancellationToken,
            options: new ProcessRunOptions(StandardOutputTo: new RefusingSink()));

        await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }
}
