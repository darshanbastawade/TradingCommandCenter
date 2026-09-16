using Trading.Domain.MarketData;
using Trading.Strategies.Common;
using Trading.Strategies.Contracts;
using Trading.Strategies.Indicators;

namespace Trading.Strategies.AdxTrendContinuation;

public sealed record AdxTrendContinuationParameters
{
    public int FastEmaPeriod { get; init; } = 20;
    public int SlowEmaPeriod { get; init; } = 50;
    public int AtrPeriod { get; init; } = 14;
    public int AdxPeriod { get; init; } = 14;
    public int VolumeAveragePeriod { get; init; } = 20;
    public decimal MinimumAdx { get; init; } = 25m;
    public decimal AtrStopMultiple { get; init; } = 1m;
    public decimal RewardRiskMultiple { get; init; } = 3m;
    public TimeOnly EntryWindowStart { get; init; } = new(9, 30);
    public TimeOnly EntryWindowEnd { get; init; } = new(14, 30);
}

public sealed class AdxTrendContinuationStrategy : ITradingStrategy
{
    public const string StrategyId = "adx-trend-continuation-v1";
    private readonly AdxTrendContinuationParameters parameters;

    public AdxTrendContinuationStrategy(AdxTrendContinuationParameters? parameters = null)
    {
        this.parameters = parameters ?? new();
        if (this.parameters.FastEmaPeriod < 1 || this.parameters.SlowEmaPeriod <= this.parameters.FastEmaPeriod ||
            this.parameters.AtrPeriod < 1 || this.parameters.AdxPeriod < 2 || this.parameters.VolumeAveragePeriod < 1 ||
            this.parameters.MinimumAdx is < 0 or > 100 || this.parameters.AtrStopMultiple <= 0 ||
            this.parameters.RewardRiskMultiple <= 0 || this.parameters.EntryWindowStart >= this.parameters.EntryWindowEnd)
            throw new ArgumentException("ADX-continuation parameters are invalid.", nameof(parameters));
    }

    public string Id => StrategyId;
    public string Name => "ADX Trend Continuation";

    public IReadOnlyList<StrategySignal> Evaluate(IReadOnlyList<Candle> candles, TimeZoneInfo exchangeTimeZone)
    {
        var points = IndicatorEngine.Calculate(candles, exchangeTimeZone, new(parameters.FastEmaPeriod,
            parameters.SlowEmaPeriod, parameters.AtrPeriod, parameters.AdxPeriod, parameters.VolumeAveragePeriod));
        var signals = new List<StrategySignal>();
        for (var index = 1; index < candles.Count; index++)
        {
            var candle = candles[index];
            if (!StrategySignalFactory.InWindow(candle.OpenTimeUtc, exchangeTimeZone,
                    parameters.EntryWindowStart, parameters.EntryWindowEnd) ||
                StrategySignalFactory.Session(candle.OpenTimeUtc, exchangeTimeZone) !=
                StrategySignalFactory.Session(candles[index - 1].OpenTimeUtc, exchangeTimeZone)) continue;
            var point = points[index];
            var previous = points[index - 1];
            if (point.FastEma is null || point.SlowEma is null || point.Adx is null || previous.Adx is null ||
                point.PositiveDi is null || point.NegativeDi is null || previous.AverageVolume is null ||
                point.Adx < parameters.MinimumAdx || point.Adx <= previous.Adx) continue;

            TradeDirection? direction = null;
            if (point.FastEma > point.SlowEma && point.PositiveDi > point.NegativeDi &&
                candle.Close > point.FastEma && candle.Close > candles[index - 1].High) direction = TradeDirection.Long;
            else if (point.FastEma < point.SlowEma && point.NegativeDi > point.PositiveDi &&
                candle.Close < point.FastEma && candle.Close < candles[index - 1].Low) direction = TradeDirection.Short;
            if (direction is null) continue;
            var reference = direction == TradeDirection.Long ? candles[index - 1].High : candles[index - 1].Low;
            var signal = StrategySignalFactory.Create(Id, candle, point, previous.AverageVolume.Value,
                reference, direction.Value, parameters.AtrStopMultiple, parameters.RewardRiskMultiple);
            if (signal is not null) signals.Add(signal);
        }
        return signals.AsReadOnly();
    }
}
