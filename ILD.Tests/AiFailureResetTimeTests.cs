using ILD.Core.Services.Implementations;

namespace ILD.Tests;

/// <summary>
/// Reading back <em>when</em> a provider says its limit lifts, so an automatic
/// resume does not spend an attempt inside a window the provider has already
/// told us about. The bias is the classifier's: a time this cannot place
/// exactly is no time at all, because the caller's own retry schedule is a
/// perfectly good answer and a misread one parks a run for hours it did not
/// have to wait.
/// </summary>
public class AiFailureResetTimeTests
{
    private static readonly DateTime Now = new(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc);

    private static DateTime? Parse(string? output, string? error = null, DateTime? now = null)
        => AiFailureClassifier.TryParseResetAt(output, error, now ?? Now, out var at) ? at : null;

    [Fact]
    public void Reads_the_notice_a_real_run_was_throttled_with()
    {
        // The literal text from the run this was asked for.
        Assert.Equal(
            new DateTime(2026, 9, 3, 19, 10, 0, DateTimeKind.Utc),
            Parse("You've hit your session limit · resets 7:10pm (UTC)"));
    }

    [Theory]
    [InlineData("resets 7:10pm (UTC)", 19, 10)]
    [InlineData("Your limit will reset at 8pm UTC", 20, 0)]
    [InlineData("resets 19:10 UTC", 19, 10)]
    [InlineData("session limit reached · resets 11:59pm (UTC)", 23, 59)]
    [InlineData("available again at 6:30 PM (GMT)", 18, 30)]
    public void Reads_a_time_the_provider_stamped_utc(string notice, int hour, int minute)
        => Assert.Equal(new DateTime(2026, 9, 3, hour, minute, 0, DateTimeKind.Utc), Parse(notice));

    [Fact]
    public void Rolls_a_time_already_past_today_on_to_tomorrow()
    {
        // Throttled at 10pm and told the limit lifts at 9:40am: that clock face
        // is behind us today, so it means tomorrow morning's — the overnight
        // wait the horizon is set wide enough to keep.
        Assert.Equal(
            new DateTime(2026, 9, 4, 9, 40, 0, DateTimeKind.Utc),
            Parse("You've hit your session limit · resets 9:40am (UTC)",
                now: new DateTime(2026, 9, 3, 22, 0, 0, DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData("Rate limited. Try again in 30 seconds.", 0, 0, 30)]
    [InlineData("rate_limit_error · retry in 20 minutes", 0, 20, 0)]
    [InlineData("overloaded_error, try again in 2 hours", 2, 0, 0)]
    public void Reads_a_relative_wait(string notice, int h, int m, int s)
        => Assert.Equal(Now.AddHours(h).AddMinutes(m).AddSeconds(s), Parse(notice));

    [Fact]
    public void Ignores_a_time_with_no_zone_on_it()
    {
        // The zone is the user's and ILD does not know it. Guessing costs hours
        // of a run's day when it guesses west; the caller's own schedule costs
        // one wasted attempt when it is early. Take the cheaper mistake.
        Assert.Null(Parse("Claude usage limit reached. Your limit will reset at 3pm."));
        Assert.Null(Parse("You've hit your session limit · resets 9:40am"));
    }

    [Fact]
    public void Ignores_a_wait_further_out_than_any_real_window()
    {
        // 5:59pm is a minute behind us, so reading it literally means tomorrow —
        // 23h59 away, which is a misread far more often than it is a limit. The
        // caller falls back to its own retry ladder instead.
        Assert.Null(Parse("resets 5:59pm (UTC)"));
        Assert.Null(Parse("try again in 20 hours"));
    }

    [Theory]
    [InlineData("You've hit your session limit")]
    [InlineData("API Error: 429 {\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\"}}")]
    [InlineData("resets 25:00 (UTC)")]
    [InlineData("resets 7:99pm (UTC)")]
    [InlineData("")]
    [InlineData(null)]
    public void Reports_nothing_when_the_notice_states_no_usable_time(string? notice)
        => Assert.Null(Parse(notice));

    [Fact]
    public void Falls_back_to_the_error_text_when_the_output_states_nothing()
    {
        // Same order the classifier reads them in: the notice normally rides on
        // the agent's output, but a structured provider error lands in the error.
        Assert.Equal(
            new DateTime(2026, 9, 3, 19, 10, 0, DateTimeKind.Utc),
            Parse("exit=1", "rate_limit_error · resets 7:10pm (UTC)"));
    }

    [Fact]
    public void Prefers_the_output_the_provider_wrote_the_notice_into()
    {
        Assert.Equal(
            new DateTime(2026, 9, 3, 19, 10, 0, DateTimeKind.Utc),
            Parse("session limit · resets 7:10pm (UTC)", "retry in 45 minutes"));
    }
}
