using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Trading.Domain.MarketData;

namespace Trading.MarketData.Quality;

public sealed record ResearchWindow(TimeOnly Open, TimeOnly Close);
public sealed record ResearchCalendarDay(DateOnly Date, IReadOnlyList<ResearchWindow> Windows);

/// <summary>Every calendar date must be explicit. Empty windows mean a declared closure.</summary>
public sealed record ResearchCalendar(DateOnly From, DateOnly ToExclusive, bool Verified,
    IReadOnlyList<string> Sources, IReadOnlyList<ResearchCalendarDay> Days);

public sealed record BaselineDay(DateOnly Date, int RawRows, int ExpectedMinutes, int PresentExpectedMinutes,
    IReadOnlyList<DateTime> MissingUtc, IReadOnlyList<DateTime> OutsideSessionUtc,
    int DuplicateRows, int MisalignedRows, string Status);

public sealed record ResearchBaselineResult(bool CalendarVerified, bool SelectedSliceComplete,
    int RawRows, int ZeroVolumeRows, int FiveMinuteRows, string PolicyId, string DatasetSha256,
    IReadOnlyList<BaselineDay> Days, IReadOnlyList<IReadOnlyList<Candle>> Blocks);

public static class ResearchBaseline
{
    public const string PolicyId = "nifty-price-baseline-v1";
    public static readonly IReadOnlySet<DateOnly> KnownGapDates = new HashSet<DateOnly>
    {
        new(2022, 3, 7), new(2024, 12, 12), new(2025, 3, 25), new(2025, 4, 4), new(2025, 4, 23)
    };

    // These are source-verified special windows, not a complete holiday calendar.
    // The provisional calendar is NEVER sufficient to certify missing dates or enable research.
    public static ResearchCalendar Provisional(DateOnly from, DateOnly toExclusive)
    {
        var specials = new Dictionary<DateOnly, ResearchWindow[]>
        {
            [new(2022, 10, 24)] = [new(new(18, 15), new(19, 15))],
            [new(2023, 11, 12)] = [new(new(18, 15), new(19, 15))],
            [new(2024, 3, 2)] = [new(new(9, 15), new(10, 0)), new(new(11, 30), new(12, 30))],
            [new(2024, 5, 18)] = [new(new(9, 15), new(10, 0)), new(new(11, 30), new(12, 30))],
            [new(2024, 11, 1)] = [new(new(18, 0), new(19, 0))],
            [new(2025, 10, 21)] = [new(new(13, 45), new(14, 45))]
        };
        var days = new List<ResearchCalendarDay>();
        for (var day = from; day < toExclusive; day = day.AddDays(1))
        {
            IReadOnlyList<ResearchWindow> windows = specials.TryGetValue(day, out var special) ? special
                : day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? []
                : [new(new(9, 15), new(15, 30))];
            days.Add(new(day, windows));
        }
        return new(from, toExclusive, false, [], days);
    }

    public static ResearchBaselineResult Build(IReadOnlyList<Candle> raw, Guid instrumentId,
        DateOnly from, DateOnly toExclusive, ResearchCalendar calendar, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(zone);
        if (calendar.Sources is null || calendar.Days is null || calendar.Days.Any(d => d is null || d.Windows is null || d.Windows.Any(w => w is null)))
            throw new ArgumentException("Calendar must declare sources, days and windows arrays.");
        if (instrumentId == Guid.Empty || from >= toExclusive || calendar.From > from || calendar.ToExclusive < toExclusive)
            throw new ArgumentException("Instrument, date range or calendar coverage is invalid.");
        if (calendar.Verified && (calendar.Sources.Count == 0 || calendar.Sources.Any(string.IsNullOrWhiteSpace)))
            throw new ArgumentException("Verified calendars require source references and operator verification.");
        if (calendar.Days.Select(x => x.Date).Distinct().Count() != calendar.Days.Count)
            throw new ArgumentException("Calendar contains duplicate dates.");
        var schedule = calendar.Days.ToDictionary(x => x.Date);
        for (var day = calendar.From; day < calendar.ToExclusive; day = day.AddDays(1))
        {
            if (!schedule.TryGetValue(day, out var entry)) throw new ArgumentException($"Calendar omits {day:yyyy-MM-dd}. Declare closures explicitly.");
            TimeOnly? previousClose = null;
            foreach (var window in entry.Windows)
            {
                if (window.Open >= window.Close || window.Open.Ticks % TimeSpan.TicksPerMinute != 0 ||
                    window.Close.Ticks % TimeSpan.TicksPerMinute != 0 || (previousClose.HasValue && previousClose.Value > window.Open))
                    throw new ArgumentException($"Invalid or overlapping windows on {day:yyyy-MM-dd}.");
                previousClose = window.Close;
            }
        }
        if (calendar.Days.Any(x => x.Date < calendar.From || x.Date >= calendar.ToExclusive))
            throw new ArgumentException("Calendar contains dates outside declared coverage.");
        DateOnly LocalDate(Candle c) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(c.OpenTimeUtc, zone));
        if (raw.Any(c => c.InstrumentId != instrumentId || c.Timeframe != Timeframe.Minute1 || LocalDate(c) < from || LocalDate(c) >= toExclusive))
            throw new ArgumentException("Input must contain only the selected instrument, range and 1-minute bars.");
        var grouped = raw.GroupBy(LocalDate).ToDictionary(g => g.Key, g => g.OrderBy(c => c.OpenTimeUtc).ToArray());
        var days = new List<BaselineDay>();
        var blocks = new List<IReadOnlyList<Candle>>();
        var current = new List<Candle>();
        void BreakBlock() { if (current.Count > 0) { blocks.Add(current.ToArray()); current.Clear(); } }
        DateTime Utc(DateOnly date, TimeOnly time) => TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), zone);

        for (var date = from; date < toExclusive; date = date.AddDays(1))
        {
            var windows = schedule[date].Windows;
            var rows = grouped.GetValueOrDefault(date) ?? [];
            var expected = new List<DateTime>();
            foreach (var window in windows)
                for (var utc = Utc(date, window.Open); utc < Utc(date, window.Close); utc = utc.AddMinutes(1)) expected.Add(utc);
            var expectedSet = expected.ToHashSet();
            var actual = rows.Select(c => c.OpenTimeUtc).ToHashSet();
            var missing = expected.Where(t => !actual.Contains(t)).ToArray();
            var outside = rows.Where(c => !expectedSet.Contains(c.OpenTimeUtc)).Select(c => c.OpenTimeUtc).ToArray();
            var duplicates = rows.Length - actual.Count;
            var misaligned = rows.Count(c => c.OpenTimeUtc.Ticks % TimeSpan.TicksPerMinute != 0);
            var regular = windows.Count == 1 && windows[0].Open == new TimeOnly(9, 15) && windows[0].Close == new TimeOnly(15, 30);
            var status = KnownGapDates.Contains(date) ? "excluded-known-gap-date"
                : duplicates > 0 || misaligned > 0 ? "blocked-invalid-timestamps"
                : missing.Length > 0 ? (calendar.Verified ? "blocked-missing-minutes" : "unverified-gap-or-holiday")
                : expected.Count == 0 ? (rows.Length == 0 ? "closed" : "blocked-unexpected-date")
                : !regular ? "excluded-special-session-policy"
                : outside.Length > 0 ? "complete-with-outside-session-exclusions" : "complete";
            days.Add(new(date, rows.Length, expected.Count, expected.Count - missing.Length,
                missing, outside, duplicates, misaligned, status));
            if (status == "closed") continue; // Legitimate holidays/weekends retain indicator history.
            if (!status.StartsWith("complete", StringComparison.Ordinal)) { BreakBlock(); continue; }
            var byTime = rows.Where(c => expectedSet.Contains(c.OpenTimeUtc)).ToDictionary(c => c.OpenTimeUtc);
            for (var i = 0; i < expected.Count; i += 5)
            {
                var source = expected.Skip(i).Take(5).Select(t => byTime[t]).ToArray();
                if (source.Length != 5) throw new InvalidOperationException("Incomplete aggregation bucket.");
                current.Add(new(instrumentId, Timeframe.Minute5, new DateTimeOffset(source[0].OpenTimeUtc, TimeSpan.Zero),
                    source[0].Open, source.Max(c => c.High), source.Min(c => c.Low), source[^1].Close,
                    source.Sum(c => c.Volume), source[^1].OpenInterest));
            }
        }
        BreakBlock();
        // Explicit accepted exclusions may coexist with a complete selected slice; unexpected gaps block it.
        var complete = calendar.Verified && blocks.Count > 0 && days.All(d =>
            d.Status == "closed" || d.Status.StartsWith("complete", StringComparison.Ordinal) ||
            d.Status is "excluded-known-gap-date" or "excluded-special-session-policy");
        var fingerprint = new StringBuilder(PolicyId).Append('|').Append(instrumentId).Append('|')
            .Append(from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|')
            .Append(toExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|').Append(calendar.Verified);
        foreach (var source in calendar.Sources.Order(StringComparer.Ordinal)) fingerprint.Append('|').Append(source);
        foreach (var day in days)
        {
            fingerprint.Append('|').Append(day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|').Append(day.Status);
            foreach (var window in schedule[day.Date].Windows)
                fingerprint.Append('|').Append(window.Open.ToString("HH:mm", CultureInfo.InvariantCulture)).Append('-').Append(window.Close.ToString("HH:mm", CultureInfo.InvariantCulture));
        }
        foreach (var block in blocks)
        {
            fingerprint.Append("|reset|");
            foreach (var c in block)
                fingerprint.Append(CultureInfo.InvariantCulture, $"{c.OpenTimeUtc:O}|{c.Open}|{c.High}|{c.Low}|{c.Close}|{c.Volume}|{c.OpenInterest};");
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToString()))).ToLowerInvariant();
        return new(calendar.Verified, complete, raw.Count, raw.Count(c => c.Volume == 0), blocks.Sum(b => b.Count),
            PolicyId, hash, days, blocks);
    }
}
