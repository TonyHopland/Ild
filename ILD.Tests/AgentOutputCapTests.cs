using System.Text;
using ILD.Core.Services.RunHosts;

namespace ILD.Tests;

public sealed class AgentOutputCapTests
{
    private sealed class StubProcess(string stdout, string stderr) : IRunProcess
    {
        public int Kills { get; private set; }
        public int Id => 1;
        public StreamReader StandardOutput { get; } = new(new MemoryStream(Encoding.UTF8.GetBytes(stdout)), Encoding.UTF8);
        public StreamReader StandardError { get; } = new(new MemoryStream(Encoding.UTF8.GetBytes(stderr)), Encoding.UTF8);
        public StreamWriter StandardInput { get; } = new(new MemoryStream());
        public bool HasExited => false;
        public int ExitCode => 0;
        public Task WaitForExitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public bool TryKill() => TryKill(out _);

        public bool TryKill(out string? failure)
        {
            Kills++;
            failure = null;
            return true;
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task Output_within_the_limit_is_read_whole_on_both_streams()
    {
        var inner = new StubProcess("{\"type\":\"result\"}\nblåbær\n", "warning\n");
        using var process = new OutputCappedRunProcess(inner, 1024);

        Assert.Equal("{\"type\":\"result\"}", await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken));
        Assert.Equal("blåbær", await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken));
        Assert.Equal("warning\n", await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, inner.Kills);
    }

    [Fact]
    public async Task A_line_with_no_end_stops_at_the_limit_and_the_process_is_killed_once()
    {
        var inner = new StubProcess(new string('x', 64 * 1024), string.Empty);
        using var process = new OutputCappedRunProcess(inner, 4096);

        await Assert.ThrowsAsync<ReadLimitExceededException>(
            async () => await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ReadLimitExceededException>(
            async () => await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, inner.Kills);
    }

    [Fact]
    public async Task The_error_stream_has_the_same_limit_of_its_own()
    {
        var inner = new StubProcess("ok\n", new string('e', 64 * 1024));
        using var process = new OutputCappedRunProcess(inner, 4096);

        Assert.Equal("ok", await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ReadLimitExceededException>(
            () => process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, inner.Kills);
    }
}
