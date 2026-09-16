using Trading.Domain.MarketData;
using Trading.Strategies.Common;
using Trading.Strategies.Contracts;
using Trading.Strategies.Indicators;

namespace Trading.Strategies.EmaPullbackContinuation;

public sealed record EmaPullbackContinuationParameters
{
    public int FastEmaPeriod { get; init; } = 20;
    public int SlowEmaPeriod { get; init; } = 50;
    public int AtrPeriod { get; init; } = 14;
    public int AdxPeriod { get; init; } = 14;
    public int VolumeAveragePeriod { get; init; } = 20;
    public decimal MinimumAdx { get; init; } = 20m;
    public decimal VolumeMultiplier { get; init; } = 1m;
    public decimal AtrStopMultiple { get; init; } = 1m;
    public decimal RewardRiskMultiple { get; init; } = 3m;
    public TimeOnly EntryWindowStart { get; init; } = new(9, 30);
    public TimeOnly EntryWindowEnd { get; init; } = new(14, 30);
}

public sealed class EmaPullbackContinuationStrategy : ITradingStrategy
{
    public const string StrategyId = "ema-pullback-continuation-v1";
    private readonly EmaPullbackContinuationParameters parameters;

    public EmaPullbackContinuationStrategy(EmaPullbackContinuationParameters? parameters = null)
    {
        this.parameters = parameters ?? new();
        Validate(this.parameters);
    }

    public string Id => StrategyId;
    public string Name => "EMA Pullback Continuation";

    public IReadOnlyList<StrategySignal> Evaluate(IReadOnlyList<Candle> candles, TimeZoneInfo exchangeTimeZone)
    {
        var points = IndicatorEngine.Calculate(candles, exchangeTimeZone, new(parameters.FastEmaPeriod,
            parameters.SlowEmaPeriod, parameters.AtrPeriod, parameters.AdxPeriod, parameters.VolumeAveragePeriod));
        var signals = new List<StrategySignal>();
        for (var index = 1; index < candles.Count; index++)
        {
            var candle = candles[index];
            var previousCandle = candles[index - 1];
            if (StrategySignalFactory.Session(candle.OpenTimeUtc, exchangeTimeZone) !=
                StrategySignalFactory.Session(previousCandle.OpenTimeUtc, exchangeTimeZone) ||
                !StrategySignalFactory.InWindow(candle.OpenTimeUtc, exchangeTimeZone,
                    parameters.EntryWindowStart, parameters.EntryWindowEnd)) continue;
            var point = points[index];
            var previous = points[index - 1];
            if (point.FastEma is null || point.SlowEma is null || point.SessionVwap is null || point.Adx is null ||
                point.PositiveDi is null || point.NegativeDi is null || previous.FastEma is null ||
                previous.AverageVolume is null || point.Adx < parameters.MinimumAdx ||
                candle.Volume < previous.AverageVolume * parameters.VolumeMultiplier) continue;

            TradeDirection? direction = null;
            if (point.FastEma > point.SlowEma && candle.Close > point.SessionVwap &&
                point.PositiveDi > point.NegativeDi && previousCandle.Low <= previous.FastEma &&
                previousCandle.Close <= previous.FastEma && candle.Close > point.FastEma &&
                candle.High > previousCandle.High) direction = TradeDirection.Long;
            else if (point.FastEma < point.SlowEma && candle.Close < point.SessionVwap &&
                point.NegativeDi > point.PositiveDi && previousCandle.High >= previous.FastEma &&
                previousCandle.Close >= previous.FastEma && candle.Close < point.FastEma &&
                candle.Low < previousCandle.Low) direction = TradeDirection.Short;
            if (direction is null) continue;
            var signal = StrategySignalFactory.Create(Id, candle, point, previous.AverageVolume.Value,
                previous.FastEma.Value, direction.Value, parameters.AtrStopMultiple, parameters.RewardRiskMultiple);
            if (signal is not null) signals.Add(signal);
        }
        return signals.AsReadOnly();
    }

    private static void Validate(EmaPullbackContinuationParameters value)
    {
        if (value.FastEmaPeriod < 1 || value.SlowEmaPeriod <= value.FastEmaPeriod || value.AtrPeriod < 1 ||
            value.AdxPeriod < 2 || value.VolumeAveragePeriod < 1 || value.MinimumAdx is < 0 or > 100 ||
            value.VolumeMultiplier <= 0 || value.AtrStopMultiple <= 0 || value.RewardRiskMultiple <= 0 ||
            value.EntryWindowStart >= value.EntryWindowEnd)
            throw new ArgumentException("EMA-pullback parameters are invalid.", nameof(value));
    }
}
