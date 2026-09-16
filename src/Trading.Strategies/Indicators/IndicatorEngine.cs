using Trading.Domain.MarketData;

namespace Trading.Strategies.Indicators;

public sealed record IndicatorParameters(
    int FastEmaPeriod = 20,
    int SlowEmaPeriod = 50,
    int AtrPeriod = 14,
    int AdxPeriod = 14,
    int VolumeAveragePeriod = 20);

public static class IndicatorEngine
{
    public static IReadOnlyList<IndicatorSnapshot> Calculate(
        IReadOnlyList<Candle> candles,
        TimeZoneInfo exchangeTimeZone,
        IndicatorParameters? parameters = null)
    {
        IndicatorCalculator.Validate(candles);
        ArgumentNullException.ThrowIfNull(exchangeTimeZone);
        parameters ??= new();
        if (parameters.FastEmaPeriod < 1 || parameters.SlowEmaPeriod < 1 || parameters.AtrPeriod < 1 ||
            parameters.AdxPeriod < 2 || parameters.VolumeAveragePeriod < 1)
            throw new ArgumentOutOfRangeException(nameof(parameters), "All periods must be positive and ADX must be at least 2.");
        if (parameters.FastEmaPeriod >= parameters.SlowEmaPeriod)
            throw new ArgumentException("Fast EMA period must be smaller than slow EMA period.", nameof(parameters));

        var fast = IndicatorCalculator.ExponentialMovingAverage(candles, parameters.FastEmaPeriod);
        var slow = IndicatorCalculator.ExponentialMovingAverage(candles, parameters.SlowEmaPeriod);
        var vwap = IndicatorCalculator.SessionVwap(candles, exchangeTimeZone);
        var atr = IndicatorCalculator.AverageTrueRange(candles, parameters.AtrPeriod);
        var adx = IndicatorCalculator.AverageDirectionalIndex(candles, parameters.AdxPeriod);
        var volume = IndicatorCalculator.RollingAverageVolume(candles, parameters.VolumeAveragePeriod);
        return candles.Select((candle, i) => new IndicatorSnapshot(
            candle.OpenTimeUtc, candle.Close, fast[i].Value, slow[i].Value, vwap[i].Value,
            atr[i].Value, adx[i].Adx, adx[i].PositiveDi, adx[i].NegativeDi, volume[i].Value)).ToArray();
    }
}
