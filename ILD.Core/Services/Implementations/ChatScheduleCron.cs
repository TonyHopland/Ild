namespace ILD.Core.Services.Implementations;

/// <summary>
/// ILD's own five-field cron, read in an IANA time zone and answered in UTC.
/// Fields are minute 0-59, hour 0-23, day of month 1-31, month 1-12 and day of
/// week 0-7 (0 and 7 are both Sunday), each <c>*</c>, a value, a list,
/// <c>a-b</c>, <c>*/n</c>, <c>a-b/n</c> or <c>a/n</c>. When both day fields are
/// restricted a day matching either counts; when only one is, only that one does.
/// </summary>
public static class ChatScheduleCron
{
    /// <summary>
    /// The longest a valid expression can go without firing: 29 February skips
    /// 2100, so from 2096 the next one is 2104.
    /// </summary>
    private const int SearchYears = 9;

    // Daylight saving moves wall-clock time by at most a few hours, so a local
    // time further back than this from the instant searched after cannot follow it.
    private static readonly TimeSpan MaxZoneShift = TimeSpan.FromHours(3);

    public sealed record Expression(
        ulong Minutes, ulong Hours, ulong DaysOfMonth, ulong Months, ulong DaysOfWeek,
        bool DaysOfMonthRestricted, bool DaysOfWeekRestricted);

    private sealed record Field(string Name, int Min, int Max);

    private static readonly Field Minute = new("minute", 0, 59);
    private static readonly Field Hour = new("hour", 0, 23);
    private static readonly Field DayOfMonth = new("day of month", 1, 31);
    private static readonly Field Month = new("month", 1, 12);
    private static readonly Field DayOfWeek = new("day of week", 0, 7);

    // The most days each month can have; February counts its leap day.
    private static readonly int[] MonthLengths = [31, 29, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    /// <summary>The parsed expression; throws <see cref="FormatException"/> naming what is wrong.</summary>
    public static Expression Parse(string expression)
        => TryParse(expression, out var parsed, out var error) ? parsed : throw new FormatException(error);

    /// <summary>Whether <paramref name="expression"/> parses and ever fires; <paramref name="error"/> says why not.</summary>
    public static bool TryParse(string? expression, out Expression parsed, out string error)
    {
        parsed = null!;
        var fields = (expression ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = "A cron needs five fields: minute, hour, day of month, month and day of week.";
            return false;
        }

        ulong minutes = 0, hours = 0, days = 0, months = 0, weekdays = 0;
        if (!TryParseField(fields[0], Minute, ref minutes, out error)
            || !TryParseField(fields[1], Hour, ref hours, out error)
            || !TryParseField(fields[2], DayOfMonth, ref days, out error)
            || !TryParseField(fields[3], Month, ref months, out error)
            || !TryParseField(fields[4], DayOfWeek, ref weekdays, out error))
            return false;

        if ((weekdays & (1UL << 7)) != 0)
            weekdays = (weekdays & ~(1UL << 7)) | 1UL;

        parsed = new Expression(minutes, hours, days, months, weekdays,
            DaysOfMonthRestricted: days != Span(DayOfMonth.Min, DayOfMonth.Max),
            DaysOfWeekRestricted: weekdays != Span(0, 6));

        if (!EverFires(parsed))
        {
            error = $"'{expression}' never fires: none of its months has that day.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// The first instant strictly after <paramref name="afterUtc"/> at which the
    /// expression fires in <paramref name="zone"/>, in UTC. A local time that
    /// daylight saving skips fires at the end of the gap; one that happens twice
    /// fires once, at the earlier instant.
    /// </summary>
    public static DateTime Next(Expression expression, DateTime afterUtc, TimeZoneInfo zone)
    {
        afterUtc = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        var localAfter = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone);
        var earliestLocal = localAfter - MaxZoneShift;
        var day = DateOnly.FromDateTime(earliestLocal);
        var lastDay = day.AddYears(SearchYears).AddDays(1);

        for (; day <= lastDay; day = day.AddDays(1))
        {
            if (!FiresOn(expression, day)) continue;
            for (var hour = 0; hour < 24; hour++)
            {
                if (!Has(expression.Hours, hour)) continue;
                for (var minute = 0; minute < 60; minute++)
                {
                    if (!Has(expression.Minutes, minute)) continue;
                    var local = day.ToDateTime(new TimeOnly(hour, minute));
                    if (local < earliestLocal) continue;
                    var instant = ToUtc(local, zone);
                    if (instant > afterUtc) return instant;
                }
            }
        }

        throw new InvalidOperationException($"The cron fires nowhere in the {SearchYears} years after {afterUtc:o}.");
    }

    private static bool FiresOn(Expression e, DateOnly day)
    {
        if (!Has(e.Months, day.Month)) return false;
        var byDate = Has(e.DaysOfMonth, day.Day);
        var byWeekday = Has(e.DaysOfWeek, (int)day.DayOfWeek);
        if (e.DaysOfMonthRestricted && e.DaysOfWeekRestricted) return byDate || byWeekday;
        return byDate && byWeekday;
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
            var largestOffset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - largestOffset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    // Day of week never decides on its own whether a cron fires, since every
    // weekday falls in every month sooner or later; only a date that no selected
    // month has can make it never fire.
    private static bool EverFires(Expression e)
    {
        if (e.DaysOfWeekRestricted) return true;
        for (var month = 1; month <= 12; month++)
        {
            if (!Has(e.Months, month)) continue;
            for (var day = 1; day <= MonthLengths[month - 1]; day++)
                if (Has(e.DaysOfMonth, day)) return true;
        }
        return false;
    }

    private static bool TryParseField(string text, Field field, ref ulong selected, out string error)
    {
        foreach (var item in text.Split(','))
        {
            if (!TryParseItem(item, field, out var bits, out error)) return false;
            selected |= bits;
        }
        error = string.Empty;
        return true;
    }

    private static bool TryParseItem(string item, Field field, out ulong bits, out string error)
    {
        bits = 0;
        error = $"'{item}' is not a valid {field.Name}: use *, a number from {field.Min} to {field.Max}, a list, a-b, */n, a-b/n or a/n.";

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
                if (!TryNumber(range, out from)) return false;
                to = slash >= 0 ? field.Max : from;
            }

            if (from < field.Min || to > field.Max || from > to) return false;
        }

        for (var value = from; value <= to; value += step)
            bits |= 1UL << value;
        error = string.Empty;
        return true;
    }

    private static bool TryNumber(string text, out int value)
    {
        value = 0;
        if (text.Length is 0 or > 4 || !text.All(char.IsAsciiDigit)) return false;
        value = int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static ulong Span(int from, int to)
    {
        ulong bits = 0;
        for (var value = from; value <= to; value++) bits |= 1UL << value;
        return bits;
    }

    private static bool Has(ulong bits, int value) => (bits & (1UL << value)) != 0;
}
