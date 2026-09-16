using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Trading.Domain.MarketData;

namespace Trading.MarketData.Quality;

public sealed record ExchangeSessionCalendar(
    string Id,
    TimeZoneInfo TimeZone,
    TimeOnly SessionOpen,
    TimeOnly SessionClose,
    IReadOnlySet<DateOnly> Holidays);

public sealed record DatasetQualityIssue(string Code, int Count, string Message);

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
    int ExpectedCandleCount,
    int ObservedCandleCount,
    string DatasetSha256,
    IReadOnlyList<DatasetQualityIssue> Issues);

public static class DatasetQualityCertifier
{
    public static DatasetQualityCertificate Certify(IReadOnlyList<Candle> candles, Guid instrumentId,
        Timeframe timeframe, DateOnly fromSession, DateOnly toSessionExclusive,
        ExchangeSessionCalendar calendar, string dataSource, string dataVersion)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(calendar);
        if (instrumentId == Guid.Empty || !Enum.IsDefined(timeframe) || timeframe == Timeframe.Day1 ||
            fromSession >= toSessionExclusive || string.IsNullOrWhiteSpace(calendar.Id) ||
            calendar.TimeZone is null || calendar.SessionOpen >= calendar.SessionClose ||
            calendar.Holidays is null || string.IsNullOrWhiteSpace(dataSource) || string.IsNullOrWhiteSpace(dataVersion))
            throw new ArgumentException("Dataset certification inputs are invalid.");
        if (candles.Any(candle => candle.InstrumentId != instrumentId || candle.Timeframe != timeframe))
            throw new ArgumentException("Certification candles must match the requested instrument and timeframe.", nameof(candles));

        var expectedSessions = Sessions(fromSession, toSessionExclusive, calendar).ToArray();
        var expectedTimes = expectedSessions.SelectMany(session => ExpectedTimes(session, timeframe, calendar)).ToHashSet();
        var actualTimes = candles.Select(candle => candle.OpenTimeUtc).ToArray();
        var actualSet = actualTimes.ToHashSet();
        var issues = new List<DatasetQualityIssue>();
        Add(issues, "duplicate-timestamp", actualTimes.Length - actualSet.Count,
            "Duplicate candle timestamps were observed.");
        Add(issues, "missing-candle", expectedTimes.Count(time => !actualSet.Contains(time)),
            "Expected exchange-session candles are missing.");
        Add(issues, "unexpected-candle", actualSet.Count(time => !expectedTimes.Contains(time)),
            "Candles exist outside the declared exchange calendar or bar grid.");
        var observedSessions = candles.Select(candle => Session(candle.OpenTimeUtc, calendar.TimeZone)).Distinct().Count();
        return new(1, issues.Count == 0, dataSource.Trim(), dataVersion.Trim(), calendar.Id.Trim(),
            instrumentId, timeframe, fromSession, toSessionExclusive, expectedSessions.Length,
            observedSessions, expectedTimes.Count, candles.Count, Fingerprint(candles, instrumentId,
                timeframe, fromSession, toSessionExclusive, calendar.Id, dataSource, dataVersion),
            issues.AsReadOnly());
    }

    public static IReadOnlySet<DateOnly> ParseHolidays(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var holidays = new HashSet<DateOnly>();
        var lineNumber = 0;
        foreach (var raw in lines)
        {
            lineNumber++;
            var value = raw.Trim();
            if (value.Length == 0 || value.StartsWith('#')) continue;
            if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) || !holidays.Add(date))
                throw new FormatException($"Holiday calendar line {lineNumber} is invalid or duplicated.");
        }
        return holidays;
    }

    private static IEnumerable<DateOnly> Sessions(DateOnly from, DateOnly toExclusive,
        ExchangeSessionCalendar calendar)
    {
        for (var date = from; date < toExclusive; date = date.AddDays(1))
            if (date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday &&
                !calendar.Holidays.Contains(date)) yield return date;
    }

    private static IEnumerable<DateTime> ExpectedTimes(DateOnly session, Timeframe timeframe,
        ExchangeSessionCalendar calendar)
    {
        var minutes = (int)timeframe;
        for (var time = calendar.SessionOpen; time < calendar.SessionClose; time = time.AddMinutes(minutes))
        {
            var local = session.ToDateTime(time, DateTimeKind.Unspecified);
            yield return TimeZoneInfo.ConvertTimeToUtc(local, calendar.TimeZone);
        }
    }

    private static DateOnly Session(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));

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
            .Append(calendarId.Trim()).Append('|').Append(source.Trim()).Append('|').Append(version.Trim()).AppendLine();
        foreach (var candle in candles.OrderBy(candle => candle.OpenTimeUtc))
            builder.Append(candle.OpenTimeUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                .Append(candle.Open.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(candle.High.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(candle.Low.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(candle.Close.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(candle.Volume.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(candle.OpenInterest?.ToString(CultureInfo.InvariantCulture) ?? "null").AppendLine();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
