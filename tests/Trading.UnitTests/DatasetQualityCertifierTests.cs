using Trading.Domain.MarketData;
using Trading.MarketData.Quality;

namespace Trading.UnitTests;

public sealed class DatasetQualityCertifierTests
{
    private static readonly Guid InstrumentId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone(
        "Quality India", TimeSpan.FromMinutes(330), "Quality India", "Quality India");
    private static readonly DateOnly Monday = new(2026, 9, 7);

    [Fact]
    public void Complete_exchange_grid_passes_with_stable_fingerprint()
    {
        var calendar = new ExchangeSessionCalendar("nse-test", India, new(9, 15), new(9, 30),
            new HashSet<DateOnly>(), new Dictionary<DateOnly, ExchangeSession>());
        var candles = Bars(Monday, 3);
        var first = DatasetQualityCertifier.Certify(candles, InstrumentId, Timeframe.Minute5,
            Monday, Monday.AddDays(1), calendar, "fixture", "v1");
        var second = DatasetQualityCertifier.Certify(candles, InstrumentId, Timeframe.Minute5,
            Monday, Monday.AddDays(1), calendar, "fixture", "v1");

        Assert.True(first.Certificate.Passed);
        Assert.Equal(3, first.Certificate.ExpectedCandleCount);
        Assert.Equal(first.Certificate.DatasetSha256, second.Certificate.DatasetSha256);
        Assert.Equal(64, first.Certificate.DatasetSha256.Length);
    }

    [Fact]
    public void Missing_and_off_grid_bars_fail_certification()
    {
        var calendar = new ExchangeSessionCalendar("nse-test", India, new(9, 15), new(9, 30),
            new HashSet<DateOnly>(), new Dictionary<DateOnly, ExchangeSession>());
        var candles = new[] { Bar(Monday, new(9, 15)), Bar(Monday, new(9, 27)) };
        var result = DatasetQualityCertifier.Certify(candles, InstrumentId, Timeframe.Minute5,
            Monday, Monday.AddDays(1), calendar, "fixture", "v1");

        Assert.False(result.Certificate.Passed);
        Assert.Equal(2, result.Certificate.Issues.Single(issue => issue.Code == "missing-candle").Count);
        Assert.Equal(1, result.Certificate.Issues.Single(issue => issue.Code == "off-grid-candle").Count);
    }

    [Fact]
    public void Holiday_parser_rejects_duplicates_and_accepts_comments()
    {
        var holidays = DatasetQualityCertifier.ParseHolidays(["# NSE", "2026-10-02", ""]);
        Assert.Contains(new DateOnly(2026, 10, 2), holidays);
        Assert.Throws<FormatException>(() => DatasetQualityCertifier.ParseHolidays(
            ["2026-10-02", "2026-10-02"]));
    }

    [Fact]
    public void Special_Saturday_overrides_weekend_and_Muhurat_uses_declared_grid()
    {
        var budgetSaturday = new DateOnly(2025, 2, 1);
        var muhurat = new DateOnly(2025, 10, 21);
        var calendar = Calendar(
            new Dictionary<DateOnly, ExchangeSession>
            {
                [budgetSaturday] = new(budgetSaturday, new(9, 15), new(15, 30)),
                [muhurat] = new(muhurat, new(13, 45), new(14, 45))
            });

        Assert.True(DatasetQualityCertifier.TryGetSession(calendar, budgetSaturday, out _));
        Assert.False(DatasetQualityCertifier.TryGetSession(calendar,
            budgetSaturday.AddDays(1), out _));

        var candles = Enumerable.Range(0, 12)
            .Select(index => Bar(muhurat, new TimeOnly(13, 45).AddMinutes(index * 5)))
            .ToArray();
        var result = DatasetQualityCertifier.Certify(candles, InstrumentId, Timeframe.Minute5,
            muhurat, muhurat.AddDays(1), calendar, "fixture", "v1");

        Assert.True(result.Certificate.Passed);
        Assert.Equal(12, result.Certificate.ExpectedCandleCount);
        Assert.Equal(12, result.Certificate.CertifiedCandleCount);
    }

    [Fact]
    public void Outside_session_bars_are_audited_but_do_not_fail_or_enter_research_slice()
    {
        var calendar = new ExchangeSessionCalendar("nse-test", India, new(9, 15), new(15, 30),
            new HashSet<DateOnly>(), new Dictionary<DateOnly, ExchangeSession>());
        var raw = Bars(Monday, 75)
            .Concat([Bar(Monday, new(15, 35)), Bar(Monday, new(15, 55))])
            .ToArray();

        var result = DatasetQualityCertifier.Certify(raw, InstrumentId, Timeframe.Minute5,
            Monday, Monday.AddDays(1), calendar, "fixture", "v1");

        Assert.True(result.Certificate.Passed);
        Assert.Equal(77, result.Certificate.RawCandleCount);
        Assert.Equal(75, result.Certificate.CertifiedCandleCount);
        Assert.Equal(2, result.Certificate.ExcludedCandleCount);
        Assert.All(result.Certificate.Exclusions,
            exclusion => Assert.Equal("outside-declared-session", exclusion.Reason));
        Assert.Equal(75, result.CertifiedCandles.Count);
    }

    [Fact]
    public void Holiday_has_no_expected_candles()
    {
        var calendar = new ExchangeSessionCalendar("nse-test", India, new(9, 15), new(15, 30),
            new HashSet<DateOnly> { Monday }, new Dictionary<DateOnly, ExchangeSession>());

        var result = DatasetQualityCertifier.Certify([], InstrumentId, Timeframe.Minute5,
            Monday, Monday.AddDays(1), calendar, "fixture", "v1");

        Assert.True(result.Certificate.Passed);
        Assert.Equal(0, result.Certificate.ExpectedSessionCount);
        Assert.Equal(0, result.Certificate.ExpectedCandleCount);
    }

    [Fact]
    public void Candle_on_undeclared_weekend_fails_certification()
    {
        var saturday = Monday.AddDays(5);
        var result = DatasetQualityCertifier.Certify([Bar(saturday, new(11, 0))],
            InstrumentId, Timeframe.Minute5, saturday, saturday.AddDays(1), Calendar(),
            "fixture", "v1");

        Assert.False(result.Certificate.Passed);
        Assert.Equal(1, result.Certificate.ExcludedCandleCount);
        Assert.Empty(result.CertifiedCandles);
        Assert.Equal("unexpected-session-date", Assert.Single(result.Certificate.Exclusions).Reason);
        Assert.Equal(1, Assert.Single(result.Certificate.Issues,
            issue => issue.Code == "unexpected-session-date").Count);
    }

    [Fact]
    public void Candle_on_declared_holiday_fails_certification()
    {
        var calendar = new ExchangeSessionCalendar("nse-test", India, new(9, 15), new(15, 30),
            new HashSet<DateOnly> { Monday }, new Dictionary<DateOnly, ExchangeSession>());
        var result = DatasetQualityCertifier.Certify([Bar(Monday, new(9, 15))],
            InstrumentId, Timeframe.Minute5, Monday, Monday.AddDays(1), calendar,
            "fixture", "v1");

        Assert.False(result.Certificate.Passed);
        Assert.Equal("unexpected-session-date", Assert.Single(result.Certificate.Exclusions).Reason);
        Assert.Equal(1, Assert.Single(result.Certificate.Issues,
            issue => issue.Code == "unexpected-session-date").Count);
    }

    [Fact]
    public void Calendar_fingerprint_binds_normal_hours_holidays_and_special_sessions()
    {
        var special = new DateOnly(2025, 10, 21);
        var holidays = new HashSet<DateOnly> { new(2025, 3, 14), new(2025, 2, 26) };
        var sessions = new Dictionary<DateOnly, ExchangeSession>
        {
            [special] = new(special, new(13, 45), new(14, 45))
        };
        var firstCalendar = new ExchangeSessionCalendar("same-id", India, new(9, 15),
            new(15, 30), holidays, sessions);
        var reorderedCalendar = new ExchangeSessionCalendar("same-id", India, new(9, 15),
            new(15, 30), new HashSet<DateOnly> { new(2025, 2, 26), new(2025, 3, 14) },
            new Dictionary<DateOnly, ExchangeSession>(sessions));
        var changedHours = firstCalendar with
        {
            SpecialSessions = new Dictionary<DateOnly, ExchangeSession>
            {
                [special] = new(special, new(13, 30), new(14, 45))
            }
        };
        var changedHoliday = firstCalendar with
        {
            Holidays = new HashSet<DateOnly> { new(2025, 2, 26) }
        };
        var changedNormalHours = firstCalendar with { DefaultSessionOpen = new(9, 10) };

        DatasetQualityCertificate Certify(ExchangeSessionCalendar calendar) =>
            DatasetQualityCertifier.Certify([], InstrumentId, Timeframe.Minute5,
                Monday, Monday.AddDays(1), calendar, "fixture", "v1").Certificate;

        var first = Certify(firstCalendar);
        var reordered = Certify(reorderedCalendar);
        Assert.Equal(first.CalendarSha256, reordered.CalendarSha256);
        Assert.Equal(first.DatasetSha256, reordered.DatasetSha256);
        foreach (var changed in new[] { changedHours, changedHoliday, changedNormalHours })
        {
            var certificate = Certify(changed);
            Assert.NotEqual(first.CalendarSha256, certificate.CalendarSha256);
            Assert.NotEqual(first.DatasetSha256, certificate.DatasetSha256);
        }
    }

    [Fact]
    public void Missing_Muhurat_bar_and_duplicate_timestamp_fail()
    {
        var muhurat = new DateOnly(2025, 10, 21);
        var calendar = Calendar(new Dictionary<DateOnly, ExchangeSession>
        {
            [muhurat] = new(muhurat, new(13, 45), new(14, 45))
        });
        var candles = Enumerable.Range(0, 11)
            .Select(index => Bar(muhurat, new TimeOnly(13, 45).AddMinutes(index * 5)))
            .Append(Bar(muhurat, new(13, 45)))
            .ToArray();

        var result = DatasetQualityCertifier.Certify(candles, InstrumentId, Timeframe.Minute5,
            muhurat, muhurat.AddDays(1), calendar, "fixture", "v1");

        Assert.False(result.Certificate.Passed);
        Assert.Contains(result.Certificate.Issues, issue => issue.Code == "missing-candle");
        Assert.Contains(result.Certificate.Issues, issue => issue.Code == "duplicate-timestamp");
    }

    [Fact]
    public void Calendar_parser_supports_typed_entries_and_legacy_holidays()
    {
        var result = DatasetQualityCertifier.ParseCalendar(
        [
            "# NSE 2025", "holiday,2025-02-26", "2025-03-14",
            "special,2025-02-01,09:15,15:30", "special,2025-10-21,13:45,14:45"
        ]);

        Assert.Equal(2, result.Holidays.Count);
        Assert.Equal(new TimeOnly(13, 45), result.SpecialSessions[new(2025, 10, 21)].Open);
        Assert.Throws<FormatException>(() => DatasetQualityCertifier.ParseCalendar(
            ["special,2025-02-01,15:30,09:15"]));
        Assert.Throws<FormatException>(() => DatasetQualityCertifier.ParseCalendar(
            ["holiday,2025-10-21", "special,2025-10-21,13:45,14:45"]));
        Assert.Throws<FormatException>(() => DatasetQualityCertifier.ParseCalendar(
            ["special,2025-10-21,13:45,14:45", "holiday,2025-10-21"]));
    }

    private static ExchangeSessionCalendar Calendar(
        IReadOnlyDictionary<DateOnly, ExchangeSession>? specialSessions = null) =>
        new("nse-test", India, new(9, 15), new(9, 30), new HashSet<DateOnly>(),
            specialSessions ?? new Dictionary<DateOnly, ExchangeSession>());

    private static Candle[] Bars(DateOnly date, int count) => Enumerable.Range(0, count)
        .Select(index => Bar(date, new TimeOnly(9, 15).AddMinutes(index * 5))).ToArray();

    private static Candle Bar(DateOnly date, TimeOnly time)
    {
        var utc = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), India);
        return new(InstrumentId, Timeframe.Minute5, new DateTimeOffset(utc, TimeSpan.Zero), 100, 101, 99, 100, 100);
    }
}
