using ILD.Core.Services.Implementations;

namespace ILD.Tests;

public class LoginThrottleTests
{
    private readonly FakeTimeProvider _clock = new();

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private LoginThrottle Throttle() => new(_clock);

    private static void Fail(LoginThrottle t, int times, string ip = "10.0.0.1", string user = "admin")
    {
        for (var i = 0; i < times; i++) t.RecordFailure(ip, user);
    }

    [Fact]
    public void The_first_failures_are_free()
    {
        var t = Throttle();
        Fail(t, LoginThrottle.FreeFailures - 1);
        Assert.Null(t.RetryAfter("10.0.0.1", "admin"));
    }

    [Fact]
    public void After_the_free_failures_each_attempt_waits_twice_as_long()
    {
        var t = Throttle();
        Fail(t, LoginThrottle.FreeFailures);
        Assert.Equal(TimeSpan.FromSeconds(1), t.RetryAfter("10.0.0.1", "admin"));

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(t.RetryAfter("10.0.0.1", "admin"));
        t.RecordFailure("10.0.0.1", "admin");
        Assert.Equal(TimeSpan.FromSeconds(2), t.RetryAfter("10.0.0.1", "admin"));
    }

    [Fact]
    public void The_delay_is_capped_and_never_a_lockout()
    {
        var t = Throttle();
        Fail(t, 60);
        Assert.Equal(LoginThrottle.MaxDelay, t.RetryAfter("10.0.0.1", "admin"));

        _clock.Advance(LoginThrottle.MaxDelay);
        Assert.Null(t.RetryAfter("10.0.0.1", "admin"));
    }

    [Fact]
    public void A_user_name_is_throttled_from_any_address_and_an_address_for_any_name()
    {
        var t = Throttle();
        Fail(t, LoginThrottle.FreeFailures);

        Assert.NotNull(t.RetryAfter("10.0.0.99", "ADMIN"));
        Assert.NotNull(t.RetryAfter("10.0.0.1", "someone-else"));
        Assert.Null(t.RetryAfter("10.0.0.99", "someone-else"));
    }

    [Fact]
    public void A_success_clears_the_user_name_but_not_the_address()
    {
        var t = Throttle();
        Fail(t, LoginThrottle.FreeFailures);

        t.RecordSuccess("admin");

        Assert.Null(t.RetryAfter("10.0.0.99", "admin"));
        Assert.NotNull(t.RetryAfter("10.0.0.1", "admin"));
    }
}
