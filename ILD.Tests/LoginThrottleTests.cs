using ILD.Core.Services.Implementations;

namespace ILD.Tests;

public class LoginThrottleTests
{
    private readonly FakeTimeProvider _clock = new();

    private LoginThrottle Throttle() => new(_clock);

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static void Use(LoginThrottle t, int attempts, string ip = "10.0.0.1", string user = "admin", bool run = true)
    {
        for (var i = 0; i < attempts; i++) Assert.Null(t.TryBegin(ip, user, run));
    }

    // Attempts, each made when the last wait is over, until the wait is at its cap.
    private void Saturate(LoginThrottle t, string ip)
    {
        for (var i = 0; i < 60; i++)
        {
            if (t.TryBegin(ip, "admin", true) is { } wait)
            {
                _clock.Advance(wait);
                Assert.Null(t.TryBegin(ip, "admin", true));
            }
        }
    }

    [Fact]
    public void The_first_attempts_are_free()
    {
        var t = Throttle();
        Use(t, LoginThrottle.FreeAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), t.TryBegin("10.0.0.1", "admin", true));
    }

    [Fact]
    public void After_the_free_attempts_each_one_waits_twice_as_long()
    {
        var t = Throttle();
        Use(t, LoginThrottle.FreeAttempts);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(t.TryBegin("10.0.0.1", "admin", true));
        Assert.Equal(TimeSpan.FromSeconds(2), t.TryBegin("10.0.0.1", "admin", true));
    }

    [Fact]
    public void The_delay_is_capped_and_never_a_lockout()
    {
        var t = Throttle();
        Saturate(t, "10.0.0.1");
        Assert.Equal(LoginThrottle.MaxDelay, t.TryBegin("10.0.0.1", "admin", true));

        _clock.Advance(LoginThrottle.MaxDelay);
        Assert.Null(t.TryBegin("10.0.0.1", "admin", true));
    }

    [Fact]
    public void Attempts_sent_at_once_cannot_all_pass_once_the_free_ones_are_used()
    {
        var t = Throttle();
        Use(t, LoginThrottle.FreeAttempts);
        _clock.Advance(TimeSpan.FromSeconds(1));

        var passed = Enumerable.Range(0, 200).AsParallel().Count(_ => t.TryBegin("10.0.0.1", "admin", true) is null);

        Assert.Equal(1, passed);
    }

    [Fact]
    public void A_run_holding_the_user_name_at_the_cap_does_not_hold_up_an_address_outside_a_run_network()
    {
        var t = Throttle();
        Saturate(t, "10.88.0.5");
        Assert.NotNull(t.TryBegin("10.88.0.6", "admin", true));

        Assert.Null(t.TryBegin("192.168.1.20", "admin", false));
    }

    [Fact]
    public void An_address_is_throttled_for_every_user_name_and_a_user_name_from_every_run_address()
    {
        var t = Throttle();
        Use(t, LoginThrottle.FreeAttempts);

        Assert.NotNull(t.TryBegin("10.0.0.99", "ADMIN", true));
        Assert.NotNull(t.TryBegin("10.0.0.1", "someone-else", true));
        Assert.Null(t.TryBegin("10.0.0.99", "someone-else", true));
    }

    [Fact]
    public void A_success_gives_back_what_the_attempt_counted()
    {
        var t = Throttle();
        Use(t, LoginThrottle.FreeAttempts - 1, "192.168.1.20", "admin", run: false);
        t.Succeeded("192.168.1.20", "admin", false);
        Use(t, LoginThrottle.FreeAttempts, "192.168.1.20", "admin", run: false);
    }
}
