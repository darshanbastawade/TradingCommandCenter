using Trading.Domain.MarketData;
using Trading.Strategies.Common;
using Trading.Strategies.Contracts;
using Trading.Strategies.Indicators;

namespace Trading.Strategies.OpeningRangeBreakout;

public sealed class OpeningRangeBreakoutStrategy : ITradingStrategy
{
    public const string StrategyId = "opening-range-breakout-v1";
    private readonly OpeningRangeBreakoutParameters parameters;

    public OpeningRangeBreakoutStrategy(OpeningRangeBreakoutParameters? parameters = null)
    {
        this.parameters = parameters ?? new();
        if (this.parameters.FastEmaPeriod < 1 || this.parameters.SlowEmaPeriod <= this.parameters.FastEmaPeriod ||
            this.parameters.AtrPeriod < 1 || this.parameters.AdxPeriod < 2 ||
            this.parameters.VolumeAveragePeriod < 1 || this.parameters.OpeningRangeBars < 1 ||
            this.parameters.VolumeMultiplier <= 0 || this.parameters.AtrStopMultiple <= 0 ||
            this.parameters.RewardRiskMultiple <= 0 || this.parameters.EntryWindowStart >= this.parameters.EntryWindowEnd)
            throw new ArgumentException("Opening-range parameters are invalid.", nameof(parameters));
    }

    public string Id => StrategyId;
    public string Name => "Opening Range Breakout";

    public IReadOnlyList<StrategySignal> Evaluate(IReadOnlyList<Candle> candles, TimeZoneInfo exchangeTimeZone)
    {
        var points = IndicatorEngine.Calculate(candles, exchangeTimeZone, new(parameters.FastEmaPeriod,
            parameters.SlowEmaPeriod, parameters.AtrPeriod, parameters.AdxPeriod, parameters.VolumeAveragePeriod));
        var signals = new List<StrategySignal>();
        foreach (var session in candles.Select((candle, index) => (candle, index))
                     .GroupBy(item => StrategySignalFactory.Session(item.candle.OpenTimeUtc, exchangeTimeZone)))
        {
            var bars = session.ToArray();
            if (bars.Length <= parameters.OpeningRangeBars) continue;
            var openingHigh = bars.Take(parameters.OpeningRangeBars).Max(item => item.candle.High);
            var openingLow = bars.Take(parameters.OpeningRangeBars).Min(item => item.candle.Low);
            foreach (var item in bars.Skip(parameters.OpeningRangeBars))
            {
                if (!StrategySignalFactory.InWindow(item.candle.OpenTimeUtc, exchangeTimeZone,
                        parameters.EntryWindowStart, parameters.EntryWindowEnd) || item.index == 0) continue;
                var point = points[item.index];
                var average = points[item.index - 1].AverageVolume;
                if (average is null || item.candle.Volume < average * parameters.VolumeMultiplier) continue;
                var direction = item.candle.Close > openingHigh ? TradeDirection.Long
                    : item.candle.Close < openingLow ? TradeDirection.Short : (TradeDirection?)null;
                if (direction is null) continue;
                var signal = StrategySignalFactory.Create(Id, item.candle, point, average.Value,
                    direction == TradeDirection.Long ? openingHigh : openingLow, direction.Value,
                    parameters.AtrStopMultiple, parameters.RewardRiskMultiple);
                if (signal is not null) signals.Add(signal);
            }
        }
        return signals.AsReadOnly();
    }
}
