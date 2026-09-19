using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Trading.Domain.MarketData;

namespace Trading.MarketData.Quality;

public sealed record ExchangeSession(DateOnly Date, TimeOnly Open, TimeOnly Close);

public sealed record ExchangeSessionCalendar(
    string Id,
    TimeZoneInfo TimeZone,
    TimeOnly DefaultSessionOpen,
    TimeOnly DefaultSessionClose,
    IReadOnlySet<DateOnly> Holidays,
    IReadOnlyDictionary<DateOnly, ExchangeSession> SpecialSessions)
{
    public ExchangeSessionCalendar(string id, TimeZoneInfo timeZone, TimeOnly sessionOpen,
        TimeOnly sessionClose, IReadOnlySet<DateOnly> holidays)
        : this(id, timeZone, sessionOpen, sessionClose, holidays,
            new Dictionary<DateOnly, ExchangeSession>())
    {
    }

    public TimeOnly SessionOpen => DefaultSessionOpen;
    public TimeOnly SessionClose => DefaultSessionClose;
}

public sealed record ExchangeCalendarEntries(
    IReadOnlySet<DateOnly> Holidays,
    IReadOnlyDictionary<DateOnly, ExchangeSession> SpecialSessions);

public sealed record DatasetQualityIssue(string Code, int Count, string Message);
public sealed record DatasetExclusion(DateTime OpenTimeUtc, string Reason);

public sealed record DatasetQualityCertificate(
    int SchemaVersion,
    bool Passed,
    string DataSource,
    string DataVersion,
    string CalendarId,
    Guid InstrumentId,
    Timeframe Timeframe,
    DateOnly FromSession,
    DateOnly ToSessionExclusive,
    int ExpectedSessionCount,
    int ObservedSessionCount,
    int CertifiedSessionCount,
    int ExpectedCandleCount,
    int ObservedCandleCount,
    int RawCandleCount,
    int CertifiedCandleCount,
    int ExcludedCandleCount,
    string DatasetSha256,
    IReadOnlyList<DatasetExclusion> Exclusions,
    IReadOnlyList<DatasetQualityIssue> Issues);

public sealed record DatasetCertificationResult(
    DatasetQualityCertificate Certificate,
    IReadOnlyList<Candle> CertifiedCandles);

public static class DatasetQualityCertifier
{
    public static DatasetCertificationResult Certify(IReadOnlyList<Candle> candles, Guid instrumentId,
        Timeframe timeframe, DateOnly fromSession, DateOnly toSessionExclusive,
        ExchangeSessionCalendar calendar, string dataSource, string dataVersion)
    {
        ValidateInputs(candles, instrumentId, timeframe, fromSession, toSessionExclusive,
            calendar, dataSource, dataVersion);

        var expectedSessions = Sessions(fromSession, toSessionExclusive, calendar).ToArray();
        var expectedTimes = expectedSessions
            .SelectMany(session => ExpectedTimes(session, timeframe, calendar))
            .ToHashSet();
        var actualTimes = candles.Select(candle => candle.OpenTimeUtc).ToArray();
        var actualSet = actualTimes.ToHashSet();
        var certifiedCandles = candles
            .Where(candle => expectedTimes.Contains(candle.OpenTimeUtc))
            .OrderBy(candle => candle.OpenTimeUtc)
            .ToArray();
        var exclusions = candles
            .Where(candle => !expectedTimes.Contains(candle.OpenTimeUtc))
            .OrderBy(candle => candle.OpenTimeUtc)
            .Select(candle => new DatasetExclusion(candle.OpenTimeUtc,
                ExclusionReason(candle.OpenTimeUtc, timeframe, calendar)))
            .ToArray();

        var issues = new List<DatasetQualityIssue>();
        Add(issues, "duplicate-timestamp", actualTimes.Length - actualSet.Count,
            "Duplicate candle timestamps were observed.");
        Add(issues, "missing-candle", expectedTimes.Count(time => !actualSet.Contains(time)),
            "Expected exchange-session candles are missing.");
        Add(issues, "off-grid-candle",
            exclusions.Count(exclusion => exclusion.Reason != "outside-declared-session"),
            "Candles inside a declared session do not align to its timeframe grid.");

        var observedSessions = candles
            .Select(candle => Session(candle.OpenTimeUtc, calendar.TimeZone))
            .Distinct()
            .Count();
        var certifiedSessions = certifiedCandles
            .Select(candle => Session(candle.OpenTimeUtc, calendar.TimeZone))
            .Distinct()
            .Count();
        var certificate = new DatasetQualityCertificate(
            2, issues.Count == 0, dataSource.Trim(), dataVersion.Trim(), calendar.Id.Trim(),
            instrumentId, timeframe, fromSession, toSessionExclusive, expectedSessions.Length,
            observedSessions, certifiedSessions, expectedTimes.Count, candles.Count, candles.Count,
            certifiedCandles.Length, exclusions.Length,
            Fingerprint(certifiedCandles, instrumentId, timeframe, fromSession,
                toSessionExclusive, calendar.Id, dataSource, dataVersion),
            Array.AsReadOnly(exclusions), issues.AsReadOnly());

        return new(certificate, Array.AsReadOnly(certifiedCandles));
    }

    public static ExchangeCalendarEntries ParseCalendar(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var holidays = new HashSet<DateOnly>();
        var specialSessions = new Dictionary<DateOnly, ExchangeSession>();
        var lineNumber = 0;

        foreach (var raw in lines)
        {
            lineNumber++;
            var value = raw.Trim();
            if (value.Length == 0 || value.StartsWith('#')) continue;

            var fields = value.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length == 1)
            {
                if (!holidays.Add(ParseDate(fields[0], lineNumber))) InvalidCalendarLine(lineNumber);
                continue;
            }

            if (fields.Length == 2 && fields[0] == "holiday")
            {
                if (!holidays.Add(ParseDate(fields[1], lineNumber))) InvalidCalendarLine(lineNumber);
                continue;
            }

            if (fields.Length == 4 && fields[0] == "special")
            {
                var date = ParseDate(fields[1], lineNumber);
                var open = ParseTime(fields[2], lineNumber);
                var close = ParseTime(fields[3], lineNumber);
                if (open >= close || !specialSessions.TryAdd(date, new(date, open, close)))
                    InvalidCalendarLine(lineNumber);
                continue;
            }

            InvalidCalendarLine(lineNumber);
        }

        return new(holidays, specialSessions);
    }

    public static IReadOnlySet<DateOnly> ParseHolidays(IEnumerable<string> lines) =>
        ParseCalendar(lines).Holidays;

    public static bool TryGetSession(ExchangeSessionCalendar calendar, DateOnly date,
        out ExchangeSession session)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        if (calendar.SpecialSessions.TryGetValue(date, out session!)) return true;
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
            calendar.Holidays.Contains(date))
        {
            session = default!;
            return false;
        }

        session = new(date, calendar.DefaultSessionOpen, calendar.DefaultSessionClose);
        return true;
    }

    private static void ValidateInputs(IReadOnlyList<Candle> candles, Guid instrumentId,
        Timeframe timeframe, DateOnly fromSession, DateOnly toSessionExclusive,
        ExchangeSessionCalendar calendar, string dataSource, string dataVersion)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(calendar);
        if (instrumentId == Guid.Empty || !Enum.IsDefined(timeframe) || timeframe == Timeframe.Day1 ||
            fromSession >= toSessionExclusive || string.IsNullOrWhiteSpace(calendar.Id) ||
            calendar.TimeZone is null || calendar.DefaultSessionOpen >= calendar.DefaultSessionClose ||
            calendar.Holidays is null || calendar.SpecialSessions is null ||
            calendar.SpecialSessions.Any(item => item.Key != item.Value.Date ||
                item.Value.Open >= item.Value.Close) || string.IsNullOrWhiteSpace(dataSource) ||
            string.IsNullOrWhiteSpace(dataVersion))
            throw new ArgumentException("Dataset certification inputs are invalid.");
        if (candles.Any(candle => candle.InstrumentId != instrumentId || candle.Timeframe != timeframe))
            throw new ArgumentException(
                "Certification candles must match the requested instrument and timeframe.", nameof(candles));
    }

    private static IEnumerable<ExchangeSession> Sessions(DateOnly from, DateOnly toExclusive,
        ExchangeSessionCalendar calendar)
    {
        for (var date = from; date < toExclusive; date = date.AddDays(1))
            if (TryGetSession(calendar, date, out var session)) yield return session;
    }

    private static IEnumerable<DateTime> ExpectedTimes(ExchangeSession session, Timeframe timeframe,
        ExchangeSessionCalendar calendar)
    {
        var minutes = (int)timeframe;
        for (var time = session.Open; time < session.Close; time = time.AddMinutes(minutes))
        {
            var local = session.Date.ToDateTime(time, DateTimeKind.Unspecified);
            yield return TimeZoneInfo.ConvertTimeToUtc(local, calendar.TimeZone);
        }
    }

    private static string ExclusionReason(DateTime timestamp, Timeframe timeframe,
        ExchangeSessionCalendar calendar)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(timestamp, calendar.TimeZone);
        var date = DateOnly.FromDateTime(local);
        if (!TryGetSession(calendar, date, out var session)) return "outside-declared-session";
        var time = TimeOnly.FromDateTime(local);
        if (time < session.Open || time >= session.Close) return "outside-declared-session";

        var elapsed = local - date.ToDateTime(session.Open, DateTimeKind.Unspecified);
        return elapsed.Ticks % TimeSpan.FromMinutes((int)timeframe).Ticks == 0
            ? "unexpected-session-candle"
            : "off-grid-within-declared-session";
    }

    private static DateOnly Session(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));

    private static DateOnly ParseDate(string value, int lineNumber) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : throw new FormatException($"Exchange calendar line {lineNumber} is invalid or duplicated.");

    private static TimeOnly ParseTime(string value, int lineNumber) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var time)
            ? time
            : throw new FormatException($"Exchange calendar line {lineNumber} is invalid or duplicated.");

    private static void InvalidCalendarLine(int lineNumber) =>
        throw new FormatException($"Exchange calendar line {lineNumber} is invalid or duplicated.");

    private static void Add(List<DatasetQualityIssue> issues, string code, int count, string message)
    {
        if (count > 0) issues.Add(new(code, count, message));
    }

    private static string Fingerprint(IEnumerable<Candle> candles, Guid instrumentId, Timeframe timeframe,
        DateOnly from, DateOnly to, string calendarId, string source, string version)
    {
        var builder = new StringBuilder();
        builder.Append(instrumentId).Append('|').Append((int)timeframe).Append('|')
            .Append(from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|')
            .Append(to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|')
            .Append(calendarId.Trim()).Append('|').Append(source.Trim()).Append('|')
            .Append(version.Trim()).AppendLine();
        foreach (var candle in candles.OrderBy(candle => candle.OpenTimeUtc))
            builder.Append(candle.OpenTimeUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                .Append(candle.Open.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(candle.High.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(candle.Low.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(candle.Close.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(candle.Volume.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(candle.OpenInterest?.ToString(CultureInfo.InvariantCulture) ?? "null").AppendLine();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }
}
