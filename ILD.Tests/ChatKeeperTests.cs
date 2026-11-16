using ILD.Core.Services.RunHosts.Isolated;
using ILD.Tests.Fakes;

namespace ILD.Tests;

/// <summary>Keeping, holding and reaping the chat container, on an engine that only records what it was asked, with the clock in the test's hands.</summary>
public class ChatKeeperTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ManualTimeProvider _time = new();
    private readonly RecordingLogger _log = new();
    private readonly StubEngine _engine = new();

    private sealed class StubEngine : IChatEngine
    {
        public bool Up { get; set; }
        public int Made { get; private set; }
        public int Removed { get; private set; }
        public Func<Task>? OnMake { get; set; }
        public Func<Task>? OnExists { get; set; }
        public Func<Task>? OnRemove { get; set; }

        public string Name => "the-chat";
        public Task<bool> IsUpAsync(CancellationToken ct) => Task.FromResult(Up);

        public async Task MakeAsync(CancellationToken ct)
        {
            if (OnMake is { } hook) await hook();
            Made++;
            Up = true;
        }

        public async Task RemoveAsync(CancellationToken ct)
        {
            if (OnRemove is { } hook) await hook();
            Removed++;
            Up = false;
        }

        public async Task<bool> ExistsAsync(CancellationToken ct)
        {
            if (OnExists is { } hook) await hook();
            return Up;
        }
    }

    private ChatKeeper Keeper(TimeSpan? timeout = null) => new(_engine, timeout ?? Timeout, _time, "container", _log);

    [Fact]
    public async Task The_container_is_made_when_it_is_not_up_and_found_when_it_is()
    {
        var keeper = Keeper();

        Assert.Equal("the-chat", await keeper.EnsureAsync(Ct));
        Assert.Equal("the-chat", await keeper.EnsureAsync(Ct));

        Assert.Equal(1, _engine.Made);
    }

    [Fact]
    public async Task Callers_that_arrive_together_make_the_container_once()
    {
        var keeper = Keeper();
        var making = new TaskCompletionSource();
        _engine.OnMake = () => making.Task;

        var first = keeper.EnsureAsync(Ct);
        var second = keeper.EnsureAsync(Ct);
        making.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, _engine.Made);
    }

    [Fact]
    public async Task The_first_look_only_starts_the_clock_for_a_container_that_was_there_before_ILD()
    {
        var keeper = Keeper();
        _engine.Up = true;

        await keeper.ReapIdleAsync(Ct);
        Assert.Equal(0, _engine.Removed);

        _time.Advance(Timeout - TimeSpan.FromSeconds(1));
        await keeper.ReapIdleAsync(Ct);
        Assert.Equal(0, _engine.Removed);

        _time.Advance(TimeSpan.FromSeconds(1));
        await keeper.ReapIdleAsync(Ct);
        Assert.Equal(1, _engine.Removed);
    }

    [Fact]
    public async Task An_idle_container_is_removed_at_the_timeout_and_made_again_for_the_next_chat_which_is_logged_once()
    {
        var keeper = Keeper();
        using (keeper.Begin()) { }
        await keeper.EnsureAsync(Ct);

        _time.Advance(Timeout - TimeSpan.FromSeconds(1));
        await keeper.ReapIdleAsync(Ct);
        Assert.Equal(0, _engine.Removed);

        _time.Advance(TimeSpan.FromSeconds(2));
        await keeper.ReapIdleAsync(Ct);
        Assert.Equal(1, _engine.Removed);

        await keeper.EnsureAsync(Ct);
        await keeper.EnsureAsync(Ct);
        Assert.Equal(2, _engine.Made);
        Assert.Single(_log.At(Microsoft.Extensions.Logging.LogLevel.Information), t => t.Contains("is back"));
        Assert.Single(_log.At(Microsoft.Extensions.Logging.LogLevel.Information), t => t.Contains("it was idle for 10 minutes"));
    }

    [Fact]
    public async Task A_container_made_for_the_first_time_is_not_logged_as_back()
    {
        await Keeper().EnsureAsync(Ct);

        Assert.DoesNotContain(_log.Entries, e => e.Text.Contains("is back"));
    }

    [Fact]
    public async Task Open_work_is_not_idle_however_long_ago_it_started_and_the_time_counts_from_when_it_ends()
    {
        var keeper = Keeper();
        await keeper.EnsureAsync(Ct);

        using (keeper.Begin())
        {
            _time.Advance(Timeout * 2);
            await keeper.ReapIdleAsync(Ct);
            Assert.Equal(0, _engine.Removed);
        }

        await keeper.ReapIdleAsync(Ct);
        Assert.Equal(0, _engine.Removed);
        _time.Advance(Timeout);
        await keeper.ReapIdleAsync(Ct);
        Assert.Equal(1, _engine.Removed);
    }

    [Fact]
    public async Task Work_that_is_disposed_twice_is_closed_once()
    {
        var keeper = Keeper();
        await keeper.EnsureAsync(Ct);
        var held = keeper.Begin();
        using var other = keeper.Begin();

        held.Dispose();
        held.Dispose();
        _time.Advance(Timeout * 2);
        await keeper.ReapIdleAsync(Ct);

        Assert.Equal(0, _engine.Removed);
    }

    [Fact]
    public async Task A_timeout_of_zero_never_reaps()
    {
        var keeper = Keeper(TimeSpan.Zero);
        await keeper.EnsureAsync(Ct);

        _time.Advance(TimeSpan.FromDays(30));
        await keeper.ReapIdleAsync(Ct);

        Assert.Equal(0, _engine.Removed);
    }

    [Fact]
    public async Task A_container_that_is_not_there_is_not_removed()
    {
        var keeper = Keeper();
        using (keeper.Begin()) { }
        _time.Advance(Timeout);

        await keeper.ReapIdleAsync(Ct);

        Assert.Equal(0, _engine.Removed);
    }

    [Fact]
    public async Task A_chat_that_starts_while_the_reaper_removes_the_container_waits_for_it_and_gets_a_new_one()
    {
        var keeper = Keeper();
        using (keeper.Begin()) { }
        await keeper.EnsureAsync(Ct);
        var removing = new TaskCompletionSource();
        var proceed = new TaskCompletionSource();
        _engine.OnRemove = async () =>
        {
            removing.SetResult();
            await proceed.Task;
        };
        _time.Advance(Timeout);

        var reaping = keeper.ReapIdleAsync(Ct);
        await removing.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        // What it sees is the container, still up: it must not trust that.
        Assert.True(_engine.Up);
        var starting = keeper.EnsureAsync(Ct);
        await Task.Delay(100, Ct);
        Assert.False(starting.IsCompleted);
        proceed.SetResult();
        await reaping;
        await starting;

        Assert.Equal(2, _engine.Made);
        Assert.Equal(1, _engine.Removed);
    }

    [Fact]
    public async Task Work_that_begins_while_the_reaper_looks_whether_the_container_is_there_stops_the_removal()
    {
        var keeper = Keeper();
        using (keeper.Begin()) { }
        await keeper.EnsureAsync(Ct);
        _time.Advance(Timeout);
        ChatWork? arrived = null;
        _engine.OnExists = () =>
        {
            arrived = keeper.Begin();
            return Task.CompletedTask;
        };

        await keeper.ReapIdleAsync(Ct);

        Assert.Equal(0, _engine.Removed);
        arrived!.Dispose();
    }

    [Fact]
    public async Task A_chat_exec_counts_as_open_work_until_its_command_ends_or_the_stream_is_disposed_whichever_is_first()
    {
        var keeper = Keeper();
        await keeper.EnsureAsync(Ct);
        var status = new TaskCompletionSource<int?>();
        var stream = new ChatExecStream(new StubStream(status.Task), keeper.Begin());

        _time.Advance(Timeout * 2);
        await keeper.ReapIdleAsync(Ct);
        Assert.Equal(0, _engine.Removed);

        status.SetResult(0);
        await stream.Status;
        await keeper.ReapIdleAsync(Ct);
        Assert.Equal(0, _engine.Removed);
        _time.Advance(Timeout);
        await keeper.ReapIdleAsync(Ct);
        Assert.Equal(1, _engine.Removed);
    }

    private sealed class StubStream(Task<int?> status) : IExecStream
    {
        public Stream Stdout => Stream.Null;
        public Stream? Stderr => Stream.Null;
        public Task<int?> Status => status;
        public Task WriteStdinAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => Task.CompletedTask;
        public Task CloseStdinAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeAsync(int cols, int rows, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }
}
