namespace ILD.Tests.Fakes;

/// <summary>
/// A clock that stands still at <see cref="Now"/> until a test moves it, from any
/// thread. Its timers never fire, so nothing a host schedules on it runs unless the
/// test drives it.
/// </summary>
internal sealed class TestClock(DateTime now) : TimeProvider
{
    private long _ticks = now.Ticks;

    public DateTime Now
    {
        get => new(Interlocked.Read(ref _ticks), DateTimeKind.Utc);
        set => Interlocked.Exchange(ref _ticks, value.Ticks);
    }

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => new NeverTimer();

    private sealed class NeverTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
