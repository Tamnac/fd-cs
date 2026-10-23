using System.Globalization;

namespace Fd;

/// <summary>
/// A point in time as a file time, parsed from what --changed-within / --changed-before accept: a duration
/// before now (<c>10h</c>, <c>2 days 3 hours</c>), a date or date-time in local time, a timestamp with offset, or <c>@seconds</c>.
/// </summary>
readonly record struct TimeFilter(long FileTime)
{
    static readonly string[] CivilFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFFFF", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFFF",
    ];

    static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd HH:mm:ss.FFFFFFFFFK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFFFK",
    ];

    public static TimeFilter Parse(string text)
    {
        if (TryParseDuration(text, out DateTime moment)) return new TimeFilter(moment.ToFileTimeUtc());

        if (DateTimeOffset.TryParseExact(text, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            return new TimeFilter(timestamp.ToFileTime());

        if (DateTime.TryParseExact(text, CivilFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var civil))
            return new TimeFilter(DateTime.SpecifyKind(civil, DateTimeKind.Local).ToFileTimeUtc());

        if (text.StartsWith('@') && ulong.TryParse(text.AsSpan(1), out ulong seconds) && seconds < 253_402_300_800UL)
            return new TimeFilter(DateTime.UnixEpoch.AddSeconds(seconds).ToFileTimeUtc());

        throw new FdException($"'{text}' is not a valid date or duration. See 'fd --help'.");
    }

    public bool IsBefore(long fileTime) => fileTime < FileTime;
    public bool IsAfter(long fileTime) => fileTime > FileTime;

    /// <summary>A sequence of <c>number unit</c> pairs, optionally separated by spaces or commas.</summary>
    static bool TryParseDuration(string text, out DateTime moment)
    {
        moment = default;
        DateTime result = DateTime.Now;
        var calendar = new Dictionary<char, int>();
        double seconds = 0;
        int p = 0, parts = 0;

        while (true)
        {
            while (p < text.Length && (text[p] == ' ' || text[p] == ',')) p++;
            if (p == text.Length) break;

            int start = p;
            while (p < text.Length && char.IsAsciiDigit(text[p])) p++;
            if (p == start || !int.TryParse(text.AsSpan(start, p - start), out int number)) return false;
            while (p < text.Length && text[p] == ' ') p++;

            int unitStart = p;
            while (p < text.Length && char.IsAsciiLetter(text[p])) p++;
            switch (text[unitStart..p].ToLowerInvariant())
            {
                case "y" or "yr" or "yrs" or "year" or "years": calendar['y'] = calendar.GetValueOrDefault('y') + number; break;
                case "mo" or "mos" or "month" or "months": calendar['M'] = calendar.GetValueOrDefault('M') + number; break;
                case "w" or "wk" or "wks" or "week" or "weeks": calendar['d'] = calendar.GetValueOrDefault('d') + 7 * number; break;
                case "d" or "day" or "days": calendar['d'] = calendar.GetValueOrDefault('d') + number; break;
                case "h" or "hr" or "hrs" or "hour" or "hours": seconds += number * 3600.0; break;
                case "m" or "min" or "mins" or "minute" or "minutes": seconds += number * 60.0; break;
                case "s" or "sec" or "secs" or "second" or "seconds": seconds += number; break;
                case "ms" or "msec" or "msecs" or "millisecond" or "milliseconds": seconds += number / 1e3; break;
                case "us" or "usec" or "usecs" or "microsecond" or "microseconds": seconds += number / 1e6; break;
                case "ns" or "nsec" or "nsecs" or "nanosecond" or "nanoseconds": seconds += number / 1e9; break;
                default: return false;
            }
            parts++;
        }

        if (parts == 0) return false;
        try
        {
            result = result.AddYears(-calendar.GetValueOrDefault('y')).AddMonths(-calendar.GetValueOrDefault('M'))
                .AddDays(-calendar.GetValueOrDefault('d')).AddSeconds(-seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        moment = result;
        return true;
    }
}
