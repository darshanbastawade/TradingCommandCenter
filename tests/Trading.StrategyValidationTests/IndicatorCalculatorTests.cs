using Trading.Domain.MarketData;
using Trading.Strategies.Indicators;

namespace Trading.StrategyValidationTests;

public sealed class IndicatorCalculatorTests
{
    private static readonly Guid InstrumentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T09:15:00+05:30");

    [Fact]
    public void Ema_uses_SMA_seed_and_preserves_warmup_timestamps()
    {
        var candles = Enumerable.Range(1, 5).Select((close, i) => Bar(Start.AddMinutes(i * 5), close)).ToArray();
        var result = IndicatorCalculator.ExponentialMovingAverage(candles, 3);
        Assert.Equal(candles.Select(candle => candle.OpenTimeUtc), result.Select(point => point.OpenTimeUtc));
        Assert.Null(result[0].Value);
        Assert.Null(result[1].Value);
        Assert.Equal(2m, result[2].Value);
        Assert.Equal(3m, result[3].Value);
        Assert.Equal(4m, result[4].Value);
    }

    [Fact]
    public void Rolling_volume_average_has_a_full_window_before_emitting()
    {
        var candles = new[] { Bar(Start, 100, 10), Bar(Start.AddMinutes(5), 100, 20), Bar(Start.AddMinutes(10), 100, 40) };
        var result = IndicatorCalculator.RollingAverageVolume(candles, 2);
        Assert.Null(result[0].Value);
        Assert.Equal(15m, result[1].Value);
        Assert.Equal(30m, result[2].Value);
    }

    [Fact]
    public void Atr_uses_true_range_and_Wilder_smoothing()
    {
        var candles = new[]
        {
            Candle(Start, 9, 10, 8, 9),
            Candle(Start.AddMinutes(5), 10, 12, 9, 11),
            Candle(Start.AddMinutes(10), 11, 13, 10, 12),
            Candle(Start.AddMinutes(15), 12, 15, 11, 14)
        };
        var result = IndicatorCalculator.AverageTrueRange(candles, 3);
        Assert.Null(result[1].Value);
        Assert.Equal(8m / 3m, result[2].Value);
        Assert.Equal(3.111111m, decimal.Round(result[3].Value!.Value, 6));
    }

    [Fact]
    public void Session_vwap_is_volume_weighted_and_resets_on_exchange_date()
    {
        var india = TimeZoneInfo.CreateCustomTimeZone("Test India", TimeSpan.FromMinutes(330), "Test India", "Test India");
        var candles = new[]
        {
            Candle(Start, 100, 101, 99, 100, 10),
            Candle(Start.AddMinutes(5), 110, 111, 109, 110, 30),
            Candle(Start.AddDays(1), 200, 201, 199, 200, 20)
        };
        var result = IndicatorCalculator.SessionVwap(candles, india);
        Assert.Equal(100m, result[0].Value);
        Assert.Equal(107.5m, result[1].Value);
        Assert.Equal(200m, result[2].Value);
    }

    [Fact]
    public void Session_vwap_is_null_until_session_has_positive_volume()
    {
        var zone = TimeZoneInfo.Utc;
        var candles = new[] { Bar(Start, 100, 0), Bar(Start.AddMinutes(5), 110, 0), Bar(Start.AddMinutes(10), 120, 10) };
        var result = IndicatorCalculator.SessionVwap(candles, zone);
        Assert.Null(result[0].Value);
        Assert.Null(result[1].Value);
        Assert.Equal(120m, result[2].Value);
    }

    [Fact]
    public void Adx_uses_Wilder_warmup_and_detects_one_way_trend()
    {
        var candles = Enumerable.Range(0, 8).Select(i =>
            Candle(Start.AddMinutes(i * 5), 100 + i, 101 + i, 99 + i, 100 + i)).ToArray();
        var result = IndicatorCalculator.AverageDirectionalIndex(candles, 3);
        Assert.All(result.Take(3), point => Assert.Null(point.PositiveDi));
        Assert.Equal(50m, result[3].PositiveDi);
        Assert.Equal(0m, result[3].NegativeDi);
        Assert.All(result.Take(5), point => Assert.Null(point.Adx));
        Assert.All(result.Skip(5), point => Assert.Equal(100m, point.Adx));
    }

    [Fact]
    public void Adx_is_zero_for_flat_prices_without_dividing_by_zero()
    {
        var candles = Enumerable.Range(0, 6).Select(i => Bar(Start.AddMinutes(i * 5), 100)).ToArray();
        var result = IndicatorCalculator.AverageDirectionalIndex(candles, 2);
        Assert.Equal(0m, result[2].PositiveDi);
        Assert.Equal(0m, result[2].NegativeDi);
        Assert.Equal(0m, result[3].Adx);
    }

    [Fact]
    public void Engine_aligns_all_series_and_leaves_warmups_null()
    {
        var candles = Enumerable.Range(0, 6).Select(i => Bar(Start.AddMinutes(i * 5), 100 + i, 100 + i)).ToArray();
        var result = IndicatorEngine.Calculate(candles, TimeZoneInfo.Utc, new(2, 3, 2, 2, 2));
        Assert.Equal(candles.Length, result.Count);
        Assert.Null(result[0].FastEma);
        Assert.NotNull(result[1].FastEma);
        Assert.Null(result[1].SlowEma);
        Assert.NotNull(result[2].SlowEma);
        Assert.Null(result[2].Adx);
        Assert.NotNull(result[3].Adx);
        Assert.All(result, snapshot => Assert.Equal(DateTimeKind.Utc, snapshot.OpenTimeUtc.Kind));
    }

    [Fact]
    public void Empty_input_returns_empty_aligned_series()
    {
        Assert.Empty(IndicatorCalculator.ExponentialMovingAverage([], 20));
        Assert.Empty(IndicatorCalculator.SessionVwap([], TimeZoneInfo.Utc));
        Assert.Empty(IndicatorEngine.Calculate([], TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_period_is_rejected(int period) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => IndicatorCalculator.ExponentialMovingAverage([Bar(Start, 100)], period));

    [Fact]
    public void Engine_rejects_invalid_parameter_relationships() =>
        Assert.Throws<ArgumentException>(() => IndicatorEngine.Calculate([Bar(Start, 100)], TimeZoneInfo.Utc, new(20, 20)));

    [Fact]
    public void Mixed_instrument_timeframe_or_order_is_rejected()
    {
        var first = Bar(Start, 100);
        var otherInstrument = Bar(Start.AddMinutes(5), 101, instrumentId: Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => IndicatorCalculator.ExponentialMovingAverage([first, otherInstrument], 2));
        var otherTimeframe = Candle(Start.AddMinutes(5), 101, 102, 100, 101, timeframe: Timeframe.Minute15);
        Assert.Throws<ArgumentException>(() => IndicatorCalculator.ExponentialMovingAverage([first, otherTimeframe], 2));
        Assert.Throws<ArgumentException>(() => IndicatorCalculator.ExponentialMovingAverage([Bar(Start.AddMinutes(5), 101), first], 2));
    }

    [Fact]
    public void A_time_gap_is_preserved_and_calculated_as_the_next_observed_bar()
    {
        var candles = new[] { Bar(Start, 100), Bar(Start.AddMinutes(15), 110) };
        var result = IndicatorCalculator.ExponentialMovingAverage(candles, 2);
        Assert.Equal(Start.AddMinutes(15).UtcDateTime, result[1].OpenTimeUtc);
        Assert.Equal(105m, result[1].Value);
    }

    private static Candle Bar(DateTimeOffset time, decimal close, long volume = 10, Guid? instrumentId = null) =>
        Candle(time, close, close, close, close, volume, instrumentId: instrumentId);

    private static Candle Candle(DateTimeOffset time, decimal open, decimal high, decimal low, decimal close,
        long volume = 10, Timeframe timeframe = Timeframe.Minute5, Guid? instrumentId = null) =>
        new(instrumentId ?? InstrumentId, timeframe, time, open, high, low, close, volume);
}
