using Trading.Domain.MarketData;
using Trading.Strategies.Contracts;
using Trading.Strategies.Indicators;

namespace Trading.Strategies.VwapEmaTrendBreakout;

/// <summary>Research hypothesis for an underlying instrument; emits candidates and never places orders.</summary>
public sealed class VwapEmaTrendBreakoutStrategy : ITradingStrategy
{
    public const string StrategyId = "vwap-ema-trend-breakout-v1";
    private readonly VwapEmaTrendBreakoutParameters parameters;

    public VwapEmaTrendBreakoutStrategy(VwapEmaTrendBreakoutParameters? parameters = null)
    {
        this.parameters = parameters ?? new();
        Validate(this.parameters);
    }

    public string Id => StrategyId;
    public string Name => "VWAP + EMA Trend Breakout";

    public IReadOnlyList<StrategySignal> Evaluate(IReadOnlyList<Candle> candles, TimeZoneInfo exchangeTimeZone)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(exchangeTimeZone);
        var indicators = IndicatorEngine.Calculate(candles, exchangeTimeZone, new(
            parameters.FastEmaPeriod, parameters.SlowEmaPeriod, parameters.AtrPeriod,
            parameters.AdxPeriod, parameters.VolumeAveragePeriod));
        if (candles.Count == 0) return [];

        var signals = new List<StrategySignal>();
        DateOnly? session = null;
        var sessionStartIndex = 0;
        for (var i = 0; i < candles.Count; i++)
        {
            var candle = candles[i];
            var local = TimeZoneInfo.ConvertTimeFromUtc(candle.OpenTimeUtc, exchangeTimeZone);
            var localDate = DateOnly.FromDateTime(local);
            if (localDate != session)
            {
                session = localDate;
                sessionStartIndex = i;
            }
            if (TimeOnly.FromDateTime(local) < parameters.EntryWindowStart ||
                TimeOnly.FromDateTime(local) >= parameters.EntryWindowEnd ||
                i - sessionStartIndex < parameters.BreakoutLookbackBars || i == 0)
                continue;

            var point = indicators[i];
            var previous = indicators[i - 1];
            if (point.FastEma is null || point.SlowEma is null || point.SessionVwap is null ||
                point.Atr is null || point.Adx is null || point.PositiveDi is null || point.NegativeDi is null ||
                previous.AverageVolume is null || point.Atr <= 0 || previous.AverageVolume <= 0 || candle.Volume <= 0)
                continue;
            if (point.Adx < parameters.MinimumAdx || candle.Volume < previous.AverageVolume * parameters.VolumeMultiplier)
                continue;

            var firstBreakoutBar = i - parameters.BreakoutLookbackBars;
            var priorHigh = candles.Skip(firstBreakoutBar).Take(parameters.BreakoutLookbackBars).Max(bar => bar.High);
            var priorLow = candles.Skip(firstBreakoutBar).Take(parameters.BreakoutLookbackBars).Min(bar => bar.Low);
            if (point.FastEma > point.SlowEma && candle.Close > point.SessionVwap &&
                point.PositiveDi > point.NegativeDi && candle.Close > priorHigh)
            {
                var signal = Create(candle, point, previous.AverageVolume.Value, priorHigh, TradeDirection.Long);
                if (signal is not null) signals.Add(signal);
            }
            else if (point.FastEma < point.SlowEma && candle.Close < point.SessionVwap &&
                point.NegativeDi > point.PositiveDi && candle.Close < priorLow)
            {
                var signal = Create(candle, point, previous.AverageVolume.Value, priorLow, TradeDirection.Short);
                if (signal is not null) signals.Add(signal);
            }
        }
        return signals.AsReadOnly();
    }

    private StrategySignal? Create(Candle candle, IndicatorSnapshot point, decimal previousVolume,
        decimal breakoutLevel, TradeDirection direction)
    {
        var risk = point.Atr!.Value * parameters.AtrStopMultiple;
        var stop = direction == TradeDirection.Long ? candle.Close - risk : candle.Close + risk;
        var target = direction == TradeDirection.Long
            ? candle.Close + (risk * parameters.RewardRiskMultiple)
            : candle.Close - (risk * parameters.RewardRiskMultiple);
        if (stop <= 0 || target <= 0) return null;
        return new(StrategyId, candle.InstrumentId, candle.OpenTimeUtc, direction, candle.Close, stop, target,
            risk, parameters.RewardRiskMultiple, new(point.FastEma!.Value, point.SlowEma!.Value,
                point.SessionVwap!.Value, point.Atr.Value, point.Adx!.Value, point.PositiveDi!.Value,
                point.NegativeDi!.Value, candle.Volume, previousVolume, breakoutLevel));
    }

    private static void Validate(VwapEmaTrendBreakoutParameters value)
    {
        if (value.FastEmaPeriod < 1 || value.SlowEmaPeriod < 1 || value.FastEmaPeriod >= value.SlowEmaPeriod)
            throw new ArgumentException("EMA periods must be positive and fast must be smaller than slow.", nameof(value));
        if (value.AtrPeriod < 1 || value.AdxPeriod < 2 || value.VolumeAveragePeriod < 1)
            throw new ArgumentException("ATR/volume periods must be positive and ADX must be at least 2.", nameof(value));
        if (value.BreakoutLookbackBars is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(value), "Breakout lookback must be 1-1000 bars.");
        if (value.MinimumAdx is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(value), "Minimum ADX must be 0-100.");
        if (value.VolumeMultiplier <= 0 || value.AtrStopMultiple <= 0 || value.RewardRiskMultiple <= 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Volume, stop, and reward/risk multipliers must be positive.");
        if (value.EntryWindowStart >= value.EntryWindowEnd)
            throw new ArgumentException("Entry window start must precede its end.", nameof(value));
    }
}
