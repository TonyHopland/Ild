using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Local;
using ILD.Core.Services.RunHosts.Tools;

namespace ILD.Tests;

/// <summary>What ILD reads out of a run stops at a limit, and stops there: nothing is buffered past it.</summary>
public sealed class RunReadLimitsTests
{
    private const int Chunk = 16 * 1024;

    private static RunReadLimits Limits(long bytes) => new() { MaxFileBytes = bytes, MaxOutputBytes = bytes, MaxBundleBytes = bytes, MaxPreviewDataBytes = bytes };

    private static Host NewHost(IExecStream exec, long limit) => new(exec) { ReadLimits = Limits(limit) };

    [Fact]
    public async Task Collected_output_stops_at_the_limit_and_the_command_is_not_read_further()
    {
        var exec = new EndlessExec();
        var host = NewHost(exec, 100 * 1024);

        await Assert.ThrowsAsync<ReadLimitExceededException>(() => host.ReadTextFileAsync(host.Path, TestContext.Current.CancellationToken));

        Assert.InRange(exec.Produced, 100 * 1024, 100 * 1024 + 2 * Chunk);
        Assert.True(exec.Disposed);
    }

    [Fact]
    public async Task A_file_read_stops_at_the_limit_without_buffering_past_it()
    {
        var exec = new EndlessExec();
        var host = NewHost(exec, 100 * 1024);

        await Assert.ThrowsAsync<ReadLimitExceededException>(() => host.OpenReadAsync(host.Path, TestContext.Current.CancellationToken));

        Assert.InRange(exec.Produced, 100 * 1024, 100 * 1024 + 2 * Chunk);
    }

    [Fact]
    public async Task A_file_read_carries_its_size_check_in_the_command()
    {
        var exec = new EndlessExec();
        var host = NewHost(exec, 100 * 1024);

        await Assert.ThrowsAsync<ReadLimitExceededException>(() => host.OpenReadAsync(host.Path, TestContext.Current.CancellationToken));

        var command = string.Join(' ', host.Commands.Single());
        Assert.Contains("stat -c %s", command);
        Assert.Contains((100 * 1024).ToString(), command);
    }

    [Fact]
    public async Task A_range_longer_than_the_limit_is_refused_before_a_command_runs()
    {
        var exec = new EndlessExec();
        var host = NewHost(exec, 100 * 1024);

        await Assert.ThrowsAsync<ReadLimitExceededException>(
            () => host.ReadFileRangeAsync(host.Path, 0, 100 * 1024 + 1, _ => Task.CompletedTask));

        Assert.Empty(host.Commands);
        Assert.Equal(0, exec.Produced);
    }

    [Fact]
    public async Task A_range_read_stops_at_the_limit_without_buffering_past_it()
    {
        var exec = new EndlessExec();
        var host = NewHost(exec, 100 * 1024);

        await Assert.ThrowsAsync<ReadLimitExceededException>(
            () => host.ReadFileRangeAsync(host.Path, 0, 100 * 1024, _ => Task.CompletedTask));

        Assert.InRange(exec.Produced, 100 * 1024, 100 * 1024 + 2 * Chunk);
    }

    [Fact]
    public async Task A_bundle_sink_that_caps_itself_stops_the_command_at_its_cap()
    {
        var exec = new EndlessExec();
        var host = NewHost(exec, 100 * 1024);
        await using var sink = new MemoryStream();

        await Assert.ThrowsAsync<ReadLimitExceededException>(
            () => host.PullAsync(new CappedWriteStream(sink, 100 * 1024), TestContext.Current.CancellationToken));

        Assert.InRange(sink.Length, 1, 100 * 1024);
        Assert.InRange(exec.Produced, 100 * 1024, 100 * 1024 + 2 * Chunk);
    }

    [Fact]
    public async Task An_oversized_preview_data_archive_is_dropped_and_the_stored_copy_is_unchanged()
    {
        var scratch = Directory.CreateTempSubdirectory("ild-readlimit-").FullName;
        try
        {
            var repository = Guid.NewGuid();
            var store = new PreviewDataStore(Path.Combine(scratch, "store"));
            var stored = store.FolderOf(repository);
            Directory.CreateDirectory(stored);
            File.WriteAllText(Path.Combine(stored, "keep.txt"), "before");
            var exec = new EndlessExec();
            var host = NewHost(exec, 100 * 1024);

            await host.SyncAsync(repository, store, TestContext.Current.CancellationToken);

            Assert.Equal("before", File.ReadAllText(Path.Combine(stored, "keep.txt")));
            Assert.Equal(["keep.txt"], Directory.GetFileSystemEntries(stored).Select(Path.GetFileName).ToArray());
            Assert.InRange(exec.Produced, 100 * 1024, 100 * 1024 + 2 * Chunk);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task Stderr_is_kept_up_to_a_small_bound_with_a_marker()
    {
        var exec = new EndlessStderrExec();
        var host = NewHost(exec, 100 * 1024);

        var result = await host.PullAsync(new MemoryStream(), TestContext.Current.CancellationToken);

        Assert.Contains("truncated", result.StdErr);
        Assert.InRange(result.StdErr.Length, 64 * 1024, 64 * 1024 + 64);
    }

    private sealed class EndlessStderrExec : IExecStream
    {
        public Stream Stdout { get; } = new MemoryStream();
        public Stream? Stderr { get; } = new MemoryStream(new byte[256 * 1024].Select(_ => (byte)'e').ToArray());
        public Task<int?> Status { get; } = Task.FromResult<int?>(1);
        public Task WriteStdinAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => Task.CompletedTask;
        public Task CloseStdinAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeAsync(int cols, int rows, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    [Fact]
    public async Task A_full_channel_holds_its_producer_and_disposing_the_stream_releases_it()
    {
        var channel = new ByteChannelStream();
        var chunk = new byte[16];
        for (var i = 0; i < ByteChannelStream.Capacity; i++)
            await channel.AppendAsync(chunk, TestContext.Current.CancellationToken);

        var waiting = channel.AppendAsync(chunk, TestContext.Current.CancellationToken).AsTask();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);

        channel.Dispose();

        await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_producer_waiting_on_a_full_channel_goes_on_when_the_reader_catches_up()
    {
        using var channel = new ByteChannelStream();
        var chunk = new byte[16];
        for (var i = 0; i < ByteChannelStream.Capacity; i++)
            await channel.AppendAsync(chunk, TestContext.Current.CancellationToken);
        var waiting = channel.AppendAsync(chunk, TestContext.Current.CancellationToken).AsTask();

        Assert.Equal(16, await channel.ReadAsync(new byte[16], TestContext.Current.CancellationToken));

        await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Limits_are_read_as_megabytes_and_a_bad_value_falls_back_to_the_default()
    {
        var limits = RunReadLimits.FromEnvironment(name => name switch
        {
            RunReadLimits.MaxFileMbVariable => "8",
            RunReadLimits.MaxOutputMbVariable => "0",
            RunReadLimits.MaxBundleMbVariable => "lots",
            _ => null,
        });

        Assert.Equal(8 * RunReadLimits.Megabyte, limits.MaxFileBytes);
        Assert.Equal(RunReadLimits.DefaultMaxOutputMb * RunReadLimits.Megabyte, limits.MaxOutputBytes);
        Assert.Equal(RunReadLimits.DefaultMaxBundleMb * RunReadLimits.Megabyte, limits.MaxBundleBytes);
        Assert.Equal(RunReadLimits.DefaultMaxPreviewDataMb * RunReadLimits.Megabyte, limits.MaxPreviewDataBytes);
    }

    /// <summary>A command that prints without end, and counts what it handed out.</summary>
    private sealed class EndlessExec : IExecStream
    {
        public EndlessExec() => Stdout = new Endless(this);

        public long Produced;
        public bool Disposed;
        public Stream Stdout { get; }
        public Stream? Stderr { get; } = new MemoryStream();
        public Task<int?> Status { get; } = new TaskCompletionSource<int?>().Task;
        public Task WriteStdinAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => Task.CompletedTask;
        public Task CloseStdinAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeAsync(int cols, int rows, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => Disposed = true;

        private sealed class Endless(EndlessExec owner) : Stream
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var count = Math.Min(buffer.Length, Chunk);
                buffer.Span[..count].Fill((byte)'x');
                owner.Produced += count;
                return ValueTask.FromResult(count);
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
    }

    private sealed class Host(IExecStream exec) : ExecRunHost(new ProcessRunner(), null)
    {
        public Guid Owner { get; } = Guid.NewGuid();
        public string Path => RunPaths.WorkingDirectory(Owner) + "/file";
        public List<IReadOnlyList<string>> Commands { get; } = [];

        protected override Task<IExecStream> OpenExecInAsync(
            string name, IReadOnlyList<string> command, IReadOnlyDictionary<string, string>? environment, bool stdin, bool tty, bool asRoot, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult(exec);
        }

        public Task<RunHostResult> PullAsync(Stream sink, CancellationToken ct) => PullBundleAsync(RunPaths.WorkingDirectory(Owner), "main", false, sink, ct);

        public Task SyncAsync(Guid repository, PreviewDataStore store, CancellationToken ct)
        {
            PreviewData = store;
            return DeleteAsync(new RunEnvironment(Owner, RunPaths.WorkingDirectory(Owner), repository), ct);
        }

        public override RunHostKind Kind => throw new NotSupportedException();
        public override string LeftoverKind => throw new NotSupportedException();
        public override TimeSpan SweepInterval => throw new NotSupportedException();
        protected override string Noun => "pod";
        protected override string IldUrl => throw new NotSupportedException();
        protected override int EgressProxyPort => 0;
        protected override Task<bool> RunExistsAsync(ExecOwner owner, CancellationToken ct) => Task.FromResult(true);
        protected override string RunEnvironmentName(Guid run) => "run";
        protected override Task<bool> ReviveRunAsync(Guid run, Exception failure, CancellationToken ct) => Task.FromResult(false);
        protected override Task<bool> IsRunReadyAsync(Guid run, CancellationToken ct) => throw new NotSupportedException();
        protected override Task<Func<Task>> MakeRunAsync(RunEnvironmentRequest request, RunToolEnvironment? tools, CancellationToken ct) => throw new NotSupportedException();
        protected override Task RemoveRunAsync(Guid run, CancellationToken ct) => Task.CompletedTask;
        protected override Task StartToolInstallerAsync(string name, Guid id, ToolInstaller installer, CancellationToken ct) => throw new NotSupportedException();
        protected override Task RemoveToolInstallerAsync(string name) => throw new NotSupportedException();
        protected override Task StartSignInAsync(string name, Guid id, RunToolEnvironment? tools, CancellationToken ct) => throw new NotSupportedException();
        protected override Task RemoveSignInAsync(string name) => throw new NotSupportedException();
        protected override string ChatEnvironmentName => throw new NotSupportedException();
        protected override Task<bool> ChatIsUpAsync(CancellationToken ct) => throw new NotSupportedException();
        protected override Task MakeChatAsync(CancellationToken ct) => throw new NotSupportedException();
        protected override Task RemoveChatAsync(CancellationToken ct) => throw new NotSupportedException();
        protected override string ToolZoneName(RunToolMount zone) => throw new NotSupportedException();
        protected override Task OpenDirectRouteAsync(ExecOwner owner, CancellationToken ct) => throw new NotSupportedException();
        protected override Task CloseDirectRouteAsync(ExecOwner owner, CancellationToken ct) => throw new NotSupportedException();
        protected override Task<bool> HasDirectRouteAsync(ExecOwner owner, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<DirectRoute>> DirectRoutesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        protected override Task<bool> IsRunningAsync(Guid owner, CancellationToken ct) => Task.FromResult(true);
        protected override Task<string> RunAddressAsync(Guid run, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<RunLeftover>> ListLeftoversAsync(IReadOnlySet<Guid> liveIds, CancellationToken ct = default) => throw new NotSupportedException();
        public override Task<bool> DeleteLeftoverAsync(RunLeftover leftover, CancellationToken ct = default) => throw new NotSupportedException();
        protected override bool UsesToolStore => false;
    }
}
