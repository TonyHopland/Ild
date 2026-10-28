namespace ILD.Tests.Fakes;

/// <summary>A clock that stands still at <see cref="Now"/> until a test moves it.</summary>
internal sealed class TestClock(DateTime now) : TimeProvider
{
    public DateTime Now = now;

    public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
}
