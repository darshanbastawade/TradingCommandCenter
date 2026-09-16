using Trading.Domain.MarketData;
using Trading.Strategies.Indicators;

namespace Trading.Strategies.Regimes;

public enum TrendRegime { Unknown = 0, Bullish = 1, Bearish = 2, Sideways = 3 }
public enum VolatilityRegime { Unknown = 0, Low = 1, Normal = 2, High = 3 }
public enum GapRegime { Unknown = 0, GapDown = 1, Normal = 2, GapUp = 3 }

public sealed record MarketRegimeSnapshot(
    DateTime OpenTimeUtc,
    TrendRegime Trend,
    VolatilityRegime Volatility,
    GapRegime Gap,
    decimal? FastEma,
    decimal? SlowEma,
    decimal? Adx,
    decimal? Atr,
    decimal? PriorAtrAverage,
    decimal? OpeningGapPercent);

public sealed record RegimeClassificationSettings
{
    public int FastEmaPeriod { get; init; } = 20;
    public int SlowEmaPeriod { get; init; } = 50;
    public int AtrPeriod { get; init; } = 14;
    public int AdxPeriod { get; init; } = 14;
    public int AtrBaselinePeriod { get; init; } = 20;
    public decimal TrendingAdxThreshold { get; init; } = 20m;
    public decimal HighVolatilityRatio { get; init; } = 1.25m;
    public decimal LowVolatilityRatio { get; init; } = .75m;
    public decimal GapThresholdPercent { get; init; } = .25m;
}

/// <summary>Point-in-time regime classification. No snapshot reads a later candle.</summary>
public static class MarketRegimeClassifier
{
    public static IReadOnlyList<MarketRegimeSnapshot> Classify(IReadOnlyList<Candle> candles,
        TimeZoneInfo exchangeTimeZone, RegimeClassificationSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(exchangeTimeZone);
        settings ??= new();
        Validate(settings);
        if (candles.Count == 0) return [];

        var indicators = IndicatorEngine.Calculate(candles, exchangeTimeZone, new(settings.FastEmaPeriod,
            settings.SlowEmaPeriod, settings.AtrPeriod, settings.AdxPeriod, 1));
        var results = new MarketRegimeSnapshot[candles.Count];
        var priorAtr = new Queue<decimal>();
        decimal atrSum = 0;
        DateOnly? currentSession = null;
        decimal? priorSessionClose = null;
        decimal? currentGap = null;
        decimal? lastClose = null;

        for (var index = 0; index < candles.Count; index++)
        {
            var candle = candles[index];
            var session = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(candle.OpenTimeUtc, exchangeTimeZone));
            if (session != currentSession)
            {
                if (currentSession is not null) priorSessionClose = lastClose;
                currentSession = session;
                currentGap = priorSessionClose is > 0
                    ? ((candle.Open - priorSessionClose.Value) / priorSessionClose.Value) * 100m
                    : null;
            }

            var point = indicators[index];
            decimal? priorAverage = priorAtr.Count == settings.AtrBaselinePeriod
                ? atrSum / settings.AtrBaselinePeriod : null;
            var trend = Trend(point, settings.TrendingAdxThreshold);
            var volatility = Volatility(point.Atr, priorAverage, settings);
            var gap = currentGap is null ? GapRegime.Unknown
                : currentGap >= settings.GapThresholdPercent ? GapRegime.GapUp
                : currentGap <= -settings.GapThresholdPercent ? GapRegime.GapDown
                : GapRegime.Normal;
            results[index] = new(candle.OpenTimeUtc, trend, volatility, gap, point.FastEma,
                point.SlowEma, point.Adx, point.Atr, priorAverage, currentGap);

            if (point.Atr is not null)
            {
                priorAtr.Enqueue(point.Atr.Value);
                atrSum += point.Atr.Value;
                if (priorAtr.Count > settings.AtrBaselinePeriod) atrSum -= priorAtr.Dequeue();
            }
            lastClose = candle.Close;
        }
        return Array.AsReadOnly(results);
    }

    private static TrendRegime Trend(IndicatorSnapshot point, decimal threshold)
    {
        if (point.FastEma is null || point.SlowEma is null || point.Adx is null) return TrendRegime.Unknown;
        if (point.Adx < threshold) return TrendRegime.Sideways;
        if (point.FastEma > point.SlowEma) return TrendRegime.Bullish;
        if (point.FastEma < point.SlowEma) return TrendRegime.Bearish;
        return TrendRegime.Sideways;
    }

    private static VolatilityRegime Volatility(decimal? atr, decimal? baseline,
        RegimeClassificationSettings settings)
    {
        if (atr is null || baseline is null || baseline <= 0) return VolatilityRegime.Unknown;
        var ratio = atr.Value / baseline.Value;
        return ratio >= settings.HighVolatilityRatio ? VolatilityRegime.High
            : ratio <= settings.LowVolatilityRatio ? VolatilityRegime.Low
            : VolatilityRegime.Normal;
    }

    private static void Validate(RegimeClassificationSettings value)
    {
        if (value.FastEmaPeriod < 1 || value.SlowEmaPeriod < 1 || value.FastEmaPeriod >= value.SlowEmaPeriod ||
            value.AtrPeriod < 1 || value.AdxPeriod < 2 || value.AtrBaselinePeriod < 1)
            throw new ArgumentException("Regime indicator periods are invalid.", nameof(value));
        if (value.TrendingAdxThreshold is < 0 or > 100 || value.LowVolatilityRatio <= 0 ||
            value.HighVolatilityRatio <= value.LowVolatilityRatio || value.GapThresholdPercent < 0)
            throw new ArgumentException("Regime thresholds are invalid.", nameof(value));
    }
}
