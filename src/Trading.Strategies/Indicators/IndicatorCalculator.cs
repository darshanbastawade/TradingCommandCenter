using Trading.Domain.MarketData;

namespace Trading.Strategies.Indicators;

/// <summary>Pure, deterministic calculations. Results retain every input timestamp.</summary>
public static class IndicatorCalculator
{
    public static IReadOnlyList<IndicatorPoint> ExponentialMovingAverage(
        IReadOnlyList<Candle> candles, int period)
    {
        Validate(candles);
        ValidatePeriod(period);
        var result = EmptySeries(candles);
        if (candles.Count < period) return result;

        var seed = 0m;
        for (var i = 0; i < period; i++) seed += candles[i].Close;
        var ema = seed / period;
        result[period - 1] = Point(candles[period - 1], ema);
        var multiplier = 2m / (period + 1m);
        for (var i = period; i < candles.Count; i++)
        {
            ema = ((candles[i].Close - ema) * multiplier) + ema;
            result[i] = Point(candles[i], ema);
        }
        return result;
    }

    public static IReadOnlyList<IndicatorPoint> RollingAverageVolume(
        IReadOnlyList<Candle> candles, int period)
    {
        Validate(candles);
        ValidatePeriod(period);
        var result = EmptySeries(candles);
        decimal sum = 0;
        for (var i = 0; i < candles.Count; i++)
        {
            sum += candles[i].Volume;
            if (i >= period) sum -= candles[i - period].Volume;
            if (i >= period - 1) result[i] = Point(candles[i], sum / period);
        }
        return result;
    }

    /// <summary>Wilder ATR seeded with the arithmetic mean of the first period true ranges.</summary>
    public static IReadOnlyList<IndicatorPoint> AverageTrueRange(
        IReadOnlyList<Candle> candles, int period)
    {
        Validate(candles);
        ValidatePeriod(period);
        var result = EmptySeries(candles);
        if (candles.Count < period) return result;

        var trueRanges = TrueRanges(candles);
        var seed = 0m;
        for (var i = 0; i < period; i++) seed += trueRanges[i];
        var atr = seed / period;
        result[period - 1] = Point(candles[period - 1], atr);
        for (var i = period; i < candles.Count; i++)
        {
            atr = ((atr * (period - 1)) + trueRanges[i]) / period;
            result[i] = Point(candles[i], atr);
        }
        return result;
    }

    /// <summary>Session VWAP using typical price (high + low + close) / 3, reset by exchange-local date.</summary>
    public static IReadOnlyList<IndicatorPoint> SessionVwap(
        IReadOnlyList<Candle> candles, TimeZoneInfo exchangeTimeZone)
    {
        Validate(candles);
        ArgumentNullException.ThrowIfNull(exchangeTimeZone);
        var result = EmptySeries(candles);
        DateOnly? session = null;
        decimal weightedPrice = 0;
        decimal volume = 0;
        for (var i = 0; i < candles.Count; i++)
        {
            var candle = candles[i];
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(candle.OpenTimeUtc, exchangeTimeZone));
            if (localDate != session)
            {
                session = localDate;
                weightedPrice = 0;
                volume = 0;
            }
            var typicalPrice = (candle.High + candle.Low + candle.Close) / 3m;
            weightedPrice += typicalPrice * candle.Volume;
            volume += candle.Volume;
            result[i] = Point(candle, volume == 0 ? null : weightedPrice / volume);
        }
        return result;
    }

    /// <summary>Wilder +DI, -DI and ADX. DI starts at period; ADX starts at 2*period-1.</summary>
    public static IReadOnlyList<DirectionalIndexPoint> AverageDirectionalIndex(
        IReadOnlyList<Candle> candles, int period)
    {
        Validate(candles);
        if (period < 2) throw new ArgumentOutOfRangeException(nameof(period), "ADX period must be at least 2.");
        var result = candles.Select(candle => new DirectionalIndexPoint(candle.OpenTimeUtc, null, null, null)).ToArray();
        if (candles.Count <= period) return result;

        var trueRanges = TrueRanges(candles);
        var positiveMovement = new decimal[candles.Count];
        var negativeMovement = new decimal[candles.Count];
        for (var i = 1; i < candles.Count; i++)
        {
            var up = candles[i].High - candles[i - 1].High;
            var down = candles[i - 1].Low - candles[i].Low;
            positiveMovement[i] = up > down && up > 0 ? up : 0;
            negativeMovement[i] = down > up && down > 0 ? down : 0;
        }

        decimal smoothedTrueRange = 0;
        decimal smoothedPositive = 0;
        decimal smoothedNegative = 0;
        for (var i = 1; i <= period; i++)
        {
            smoothedTrueRange += trueRanges[i];
            smoothedPositive += positiveMovement[i];
            smoothedNegative += negativeMovement[i];
        }

        var dx = new decimal?[candles.Count];
        decimal? adx = null;
        var firstAdxIndex = (2 * period) - 1;
        for (var i = period; i < candles.Count; i++)
        {
            if (i > period)
            {
                smoothedTrueRange = smoothedTrueRange - (smoothedTrueRange / period) + trueRanges[i];
                smoothedPositive = smoothedPositive - (smoothedPositive / period) + positiveMovement[i];
                smoothedNegative = smoothedNegative - (smoothedNegative / period) + negativeMovement[i];
            }
            var positiveDi = smoothedTrueRange == 0 ? 0 : 100m * smoothedPositive / smoothedTrueRange;
            var negativeDi = smoothedTrueRange == 0 ? 0 : 100m * smoothedNegative / smoothedTrueRange;
            var directionTotal = positiveDi + negativeDi;
            dx[i] = directionTotal == 0 ? 0 : 100m * decimal.Abs(positiveDi - negativeDi) / directionTotal;

            if (i == firstAdxIndex)
            {
                decimal total = 0;
                for (var j = period; j <= firstAdxIndex; j++) total += dx[j]!.Value;
                adx = total / period;
            }
            else if (i > firstAdxIndex)
            {
                adx = ((adx!.Value * (period - 1)) + dx[i]!.Value) / period;
            }
            result[i] = new(candles[i].OpenTimeUtc, adx, positiveDi, negativeDi);
        }
        return result;
    }

    internal static void Validate(IReadOnlyList<Candle> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (candles.Count == 0) return;
        var instrument = candles[0].InstrumentId;
        var timeframe = candles[0].Timeframe;
        for (var i = 1; i < candles.Count; i++)
        {
            if (candles[i].InstrumentId != instrument) throw new ArgumentException("All candles must belong to one instrument.", nameof(candles));
            if (candles[i].Timeframe != timeframe) throw new ArgumentException("All candles must use one timeframe.", nameof(candles));
            if (candles[i].OpenTimeUtc <= candles[i - 1].OpenTimeUtc) throw new ArgumentException("Candles must be strictly chronological.", nameof(candles));
        }
    }

    private static decimal[] TrueRanges(IReadOnlyList<Candle> candles)
    {
        var ranges = new decimal[candles.Count];
        if (candles.Count == 0) return ranges;
        ranges[0] = candles[0].High - candles[0].Low;
        for (var i = 1; i < candles.Count; i++)
        {
            var highLow = candles[i].High - candles[i].Low;
            var highClose = decimal.Abs(candles[i].High - candles[i - 1].Close);
            var lowClose = decimal.Abs(candles[i].Low - candles[i - 1].Close);
            ranges[i] = decimal.Max(highLow, decimal.Max(highClose, lowClose));
        }
        return ranges;
    }

    private static IndicatorPoint[] EmptySeries(IReadOnlyList<Candle> candles) =>
        candles.Select(candle => Point(candle, null)).ToArray();
    private static IndicatorPoint Point(Candle candle, decimal? value) => new(candle.OpenTimeUtc, value);
    private static void ValidatePeriod(int period)
    {
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period), "Period must be positive.");
    }
}
