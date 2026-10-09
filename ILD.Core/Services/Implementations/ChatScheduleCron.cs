using System.Globalization;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// ILD's own five-field cron for Chat Schedules, read in the schedule's time zone
/// and answered in UTC. Fields are minute 0-59, hour 0-23, day of month 1-31,
/// month 1-12 and day of week 0-7 (0 and 7 are both Sunday), each <c>*</c>, a
/// number, a comma list, <c>a-b</c>, <c>*/n</c> or <c>a-b/n</c>. When both day
/// fields are restricted (neither starts with <c>*</c>) a day matching either one
/// counts, as in standard cron; otherwise the restricted one decides.
/// </summary>
public sealed class ChatScheduleCron
{
    // The longest a valid expression can go without firing: 29 February skips
    // 2100, so from early 2096 the next one is 2104.
    private const int SearchYears = 9;

    // Daylight saving moves the wall clock by a few hours at most, so no local
    // time further back than this from the instant searched after can follow it.
    private static readonly TimeSpan MaxZoneShift = TimeSpan.FromHours(3);

    private sealed record Field(string Name, int Min, int Max);

    private static readonly Field Minute = new("minute", 0, 59);
    private static readonly Field Hour = new("hour", 0, 23);
    private static readonly Field DayOfMonth = new("day of month", 1, 31);
    private static readonly Field Month = new("month", 1, 12);
    private static readonly Field DayOfWeek = new("day of week", 0, 7);

    // The most days each month can have; February counts its leap day.
    private static readonly int[] MonthLengths = [31, 29, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    private readonly ulong _minutes;
    private readonly ulong _hours;
    private readonly ulong _daysOfMonth;
    private readonly ulong _months;
    private readonly ulong _daysOfWeek;
    private readonly bool _bothDaysRestricted;

    private ChatScheduleCron(ulong minutes, ulong hours, ulong daysOfMonth, ulong months, ulong daysOfWeek, bool bothDaysRestricted)
    {
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _bothDaysRestricted = bothDaysRestricted;
    }

    /// <summary>The parsed expression; throws <see cref="FormatException"/> saying what is wrong with it.</summary>
    public static ChatScheduleCron Parse(string expression)
        => TryParse(expression, out var cron, out var error) ? cron : throw new FormatException(error);

    /// <summary>Whether <paramref name="expression"/> parses and can ever fire; <paramref name="error"/> says why not.</summary>
    public static bool TryParse(string? expression, out ChatScheduleCron cron, out string error)
    {
        cron = null!;
        var fields = (expression ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = "A cron needs five fields: minute, hour, day of month, month and day of week.";
            return false;
        }

        if (!TryParseField(fields[0], Minute, out var minutes, out error)
            || !TryParseField(fields[1], Hour, out var hours, out error)
            || !TryParseField(fields[2], DayOfMonth, out var daysOfMonth, out error)
            || !TryParseField(fields[3], Month, out var months, out error)
            || !TryParseField(fields[4], DayOfWeek, out var daysOfWeek, out error))
            return false;

        const ulong sundayAsSeven = 1UL << 7;
        if ((daysOfWeek & sundayAsSeven) != 0) daysOfWeek = (daysOfWeek & ~sundayAsSeven) | 1UL;

        cron = new ChatScheduleCron(minutes, hours, daysOfMonth, months, daysOfWeek,
            bothDaysRestricted: !fields[2].StartsWith('*') && !fields[4].StartsWith('*'));
        if (!cron.CanFire())
        {
            error = $"'{expression}' never fires: none of its months has that day.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// The first instant strictly after <paramref name="afterUtc"/> at which the
    /// expression fires in <paramref name="zone"/>, in UTC. A local time that the
    /// spring-forward gap skips fires at the first valid moment after it; one that
    /// the fall-back overlap repeats fires once, on its first occurrence.
    /// </summary>
    public DateTime Next(DateTime afterUtc, TimeZoneInfo zone)
    {
        afterUtc = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        var earliestLocal = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone) - MaxZoneShift;
        var day = DateOnly.FromDateTime(earliestLocal);
        var lastDay = day.AddYears(SearchYears);

        for (; day <= lastDay; day = day.AddDays(1))
        {
            if (!FiresOn(day)) continue;
            for (var hour = 0; hour < 24; hour++)
            {
                if (!Has(_hours, hour)) continue;
                for (var minute = 0; minute < 60; minute++)
                {
                    if (!Has(_minutes, minute)) continue;
                    var local = day.ToDateTime(new TimeOnly(hour, minute));
                    if (local < earliestLocal) continue;
                    var instant = ToUtc(local, zone);
                    if (instant > afterUtc) return instant;
                }
            }
        }

        // Unreachable for an expression that parsed: CanFire rejects every one that never fires.
        throw new InvalidOperationException($"The cron does not fire in the {SearchYears} years after {afterUtc:o}.");
    }

    private bool FiresOn(DateOnly day)
    {
        if (!Has(_months, day.Month)) return false;
        var byDate = Has(_daysOfMonth, day.Day);
        var byWeekday = Has(_daysOfWeek, (int)day.DayOfWeek);
        return _bothDaysRestricted ? byDate || byWeekday : byDate && byWeekday;
    }

    // Every date falls on every weekday sooner or later, so only a day of month
    // that none of the months has can keep an expression from ever firing, and
    // not even that when a weekday alone can make the day match.
    private bool CanFire()
    {
        if (_bothDaysRestricted) return true;
        for (var month = 1; month <= 12; month++)
        {
            if (!Has(_months, month)) continue;
            for (var day = 1; day <= MonthLengths[month - 1]; day++)
                if (Has(_daysOfMonth, day)) return true;
        }
        return false;
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(local))
        {
            var gapEnd = local;
            do gapEnd = gapEnd.AddMinutes(1);
            while (zone.IsInvalidTime(gapEnd));
            return TimeZoneInfo.ConvertTimeToUtc(gapEnd, zone);
        }

        if (zone.IsAmbiguousTime(local))
        {
            // The larger offset is the one in force before the clocks went back.
            var firstOffset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - firstOffset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    private static bool TryParseField(string text, Field field, out ulong selected, out string error)
    {
        selected = 0;
        foreach (var item in text.Split(','))
        {
            if (!TryParseItem(item, field, out var bits))
            {
                error = $"'{item}' is not a valid {field.Name}: use *, a number from {field.Min} to {field.Max}, a list, a-b, */n or a-b/n.";
                return false;
            }
            selected |= bits;
        }
        error = string.Empty;
        return true;
    }

    private static bool TryParseItem(string item, Field field, out ulong bits)
    {
        bits = 0;
        var step = 1;
        var range = item;
        var slash = item.IndexOf('/');
        if (slash >= 0)
        {
            if (!TryNumber(item[(slash + 1)..], out step) || step == 0) return false;
            range = item[..slash];
        }

        int from, to;
        if (range == "*")
        {
            (from, to) = (field.Min, field.Max);
        }
        else
        {
            var dash = range.IndexOf('-');
            if (dash >= 0)
            {
                if (!TryNumber(range[..dash], out from) || !TryNumber(range[(dash + 1)..], out to)) return false;
            }
            else
            {
                // A step needs a range to walk: "5/10" is not one of the accepted forms.
                if (slash >= 0 || !TryNumber(range, out from)) return false;
                to = from;
            }

            if (from < field.Min || to > field.Max || from > to) return false;
        }

        for (var value = from; value <= to; value += step)
            bits |= 1UL << value;
        return true;
    }

    private static bool TryNumber(string text, out int value)
    {
        value = 0;
        return text.Length is > 0 and <= 4
            && text.All(char.IsAsciiDigit)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool Has(ulong bits, int value) => (bits & (1UL << value)) != 0;
}
