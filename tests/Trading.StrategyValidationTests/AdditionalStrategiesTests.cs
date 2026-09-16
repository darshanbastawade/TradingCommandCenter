using Trading.Domain.MarketData;
using Trading.Strategies.AdxTrendContinuation;
using Trading.Strategies.Contracts;
using Trading.Strategies.EmaPullbackContinuation;
using Trading.Strategies.OpeningRangeBreakout;
using Trading.Strategies.VwapReclaimRejection;
using Trading.Strategies;

namespace Trading.StrategyValidationTests;

public sealed class AdditionalStrategiesTests
{
    private static readonly Guid InstrumentId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T09:15:00+05:30");
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone(
        "Additional Strategy India", TimeSpan.FromMinutes(330), "Additional Strategy India", "Additional Strategy India");

    [Fact]
    public void Strategies_expose_stable_distinct_contract_identities()
    {
        ITradingStrategy[] strategies = [Orb(), Pullback(), Reclaim(), Adx()];
        Assert.Equal(4, strategies.Select(strategy => strategy.Id).Distinct().Count());
        Assert.Equal(new[] { "opening-range-breakout-v1", "ema-pullback-continuation-v1",
            "vwap-reclaim-rejection-v1", "adx-trend-continuation-v1" }, strategies.Select(strategy => strategy.Id));
    }

    [Fact]
    public void Default_catalog_contains_all_five_research_hypotheses()
    {
        var strategies = StrategyCatalog.CreateDefaults();
        Assert.Equal(5, strategies.Count);
        Assert.Equal(5, strategies.Select(strategy => strategy.Id).Distinct().Count());
    }

    [Fact]
    public void Opening_range_breakout_emits_auditable_three_r_candidate()
    {
        var signal = Assert.Single(Orb().Evaluate(Bars(100, 101, 102, 105), India));
        Assert.Equal(TradeDirection.Long, signal.Direction);
        Assert.Equal(102m, signal.Evidence.BreakoutLevel);
        Assert.Equal(3m, signal.RewardRiskMultiple);
    }

    [Fact]
    public void Ema_pullback_requires_touch_then_trend_resumption()
    {
        var signals = Pullback().Evaluate(Bars(100, 102, 104, 103, 106), India);
        Assert.Contains(signals, signal => signal.OpenTimeUtc == Bars(100, 102, 104, 103, 106)[4].OpenTimeUtc &&
            signal.Direction == TradeDirection.Long);
    }

    [Fact]
    public void Vwap_reclaim_requires_cross_back_through_session_vwap()
    {
        var signals = Reclaim().Evaluate(Bars(100, 104, 102, 106), India);
        Assert.Contains(signals, signal => signal.OpenTimeUtc == Bars(100, 104, 102, 106)[3].OpenTimeUtc &&
            signal.Direction == TradeDirection.Long);
    }

    [Fact]
    public void Adx_continuation_requires_strengthening_directional_trend()
    {
        var candles = Bars(100, 102, 101, 103, 108);
        var signals = Adx().Evaluate(candles, India);
        Assert.Contains(signals, signal => signal.OpenTimeUtc == candles[^1].OpenTimeUtc &&
            signal.Direction == TradeDirection.Long);
    }

    [Fact]
    public void Future_candles_cannot_change_existing_candidates()
    {
        var candles = Bars(100, 101, 102, 105);
        var original = Orb().Evaluate(candles, India).ToArray();
        var extended = Orb().Evaluate([.. candles, Bar(4, 80)], India);
        Assert.All(original, expected => Assert.Contains(expected, extended));
    }

    [Fact]
    public void Invalid_parameters_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new OpeningRangeBreakoutStrategy(new() { OpeningRangeBars = 0 }));
        Assert.Throws<ArgumentException>(() => new EmaPullbackContinuationStrategy(new() { MinimumAdx = 101 }));
        Assert.Throws<ArgumentException>(() => new VwapReclaimRejectionStrategy(new() { VolumeMultiplier = 0 }));
        Assert.Throws<ArgumentException>(() => new AdxTrendContinuationStrategy(new() { AdxPeriod = 1 }));
    }

    private static OpeningRangeBreakoutStrategy Orb() => new(new()
    {
        FastEmaPeriod = 2, SlowEmaPeriod = 3, AtrPeriod = 2, AdxPeriod = 2,
        VolumeAveragePeriod = 2, OpeningRangeBars = 2, VolumeMultiplier = .5m,
        EntryWindowStart = new(9, 15), EntryWindowEnd = new(15, 0)
    });

    private static EmaPullbackContinuationStrategy Pullback() => new(new()
    {
        FastEmaPeriod = 2, SlowEmaPeriod = 3, AtrPeriod = 2, AdxPeriod = 2,
        VolumeAveragePeriod = 2, MinimumAdx = 0, VolumeMultiplier = .5m,
        EntryWindowStart = new(9, 15), EntryWindowEnd = new(15, 0)
    });

    private static VwapReclaimRejectionStrategy Reclaim() => new(new()
    {
        FastEmaPeriod = 2, SlowEmaPeriod = 3, AtrPeriod = 2, AdxPeriod = 2,
        VolumeAveragePeriod = 2, MinimumAdx = 0, VolumeMultiplier = .5m,
        EntryWindowStart = new(9, 15), EntryWindowEnd = new(15, 0)
    });

    private static AdxTrendContinuationStrategy Adx() => new(new()
    {
        FastEmaPeriod = 2, SlowEmaPeriod = 3, AtrPeriod = 2, AdxPeriod = 2,
        VolumeAveragePeriod = 2, MinimumAdx = 0,
        EntryWindowStart = new(9, 15), EntryWindowEnd = new(15, 0)
    });

    private static Candle[] Bars(params decimal[] closes) => closes.Select((close, index) => Bar(index, close)).ToArray();
    private static Candle Bar(int index, decimal close) => new(InstrumentId, Timeframe.Minute5,
        Start.AddMinutes(index * 5), close, close + 1, close - 1, close, 100);
}
