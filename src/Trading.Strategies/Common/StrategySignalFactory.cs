using Trading.Domain.MarketData;
using Trading.Strategies.Contracts;
using Trading.Strategies.Indicators;

namespace Trading.Strategies.Common;

internal static class StrategySignalFactory
{
    public static StrategySignal? Create(string strategyId, Candle candle, IndicatorSnapshot point,
        decimal previousAverageVolume, decimal referenceLevel, TradeDirection direction,
        decimal atrStopMultiple, decimal rewardRiskMultiple)
    {
        if (point.FastEma is null || point.SlowEma is null || point.SessionVwap is null ||
            point.Atr is null || point.Adx is null || point.PositiveDi is null || point.NegativeDi is null ||
            point.Atr <= 0 || previousAverageVolume <= 0) return null;
        var risk = point.Atr.Value * atrStopMultiple;
        var stop = direction == TradeDirection.Long ? candle.Close - risk : candle.Close + risk;
        var target = direction == TradeDirection.Long ? candle.Close + risk * rewardRiskMultiple
            : candle.Close - risk * rewardRiskMultiple;
        if (stop <= 0 || target <= 0) return null;
        return new(strategyId, candle.InstrumentId, candle.OpenTimeUtc, direction, candle.Close, stop,
            target, risk, rewardRiskMultiple, new(point.FastEma.Value, point.SlowEma.Value,
                point.SessionVwap.Value, point.Atr.Value, point.Adx.Value, point.PositiveDi.Value,
                point.NegativeDi.Value, candle.Volume, previousAverageVolume, referenceLevel));
    }

    public static bool InWindow(DateTime utc, TimeZoneInfo zone, TimeOnly start, TimeOnly end)
    {
        var time = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));
        return time >= start && time < end;
    }

    public static DateOnly Session(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));
}
