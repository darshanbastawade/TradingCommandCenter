using Trading.Domain.MarketData;
using Trading.Strategies.Regimes;

namespace Trading.StrategyValidationTests;

public sealed class MarketRegimeClassifierTests
{
    private static readonly Guid InstrumentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T09:15:00+05:30");
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone(
        "Regime India", TimeSpan.FromMinutes(330), "Regime India", "Regime India");
    private static readonly RegimeClassificationSettings Settings = new()
    {
        FastEmaPeriod = 2, SlowEmaPeriod = 3, AtrPeriod = 2, AdxPeriod = 2,
        AtrBaselinePeriod = 2, TrendingAdxThreshold = 0, GapThresholdPercent = 1,
        HighVolatilityRatio = 1.5m, LowVolatilityRatio = .5m
    };

    [Fact]
    public void Classifies_trend_volatility_and_session_gap_without_future_data()
    {
        var firstSession = new[] { Bar(Start, 100, 1), Bar(Start.AddMinutes(5), 102, 1),
            Bar(Start.AddMinutes(10), 104, 1), Bar(Start.AddMinutes(15), 106, 6) };
        var secondSession = new[] { Bar(Start.AddDays(1), 110, 1), Bar(Start.AddDays(1).AddMinutes(5), 111, 1) };
        var prefix = MarketRegimeClassifier.Classify(firstSession, India, Settings);
        var all = MarketRegimeClassifier.Classify([.. firstSession, .. secondSession], India, Settings);

        Assert.Equal(TrendRegime.Bullish, prefix[^1].Trend);
        Assert.Equal(VolatilityRegime.High, prefix[^1].Volatility);
        Assert.Equal(GapRegime.GapUp, all[firstSession.Length].Gap);
        Assert.Equal(prefix, all.Take(prefix.Count));
    }

    [Fact]
    public void Invalid_settings_are_rejected_and_warmup_is_unknown()
    {
        var result = MarketRegimeClassifier.Classify([Bar(Start, 100, 1)], India, Settings);
        Assert.Equal(TrendRegime.Unknown, result[0].Trend);
        Assert.Equal(VolatilityRegime.Unknown, result[0].Volatility);
        Assert.Equal(GapRegime.Unknown, result[0].Gap);
        Assert.Throws<ArgumentException>(() => MarketRegimeClassifier.Classify([], India,
            Settings with { HighVolatilityRatio = .4m }));
    }

    private static Candle Bar(DateTimeOffset time, decimal close, decimal halfRange) =>
        new(InstrumentId, Timeframe.Minute5, time, close, close + halfRange, close - halfRange, close, 100);
}
