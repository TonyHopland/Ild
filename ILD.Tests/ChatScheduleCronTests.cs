using ILD.Core.Services.Implementations;

namespace ILD.Tests;

/// <summary>
/// ILD's own five-field cron: when a schedule fires next, worked out in the
/// schedule's IANA zone and answered in UTC. Expressions it refuses are covered
/// where a user meets the refusal, in ChatSchedulesApiTests.
/// </summary>
public sealed class ChatScheduleCronTests
{
    private static readonly TimeZoneInfo Oslo = TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");

    private static DateTime Utc(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;

    private static DateTime Next(string expression, DateTime afterUtc, TimeZoneInfo zone)
    {
        DateTimeOffset next = ChatScheduleCron.Next(ChatScheduleCron.Parse(expression), afterUtc, zone);
        return next.UtcDateTime;
    }

    [Theory]
    // Summer (CEST, UTC+2) and winter (CET, UTC+1) for the same local 08:00.
    [InlineData("0 8 * * 1", "2026-07-01T00:00:00Z", "2026-07-06T06:00:00Z")]
    [InlineData("0 8 * * 1", "2026-01-01T00:00:00Z", "2026-01-05T07:00:00Z")]
    // Strictly after: a firing instant itself moves on to the following week.
    [InlineData("0 8 * * 1", "2026-07-06T06:00:00Z", "2026-07-13T06:00:00Z")]
    // 02:30 does not exist on 2026-03-29: the first valid instant after the gap.
    [InlineData("30 2 * * *", "2026-03-28T12:00:00Z", "2026-03-29T01:00:00Z")]
    public void The_next_firing_is_computed_in_the_schedules_zone_and_answered_in_utc(string expression, string after, string expected)
    {
        Assert.Equal(Utc(expected), Next(expression, Utc(after), Oslo));
    }

    [Fact]
    public void A_local_time_that_happens_twice_fires_once_at_the_earlier_instant()
    {
        // 02:30 on 2026-10-25 is 00:30Z (CEST) and again 01:30Z (CET).
        var first = Next("30 2 * * *", Utc("2026-10-24T12:00:00Z"), Oslo);
        Assert.Equal(Utc("2026-10-25T00:30:00Z"), first);

        var second = Next("30 2 * * *", first, Oslo);
        Assert.Equal(Utc("2026-10-26T01:30:00Z"), second);
    }

    [Fact]
    public void Successive_firings_across_both_changes_of_a_year_are_strictly_increasing_and_one_per_day()
    {
        var at = Utc("2026-01-01T00:00:00Z");
        var days = new HashSet<DateOnly>();
        while (at < Utc("2027-01-01T00:00:00Z"))
        {
            var next = Next("30 2 * * *", at, Oslo);
            Assert.True(next > at, $"{next:o} is not after {at:o}");
            var localDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(next, Oslo));
            Assert.True(days.Add(localDay), $"fired twice on {localDay}");
            at = next;
        }

        Assert.Equal(365, days.Count(d => d.Year == 2026));
    }

    [Fact]
    public void The_29th_of_february_is_found_across_a_century_that_skips_its_leap_year()
    {
        Assert.Equal(Utc("2104-02-29T00:00:00Z"), Next("0 0 29 2 *", Utc("2099-01-01T00:00:00Z"), TimeZoneInfo.Utc));
    }

    [Theory]
    // Day of week 0 and 7 are both Sunday. 2026-10-08 is a Thursday.
    [InlineData("0 0 * * 7", "2026-10-08T00:00:00Z", "2026-10-11T00:00:00Z")]
    [InlineData("0 0 * * 0", "2026-10-08T00:00:00Z", "2026-10-11T00:00:00Z")]
    // Both day fields restricted: the 13th or a Friday, whichever comes first.
    [InlineData("0 0 13 * 5", "2026-10-08T00:00:00Z", "2026-10-09T00:00:00Z")]
    [InlineData("0 0 13 * 5", "2026-10-09T00:00:00Z", "2026-10-13T00:00:00Z")]
    // Only one restricted: only that one counts.
    [InlineData("0 0 13 * *", "2026-10-08T00:00:00Z", "2026-10-13T00:00:00Z")]
    [InlineData("0 0 * * 5", "2026-10-09T00:00:00Z", "2026-10-16T00:00:00Z")]
    // No 30 February, but under "either" every Monday in February still fires.
    [InlineData("0 0 30 2 1", "2026-01-01T00:00:00Z", "2026-02-02T00:00:00Z")]
    // Steps, ranges and lists.
    [InlineData("5/15 * * * *", "2026-10-08T10:06:00Z", "2026-10-08T10:20:00Z")]
    [InlineData("5/15 * * * *", "2026-10-08T10:51:00Z", "2026-10-08T11:05:00Z")]
    [InlineData("10-30/10 * * * *", "2026-10-08T10:21:00Z", "2026-10-08T10:30:00Z")]
    [InlineData("10-30/10 * * * *", "2026-10-08T10:31:00Z", "2026-10-08T11:10:00Z")]
    [InlineData("*/20 * * * *", "2026-10-08T10:41:00Z", "2026-10-08T11:00:00Z")]
    [InlineData("0 9,17 * * *", "2026-10-08T09:00:00Z", "2026-10-08T17:00:00Z")]
    [InlineData("0 0 1 1-3/2 *", "2026-01-01T00:00:00Z", "2026-03-01T00:00:00Z")]
    [InlineData("0 0 * * 1-5", "2026-10-09T00:00:00Z", "2026-10-12T00:00:00Z")]
    public void Fields_select_the_instants_the_expression_names(string expression, string after, string expected)
    {
        Assert.Equal(Utc(expected), Next(expression, Utc(after), TimeZoneInfo.Utc));
    }
}
