using Trading.Domain.MarketData;
using Trading.Strategies.Contracts;
using Trading.Strategies.VwapEmaTrendBreakout;

namespace Trading.StrategyValidationTests;

public sealed class VwapEmaTrendBreakoutStrategyTests
{
    private static readonly Guid InstrumentId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T09:15:00+05:30");
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone(
        "Strategy Test India", TimeSpan.FromMinutes(330), "Strategy Test India", "Strategy Test India");

    [Fact]
    public void Implements_common_contract_and_exposes_stable_identity()
    {
        ITradingStrategy strategy = Strategy();
        Assert.Equal("vwap-ema-trend-breakout-v1", strategy.Id);
        Assert.Equal("VWAP + EMA Trend Breakout", strategy.Name);
    }

    [Fact]
    public void Emits_auditable_long_candidate_with_exact_3R_levels()
    {
        var signal = Assert.Single(Strategy().Evaluate(Trend(100, 101, 102, 105), India));
        Assert.Equal(TradeDirection.Long, signal.Direction);
        Assert.Equal(105m, signal.EntryPrice);
        Assert.Equal(3m, signal.RiskPerUnit);
        Assert.Equal(102m, signal.StopPrice);
        Assert.Equal(114m, signal.TargetPrice);
        Assert.Equal(3m, signal.RewardRiskMultiple);
        Assert.Equal(200, signal.Evidence.Volume);
        Assert.Equal(100m, signal.Evidence.PreviousAverageVolume);
        Assert.Equal(103m, signal.Evidence.BreakoutLevel);
        Assert.True(signal.Evidence.FastEma > signal.Evidence.SlowEma);
        Assert.True(signal.Evidence.PositiveDi > signal.Evidence.NegativeDi);
    }

    [Fact]
    public void Emits_auditable_short_candidate_with_exact_3R_levels()
    {
        var signal = Assert.Single(Strategy().Evaluate(Trend(110, 109, 108, 105), India));
        Assert.Equal(TradeDirection.Short, signal.Direction);
        Assert.Equal(105m, signal.EntryPrice);
        Assert.Equal(3m, signal.RiskPerUnit);
        Assert.Equal(108m, signal.StopPrice);
        Assert.Equal(96m, signal.TargetPrice);
        Assert.Equal(107m, signal.Evidence.BreakoutLevel);
        Assert.True(signal.Evidence.FastEma < signal.Evidence.SlowEma);
        Assert.True(signal.Evidence.NegativeDi > signal.Evidence.PositiveDi);
    }

    [Fact]
    public void Rejects_candidate_without_volume_confirmation()
    {
        var candles = Trend(100, 101, 102, 105, finalVolume: 149);
        Assert.Empty(Strategy().Evaluate(candles, India));
    }

    [Fact]
    public void Rejects_candidate_at_exclusive_entry_window_end()
    {
        var strategy = Strategy(new() { EntryWindowStart = new(9, 0), EntryWindowEnd = new(9, 30) });
        Assert.Empty(strategy.Evaluate(Trend(100, 101, 102, 105), India));
    }

    [Fact]
    public void Requires_breakout_lookback_inside_current_exchange_session()
    {
        var firstDay = Trend(100, 101, 102, 105);
        var nextDayFirstBar = Bar(Start.AddDays(1), 120, 200);
        var signals = Strategy().Evaluate([..firstDay, nextDayFirstBar], India);
        Assert.DoesNotContain(signals, signal => signal.OpenTimeUtc == nextDayFirstBar.OpenTimeUtc);
    }

    [Fact]
    public void Future_bars_do_not_change_an_existing_signal()
    {
        var candles = Trend(100, 101, 102, 105);
        var original = Assert.Single(Strategy().Evaluate(candles, India));
        var withFuture = Strategy().Evaluate([..candles, Bar(Start.AddMinutes(20), 80, 1000)], India);
        Assert.Equal(original, Assert.Single(withFuture, signal => signal.OpenTimeUtc == original.OpenTimeUtc));
    }

    [Fact]
    public void Warmup_flat_market_and_empty_input_emit_no_candidates()
    {
        var strategy = Strategy();
        Assert.Empty(strategy.Evaluate([], India));
        Assert.Empty(strategy.Evaluate(Trend(100, 100, 100, 100), India));
        Assert.Empty(strategy.Evaluate(Trend(100, 101), India));
    }

    [Theory]
    [MemberData(nameof(InvalidParameters))]
    public void Invalid_parameters_are_rejected(VwapEmaTrendBreakoutParameters parameters) =>
        Assert.ThrowsAny<ArgumentException>(() => new VwapEmaTrendBreakoutStrategy(parameters));

    public static TheoryData<VwapEmaTrendBreakoutParameters> InvalidParameters => new()
    {
        new() { FastEmaPeriod = 20, SlowEmaPeriod = 20 },
        new() { AtrPeriod = 0 },
        new() { AdxPeriod = 1 },
        new() { VolumeAveragePeriod = 0 },
        new() { BreakoutLookbackBars = 0 },
        new() { MinimumAdx = 101 },
        new() { VolumeMultiplier = 0 },
        new() { AtrStopMultiple = 0 },
        new() { RewardRiskMultiple = 0 },
        new() { EntryWindowStart = new(11, 30), EntryWindowEnd = new(9, 30) }
    };

    private static VwapEmaTrendBreakoutStrategy Strategy(VwapEmaTrendBreakoutParameters? parameters = null) =>
        new(parameters ?? new()
        {
            FastEmaPeriod = 2,
            SlowEmaPeriod = 3,
            AtrPeriod = 2,
            AdxPeriod = 2,
            VolumeAveragePeriod = 2,
            BreakoutLookbackBars = 2,
            MinimumAdx = 20,
            VolumeMultiplier = 1.5m,
            AtrStopMultiple = 1,
            RewardRiskMultiple = 3,
            EntryWindowStart = new(9, 15),
            EntryWindowEnd = new(11, 30)
        });

    private static Candle[] Trend(params decimal[] closes) => Trend(closes, 200);

    private static Candle[] Trend(decimal first, decimal second, decimal third, decimal fourth, long finalVolume) =>
        Trend([first, second, third, fourth], finalVolume);

    private static Candle[] Trend(decimal[] closes, long finalVolume) => closes.Select((close, index) =>
        Bar(Start.AddMinutes(index * 5), close, index == closes.Length - 1 ? finalVolume : 100)).ToArray();

    private static Candle Bar(DateTimeOffset time, decimal close, long volume) =>
        new(InstrumentId, Timeframe.Minute5, time, close, close + 1, close - 1, close, volume);
}
