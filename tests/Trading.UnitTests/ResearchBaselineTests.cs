using Trading.Domain.MarketData;
using Trading.MarketData.Quality;

namespace Trading.UnitTests;

public sealed class ResearchBaselineTests
{
    private static readonly Guid Id = Guid.Parse("7787840d-44f9-4b47-b8ee-6c0ac40c7a01");
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone("Baseline India", TimeSpan.FromMinutes(330), "India", "India");
    private static ResearchCalendar Calendar(DateOnly from, int days) => ResearchBaseline.Provisional(from, from.AddDays(days)) with { Verified = true, Sources = ["test fixture only"] };
    private static Candle Bar(DateOnly date, TimeOnly time, decimal price = 100) => new(Id, Timeframe.Minute1,
        new DateTimeOffset(date.ToDateTime(time), TimeSpan.FromMinutes(330)), price, price + 2, price - 1, price + 1, 0);
    private static Candle[] Bars(DateOnly date) => Enumerable.Range(0, 375).Select(i => Bar(date, new TimeOnly(9, 15).AddMinutes(i), 100 + i)).ToArray();
    private static ResearchBaselineResult Build(IReadOnlyList<Candle> bars, ResearchCalendar calendar) => ResearchBaseline.Build(bars, Id, calendar.From, calendar.ToExclusive, calendar, India);

    [Fact]
    public void Complete_zero_volume_day_aggregates_exact_prices_and_reports_extra_rows()
    {
        var date = new DateOnly(2025, 3, 24);
        var raw = Bars(date).Append(Bar(date, new(9, 7))).ToArray();
        var result = Build(raw, Calendar(date, 1));
        Assert.True(result.SelectedSliceComplete);
        Assert.Equal(376, result.ZeroVolumeRows);
        Assert.Equal(75, result.FiveMinuteRows);
        Assert.Single(result.Days[0].OutsideSessionUtc);
        var first = result.Blocks[0][0];
        Assert.Equal(100m, first.Open);
        Assert.Equal(106m, first.High);
        Assert.Equal(99m, first.Low);
        Assert.Equal(105m, first.Close);
        Assert.Equal(376, raw.Length);
        Assert.Equal(result.DatasetSha256, Build(raw.Reverse().ToArray(), Calendar(date, 1)).DatasetSha256);
    }

    [Fact]
    public void Accepted_gap_date_is_excluded_and_breaks_history()
    {
        var date = new DateOnly(2025, 3, 24);
        var raw = Bars(date).Concat(Bars(date.AddDays(1)).Skip(1)).Concat(Bars(date.AddDays(2))).ToArray();
        var result = Build(raw, Calendar(date, 3));
        Assert.True(result.SelectedSliceComplete);
        Assert.Equal("excluded-known-gap-date", result.Days[1].Status);
        Assert.Equal(new[] { 75, 75 }, result.Blocks.Select(b => b.Count));
    }

    [Fact]
    public void Entire_missing_day_blocks_verified_baseline()
    {
        var date = new DateOnly(2025, 3, 26);
        var result = Build(Bars(date), Calendar(date, 2));
        Assert.False(result.SelectedSliceComplete);
        Assert.Equal(375, result.Days[1].MissingUtc.Count);
        Assert.Equal("blocked-missing-minutes", result.Days[1].Status);
    }

    [Fact]
    public void Incomplete_day_never_produces_partial_five_minute_bars()
    {
        var date = new DateOnly(2025, 3, 26);
        var result = Build(Bars(date).Where((_, i) => i != 42).ToArray(), Calendar(date, 1));
        Assert.False(result.SelectedSliceComplete);
        Assert.Equal(0, result.FiveMinuteRows);
        Assert.Single(result.Days[0].MissingUtc);
    }

    [Fact]
    public void Split_session_break_is_not_missing_data()
    {
        var date = new DateOnly(2024, 3, 2);
        var raw = Enumerable.Range(0, 45).Select(i => Bar(date, new TimeOnly(9, 15).AddMinutes(i)))
            .Concat(Enumerable.Range(0, 60).Select(i => Bar(date, new TimeOnly(11, 30).AddMinutes(i)))).ToArray();
        var result = Build(raw, Calendar(date, 1));
        Assert.Equal(105, result.Days[0].ExpectedMinutes);
        Assert.Empty(result.Days[0].MissingUtc);
        Assert.Equal("excluded-special-session-policy", result.Days[0].Status);
    }

    [Fact]
    public void Provisional_calendar_cannot_certify_even_a_complete_day()
    {
        var date = new DateOnly(2025, 3, 26);
        Assert.False(Build(Bars(date), ResearchBaseline.Provisional(date, date.AddDays(1))).SelectedSliceComplete);
    }

    [Fact]
    public void Declared_closure_preserves_history_but_omitted_calendar_date_is_rejected()
    {
        var date = new DateOnly(2025, 3, 26);
        var calendar = Calendar(date, 3);
        calendar = calendar with { Days = [calendar.Days[0], new(date.AddDays(1), []), calendar.Days[2]] };
        Assert.Equal(150, Assert.Single(Build(Bars(date).Concat(Bars(date.AddDays(2))).ToArray(), calendar).Blocks).Count);
        Assert.Throws<ArgumentException>(() => Build([], calendar with { Days = [calendar.Days[0]] }));
    }
}
