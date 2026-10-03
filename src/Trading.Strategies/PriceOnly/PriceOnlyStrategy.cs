using Trading.Domain.MarketData;
using Trading.Strategies.Contracts;
using Trading.Strategies.Indicators;

namespace Trading.Strategies.PriceOnly;

public enum PriceOnlySetup { OpeningRangeBreakout, EmaPullback, AdxContinuation }

/// <summary>Research-only 5-minute variants. Call separately per validated history block.</summary>
public sealed class PriceOnlyStrategy(PriceOnlySetup setup) : ITradingStrategy
{
    public string Id => setup switch
    {
        PriceOnlySetup.OpeningRangeBreakout => "opening-range-breakout-price-only-v1",
        PriceOnlySetup.EmaPullback => "ema-pullback-continuation-price-only-v1",
        PriceOnlySetup.AdxContinuation => "adx-trend-continuation-price-only-v1",
        _ => throw new ArgumentOutOfRangeException(nameof(setup))
    };
    public string Name => Id;
    public const int FastPeriod = 20;
    public const int SlowPeriod = 50;
    public const int AtrPeriod = 14;
    public const int AdxPeriod = 14;
    public const int OpeningRangeBars = 3;
    public const decimal RewardRiskMultiple = 3m;

    public IReadOnlyList<StrategySignal> Evaluate(IReadOnlyList<Candle> candles, TimeZoneInfo exchangeTimeZone)
    {
        _ = Id; // Validate enum even for empty inputs.
        IndicatorCalculator.Validate(candles);
        if (candles.Any(c => c.Timeframe != Timeframe.Minute5))
            throw new ArgumentException("Price-only baseline strategies require complete 5-minute candles.");
        // Price-only calculations deliberately do not compute VWAP or rolling volume.
        var fast = IndicatorCalculator.ExponentialMovingAverage(candles, FastPeriod);
        var slow = IndicatorCalculator.ExponentialMovingAverage(candles, SlowPeriod);
        var atr = IndicatorCalculator.AverageTrueRange(candles, AtrPeriod);
        var adx = IndicatorCalculator.AverageDirectionalIndex(candles, AdxPeriod);
        var signals = new List<StrategySignal>();
        DateOnly? session = null;
        var start = 0;
        for (var i = 0; i < candles.Count; i++)
        {
            var candle = candles[i];
            var local = TimeZoneInfo.ConvertTimeFromUtc(candle.OpenTimeUtc, exchangeTimeZone);
            var date = DateOnly.FromDateTime(local);
            if (date != session) { session = date; start = i; }
            var time = TimeOnly.FromDateTime(local);
            var end = setup == PriceOnlySetup.OpeningRangeBreakout ? new TimeOnly(11, 30) : new TimeOnly(14, 30);
            if (time < new TimeOnly(9, 30) || time >= end || i == 0 ||
                fast[i].Value is not decimal f || slow[i].Value is not decimal s ||
                atr[i].Value is not decimal a || a <= 0 || adx[i].Adx is not decimal strength ||
                adx[i].PositiveDi is not decimal plus || adx[i].NegativeDi is not decimal minus) continue;
            TradeDirection? direction = null;
            decimal reference = 0;
            if (setup == PriceOnlySetup.OpeningRangeBreakout)
            {
                if (i - start < OpeningRangeBars) continue;
                // Do not reinterpret the first available bar after a missing open as the opening range.
                var opening = TimeZoneInfo.ConvertTimeFromUtc(candles[start].OpenTimeUtc, exchangeTimeZone);
                if (TimeOnly.FromDateTime(opening) != new TimeOnly(9, 15) ||
                    candles[start + 1].OpenTimeUtc != candles[start].OpenTimeUtc.AddMinutes(5) ||
                    candles[start + 2].OpenTimeUtc != candles[start].OpenTimeUtc.AddMinutes(10)) continue;
                var high = candles.Skip(start).Take(OpeningRangeBars).Max(c => c.High);
                var low = candles.Skip(start).Take(OpeningRangeBars).Min(c => c.Low);
                if (candle.Close > high) { direction = TradeDirection.Long; reference = high; }
                else if (candle.Close < low) { direction = TradeDirection.Short; reference = low; }
            }
            else if (i > start && candles[i - 1].OpenTimeUtc.AddMinutes(5) == candle.OpenTimeUtc)
            {
                var previous = candles[i - 1];
                if (setup == PriceOnlySetup.EmaPullback && strength >= 20m && fast[i - 1].Value is decimal previousFast)
                {
                    if (f > s && plus > minus && previous.Low <= previousFast && previous.Close <= previousFast &&
                        candle.Close > f && candle.High > previous.High) direction = TradeDirection.Long;
                    else if (f < s && minus > plus && previous.High >= previousFast && previous.Close >= previousFast &&
                        candle.Close < f && candle.Low < previous.Low) direction = TradeDirection.Short;
                    reference = previousFast;
                }
                else if (setup == PriceOnlySetup.AdxContinuation && strength >= 25m &&
                    adx[i - 1].Adx is decimal priorStrength && strength > priorStrength)
                {
                    if (f > s && plus > minus && candle.Close > f && candle.Close > previous.High)
                    { direction = TradeDirection.Long; reference = previous.High; }
                    else if (f < s && minus > plus && candle.Close < f && candle.Close < previous.Low)
                    { direction = TradeDirection.Short; reference = previous.Low; }
                }
            }
            if (direction is null) continue;
            var stop = direction == TradeDirection.Long ? candle.Close - a : candle.Close + a;
            var target = direction == TradeDirection.Long ? candle.Close + RewardRiskMultiple * a : candle.Close - RewardRiskMultiple * a;
            if (stop <= 0 || target <= 0) continue;
            signals.Add(new(Id, candle.InstrumentId, candle.OpenTimeUtc, direction.Value, candle.Close, stop,
                target, a, RewardRiskMultiple, new(f, s, null, a, strength, plus, minus, candle.Volume, null, reference)));
        }
        return signals.AsReadOnly();
    }
}
