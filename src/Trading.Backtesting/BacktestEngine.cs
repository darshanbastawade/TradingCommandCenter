using Trading.Domain.MarketData;
using Trading.Strategies.Contracts;
using Trading.Backtesting.Costs;
using Trading.Risk;

namespace Trading.Backtesting;

/// <summary>Deterministic completed-candle simulator. It never places or routes orders.</summary>
public static class BacktestEngine
{
    public static BacktestResult Run(ITradingStrategy strategy, IReadOnlyList<Candle> candles,
        TimeZoneInfo exchangeTimeZone, BacktestSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(exchangeTimeZone);
        settings ??= new();
        ValidateSettings(settings);
        ValidateCandles(candles);

        var signals = strategy.Evaluate(candles, exchangeTimeZone);
        ValidateSignals(strategy, candles, signals);
        if (candles.Count == 0)
            return new(settings.InitialCapital, settings.InitialCapital, [], []);

        var signalByTime = signals.ToDictionary(signal => signal.OpenTimeUtc);
        var trades = new List<BacktestTrade>();
        var ignored = new List<IgnoredCandidate>();
        OpenPosition? position = null;
        StrategySignal? pending = null;
        var capital = settings.InitialCapital;

        for (var index = 0; index < candles.Count; index++)
        {
            var candle = candles[index];
            var localDate = ExchangeDate(candle.OpenTimeUtc, exchangeTimeZone);
            var localTime = ExchangeTime(candle.OpenTimeUtc, exchangeTimeZone);

            if (position is not null && localDate != position.EntrySession)
            {
                var previous = candles[index - 1];
                Close(position, previous, previous.Close, BacktestExitReason.SessionExit);
                position = null;
            }

            if (pending is not null)
            {
                var signalDate = ExchangeDate(pending.OpenTimeUtc, exchangeTimeZone);
                if (localDate != signalDate)
                    ignored.Add(new(pending.OpenTimeUtc, IgnoredCandidateReason.FollowingBarInDifferentSession));
                else if (localTime >= settings.SessionExitTime)
                    ignored.Add(new(pending.OpenTimeUtc, IgnoredCandidateReason.FollowingBarAtOrAfterSessionExit));
                else
                {
                    position = Open(pending, candle, localDate, settings);
                    if (position is null)
                        ignored.Add(new(pending.OpenTimeUtc, IgnoredCandidateReason.InvalidEntryLevels));
                    else if (position.Quantity == 0)
                    {
                        position = null;
                        ignored.Add(new(pending.OpenTimeUtc, IgnoredCandidateReason.InsufficientRiskOrCapital));
                    }
                }
                pending = null;
            }

            if (position is not null)
            {
                if (localTime >= settings.SessionExitTime)
                {
                    Close(position, candle, candle.Open, BacktestExitReason.SessionExit);
                    position = null;
                }
                else if (TryResolveExit(position, candle, out var rawExit, out var reason))
                {
                    Close(position, candle, rawExit, reason);
                    position = null;
                }
            }

            if (signalByTime.TryGetValue(candle.OpenTimeUtc, out var signal))
            {
                if (position is not null)
                    ignored.Add(new(signal.OpenTimeUtc, IgnoredCandidateReason.PositionAlreadyOpen));
                else if (index == candles.Count - 1)
                    ignored.Add(new(signal.OpenTimeUtc, IgnoredCandidateReason.NoFollowingBar));
                else
                    pending = signal;
            }
        }

        if (position is not null)
        {
            var last = candles[^1];
            Close(position, last, last.Close, BacktestExitReason.EndOfData);
        }

        return new(settings.InitialCapital, capital, trades.AsReadOnly(), ignored.AsReadOnly());

        void Close(OpenPosition open, Candle candle, decimal rawExit, BacktestExitReason reason)
        {
            var exit = ApplyExitSlippage(rawExit, open.Direction, settings.SlippageBasisPointsPerSide);
            var direction = open.Direction == TradeDirection.Long ? 1m : -1m;
            var gross = (exit - open.EntryPrice) * direction * open.Quantity;
            var variableCosts = (open.EntryPrice + exit) * open.Quantity *
                settings.VariableCostBasisPointsPerSide / 10_000m;
            var simpleCosts = (settings.FixedCostPerSide * 2m) + variableCosts;
            var breakdown = settings.CostModel?.Calculate(new(open.EntryPrice, exit, open.Quantity, open.Direction))
                ?? TradeCostBreakdown.None;
            breakdown = breakdown with { Other = breakdown.Other + simpleCosts };
            var costs = breakdown.Total;
            var net = gross - costs;
            capital += net;
            trades.Add(new(open.Signal.StrategyId, open.Signal.InstrumentId, open.Direction,
                open.Signal.OpenTimeUtc, open.EntryBarOpenTimeUtc, candle.OpenTimeUtc, open.Quantity,
                open.EntryPrice, open.StopPrice, open.TargetPrice, exit, reason, gross, breakdown, costs, net, capital));
        }
    }

    private static OpenPosition? Open(StrategySignal signal, Candle candle, DateOnly session, BacktestSettings settings)
    {
        var entry = ApplyEntrySlippage(candle.Open, signal.Direction, settings.SlippageBasisPointsPerSide);
        var stop = signal.Direction == TradeDirection.Long ? entry - signal.RiskPerUnit : entry + signal.RiskPerUnit;
        var targetDistance = signal.RiskPerUnit * signal.RewardRiskMultiple;
        var target = signal.Direction == TradeDirection.Long ? entry + targetDistance : entry - targetDistance;
        if (stop <= 0 || target <= 0) return null;
        var quantity = settings.Quantity;
        if (settings.RiskBasedSizing is not null)
        {
            var sizing = settings.RiskBasedSizing;
            quantity = PositionSizer.Calculate(new(sizing.AllowedRiskPerTrade, entry, stop, sizing.LotSize,
                sizing.MaximumCapitalPerTrade, sizing.MaximumLots)).Quantity;
        }
        return new(signal, signal.Direction, candle.OpenTimeUtc, session, entry, stop, target, quantity);
    }

    private static bool TryResolveExit(OpenPosition position, Candle candle, out decimal price,
        out BacktestExitReason reason)
    {
        if (position.Direction == TradeDirection.Long)
        {
            if (candle.Open <= position.StopPrice) return Exit(candle.Open, BacktestExitReason.StopLoss, out price, out reason);
            if (candle.Open >= position.TargetPrice) return Exit(position.TargetPrice, BacktestExitReason.Target, out price, out reason);
            // OHLC cannot reveal which level came first. Stop-first is the conservative convention.
            if (candle.Low <= position.StopPrice) return Exit(position.StopPrice, BacktestExitReason.StopLoss, out price, out reason);
            if (candle.High >= position.TargetPrice) return Exit(position.TargetPrice, BacktestExitReason.Target, out price, out reason);
        }
        else
        {
            if (candle.Open >= position.StopPrice) return Exit(candle.Open, BacktestExitReason.StopLoss, out price, out reason);
            if (candle.Open <= position.TargetPrice) return Exit(position.TargetPrice, BacktestExitReason.Target, out price, out reason);
            if (candle.High >= position.StopPrice) return Exit(position.StopPrice, BacktestExitReason.StopLoss, out price, out reason);
            if (candle.Low <= position.TargetPrice) return Exit(position.TargetPrice, BacktestExitReason.Target, out price, out reason);
        }
        price = 0;
        reason = default;
        return false;

        static bool Exit(decimal value, BacktestExitReason exitReason, out decimal exitPrice,
            out BacktestExitReason result)
        {
            exitPrice = value;
            result = exitReason;
            return true;
        }
    }

    private static decimal ApplyEntrySlippage(decimal price, TradeDirection direction, decimal basisPoints) =>
        direction == TradeDirection.Long ? price * (1m + basisPoints / 10_000m) : price * (1m - basisPoints / 10_000m);

    private static decimal ApplyExitSlippage(decimal price, TradeDirection direction, decimal basisPoints) =>
        direction == TradeDirection.Long ? price * (1m - basisPoints / 10_000m) : price * (1m + basisPoints / 10_000m);

    private static DateOnly ExchangeDate(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));

    private static TimeOnly ExchangeTime(DateTime utc, TimeZoneInfo zone) =>
        TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));

    private static void ValidateSettings(BacktestSettings value)
    {
        if (value.InitialCapital < 0) throw new ArgumentOutOfRangeException(nameof(value), "Initial capital cannot be negative.");
        if (value.Quantity < 1) throw new ArgumentOutOfRangeException(nameof(value), "Quantity must be positive.");
        if (value.SlippageBasisPointsPerSide is < 0 or >= 10_000 ||
            value.VariableCostBasisPointsPerSide is < 0 or >= 10_000 || value.FixedCostPerSide < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Costs must be non-negative and basis points below 10,000.");
        if (value.RiskBasedSizing is not null)
        {
            var sizing = value.RiskBasedSizing;
            if (sizing.AllowedRiskPerTrade <= 0 || sizing.LotSize < 1 || sizing.MaximumCapitalPerTrade <= 0 ||
                sizing.MaximumLots is <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Risk sizing values must be positive.");
        }
    }

    private static void ValidateCandles(IReadOnlyList<Candle> candles)
    {
        for (var index = 1; index < candles.Count; index++)
        {
            if (candles[index].InstrumentId != candles[0].InstrumentId || candles[index].Timeframe != candles[0].Timeframe)
                throw new ArgumentException("Candles must share one instrument and timeframe.", nameof(candles));
            if (candles[index].OpenTimeUtc <= candles[index - 1].OpenTimeUtc)
                throw new ArgumentException("Candles must be strictly chronological.", nameof(candles));
        }
    }

    private static void ValidateSignals(ITradingStrategy strategy, IReadOnlyList<Candle> candles,
        IReadOnlyList<StrategySignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        var candleTimes = candles.Select(candle => candle.OpenTimeUtc).ToHashSet();
        DateTime? previous = null;
        foreach (var signal in signals)
        {
            if (signal.StrategyId != strategy.Id || signal.InstrumentId == Guid.Empty ||
                !Enum.IsDefined(signal.Direction) || signal.EntryPrice <= 0 || signal.StopPrice <= 0 ||
                signal.TargetPrice <= 0 || signal.RiskPerUnit <= 0 || signal.RewardRiskMultiple <= 0 ||
                !candleTimes.Contains(signal.OpenTimeUtc))
                throw new InvalidOperationException("Strategy returned an invalid or unmatched candidate.");
            if (candles.Count > 0 && signal.InstrumentId != candles[0].InstrumentId)
                throw new InvalidOperationException("Strategy candidate instrument does not match the candle series.");
            if (previous is not null && signal.OpenTimeUtc <= previous)
                throw new InvalidOperationException("Strategy candidates must be unique and chronological.");
            previous = signal.OpenTimeUtc;
        }
    }

    private sealed record OpenPosition(StrategySignal Signal, TradeDirection Direction, DateTime EntryBarOpenTimeUtc,
        DateOnly EntrySession, decimal EntryPrice, decimal StopPrice, decimal TargetPrice, int Quantity);
}
