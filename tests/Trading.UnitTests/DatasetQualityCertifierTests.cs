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
            new HashSet<DateOnly>());
        var candles = Bars(Monday, 3);
        var first = DatasetQualityCertifier.Certify(candles, InstrumentId, Timeframe.Minute5,
            Monday, Monday.AddDays(1), calendar, "fixture", "v1");
        var second = DatasetQualityCertifier.Certify(candles, InstrumentId, Timeframe.Minute5,
            Monday, Monday.AddDays(1), calendar, "fixture", "v1");

        Assert.True(first.Passed);
        Assert.Equal(3, first.ExpectedCandleCount);
        Assert.Equal(first.DatasetSha256, second.DatasetSha256);
        Assert.Equal(64, first.DatasetSha256.Length);
    }

    [Fact]
    public void Missing_and_off_grid_bars_fail_certification()
    {
        var calendar = new ExchangeSessionCalendar("nse-test", India, new(9, 15), new(9, 30),
            new HashSet<DateOnly>());
        var candles = new[] { Bar(Monday, new(9, 15)), Bar(Monday, new(9, 27)) };
        var result = DatasetQualityCertifier.Certify(candles, InstrumentId, Timeframe.Minute5,
            Monday, Monday.AddDays(1), calendar, "fixture", "v1");

        Assert.False(result.Passed);
        Assert.Equal(2, result.Issues.Single(issue => issue.Code == "missing-candle").Count);
        Assert.Equal(1, result.Issues.Single(issue => issue.Code == "unexpected-candle").Count);
    }

    [Fact]
    public void Holiday_parser_rejects_duplicates_and_accepts_comments()
    {
        var holidays = DatasetQualityCertifier.ParseHolidays(["# NSE", "2026-10-02", ""]);
        Assert.Contains(new DateOnly(2026, 10, 2), holidays);
        Assert.Throws<FormatException>(() => DatasetQualityCertifier.ParseHolidays(
            ["2026-10-02", "2026-10-02"]));
    }

    private static Candle[] Bars(DateOnly date, int count) => Enumerable.Range(0, count)
        .Select(index => Bar(date, new TimeOnly(9, 15).AddMinutes(index * 5))).ToArray();

    private static Candle Bar(DateOnly date, TimeOnly time)
    {
        var utc = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), India);
        return new(InstrumentId, Timeframe.Minute5, new DateTimeOffset(utc, TimeSpan.Zero), 100, 101, 99, 100, 100);
    }
}
