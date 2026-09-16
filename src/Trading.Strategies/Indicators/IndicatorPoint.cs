namespace Trading.Strategies.Indicators;

public readonly record struct IndicatorPoint(DateTime OpenTimeUtc, decimal? Value);

public readonly record struct DirectionalIndexPoint(
    DateTime OpenTimeUtc,
    decimal? Adx,
    decimal? PositiveDi,
    decimal? NegativeDi);

public sealed record IndicatorSnapshot(
    DateTime OpenTimeUtc,
    decimal Close,
    decimal? FastEma,
    decimal? SlowEma,
    decimal? SessionVwap,
    decimal? Atr,
    decimal? Adx,
    decimal? PositiveDi,
    decimal? NegativeDi,
    decimal? AverageVolume);
