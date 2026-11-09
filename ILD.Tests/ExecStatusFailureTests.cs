using System.Text.Json;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Kubernetes;
using Porta.Pty;

namespace ILD.Tests;

/// <summary>An exec whose exit status cannot be read ends as a lost connection; nothing waits on it forever.</summary>
public sealed class ExecStatusFailureTests
{
    private sealed class StubExec(Task<int?> status) : IExecStream
    {
        public Stream Stdout { get; } = new MemoryStream("out"u8.ToArray());
        public Stream? Stderr { get; } = new MemoryStream();
        public Task<int?> Status { get; } = status;
        public Task WriteStdinAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => Task.CompletedTask;
        public Task CloseStdinAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeAsync(int cols, int rows, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    public static TheoryData<bool> Failures => new() { false, true };

    private static Task<int?> StatusFailing(bool unreadable)
        => unreadable ? Task.FromException<int?>(new JsonException("not json")) : Task.FromResult<int?>(null);

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_command_whose_status_is_missing_or_unreadable_exits_as_a_lost_connection(bool unreadable)
    {
        var ct = TestContext.Current.CancellationToken;
        using var process = new ExecRunProcess(new StubExec(StatusFailing(unreadable)), "pod", "owner/id", redirectInput: false, (_, _) => Task.FromResult(true));

        await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.Equal(ExecRunProcess.ConnectionLostExitCode, process.ExitCode);
        Assert.Equal("out", await process.StandardOutput.ReadToEndAsync(ct));
        Assert.Contains("connection", await process.StandardError.ReadToEndAsync(ct));
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_terminal_whose_status_is_missing_or_unreadable_reports_its_exit(bool unreadable)
    {
        var raised = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pty = new ExecPty(new StubExec(release.Task));
        pty.ProcessExited += (_, e) => raised.TrySetResult(e.ExitCode);

        if (unreadable) release.SetException(new JsonException("not json")); else release.SetResult(null);

        Assert.Equal(ExecRunProcess.ConnectionLostExitCode, await raised.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(ExecRunProcess.ConnectionLostExitCode, pty.ExitCode);
    }

    [Fact]
    public async Task A_status_that_is_not_json_reads_as_no_status()
    {
        using var error = new MemoryStream("not json"u8.ToArray());

        Assert.Null(await PodExec.ReadStatusAsync(error));
    }
}
