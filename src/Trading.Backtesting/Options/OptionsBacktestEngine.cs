using Trading.Backtesting.Costs;
using Trading.Domain.MarketData;
using Trading.Risk;
using Trading.Strategies.Contracts;

namespace Trading.Backtesting.Options;

public enum OptionCandidateRejection
{
    PositionAlreadyOpen = 1,
    NoFollowingUnderlyingBar = 2,
    FollowingBarOutsideSession = 3,
    NoEligibleExpiry = 4,
    NoItmStrike = 5,
    MissingEntryQuote = 6,
    InsufficientVolume = 7,
    InsufficientOpenInterest = 8,
    SpreadTooWide = 9,
    InsufficientRiskOrCapital = 10,
    MissingExitQuote = 11,
    InvalidPremiumLevels = 12
}

public sealed record OptionsBacktestSettings
{
    public decimal InitialCapital { get; init; } = 100_000m;
    public decimal AllowedRiskPerTrade { get; init; } = 750m;
    public decimal MaximumCapitalPerTrade { get; init; } = 100_000m;
    public int? MaximumLots { get; init; }
    public decimal PremiumStopPercent { get; init; } = .20m;
    public decimal RewardRiskMultiple { get; init; } = 3m;
    public long MinimumVolume { get; init; }
    public long MinimumOpenInterest { get; init; }
    public decimal MaximumSpreadBasisPoints { get; init; } = 500m;
    public decimal SlippageBasisPointsPerSide { get; init; }
    public TimeOnly SessionExitTime { get; init; } = new(15, 25);
    public ITradeCostModel? CostModel { get; init; }
}

public sealed record RejectedOptionCandidate(DateTime SignalTimeUtc, OptionCandidateRejection Reason,
    Guid? OptionContractId = null);

public sealed record OptionBacktestTrade(
    string StrategyId,
    Guid UnderlyingInstrumentId,
    TradeDirection UnderlyingDirection,
    Guid OptionContractId,
    string OptionSymbol,
    OptionRight OptionRight,
    DateOnly ExpiryDate,
    decimal Strike,
    DateTime SignalTimeUtc,
    DateTime EntryTimeUtc,
    DateTime ExitTimeUtc,
    int Quantity,
    decimal EntryAsk,
    decimal EntryPrice,
    decimal StopPrice,
    decimal TargetPrice,
    decimal ExitBid,
    decimal ExitPrice,
    BacktestExitReason ExitReason,
    decimal EntrySpreadBasisPoints,
    decimal InitialRisk,
    decimal GrossPnl,
    TradeCostBreakdown CostBreakdown,
    decimal Costs,
    decimal NetPnl,
    decimal NetRMultiple,
    decimal CapitalAfterTrade);

public sealed record OptionsBacktestResult(decimal InitialCapital, decimal FinalCapital,
    IReadOnlyList<OptionBacktestTrade> Trades, IReadOnlyList<RejectedOptionCandidate> RejectedCandidates)
{
    public decimal NetPnl => FinalCapital - InitialCapital;
    public int WinningTrades => Trades.Count(trade => trade.NetPnl > 0);
    public int LosingTrades => Trades.Count(trade => trade.NetPnl < 0);
    public decimal TotalNetR => Trades.Sum(trade => trade.NetRMultiple);
    public decimal? AverageNetR => Trades.Count == 0 ? null : TotalNetR / Trades.Count;
}

/// <summary>Replays observed option quotes. It never derives premium from the underlying.</summary>
public static class OptionsBacktestEngine
{
    public static OptionsBacktestResult Run(ITradingStrategy strategy, IReadOnlyList<Candle> underlyingCandles,
        IReadOnlyList<OptionContract> contracts, IReadOnlyList<OptionQuote> quotes,
        TimeZoneInfo exchangeTimeZone, OptionsBacktestSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(underlyingCandles);
        ArgumentNullException.ThrowIfNull(contracts);
        ArgumentNullException.ThrowIfNull(quotes);
        ArgumentNullException.ThrowIfNull(exchangeTimeZone);
        settings ??= new();
        Validate(underlyingCandles, contracts, quotes, exchangeTimeZone, settings);
        var signals = strategy.Evaluate(underlyingCandles, exchangeTimeZone);
        ValidateSignals(strategy, underlyingCandles, signals);
        var candleIndex = underlyingCandles.Select((candle, index) => (candle.OpenTimeUtc, index))
            .ToDictionary(item => item.OpenTimeUtc, item => item.index);
        var quoteIndex = quotes.ToDictionary(quote => (quote.OptionContractId, quote.TimestampUtc));
        var quotesByContract = quotes.GroupBy(quote => quote.OptionContractId)
            .ToDictionary(group => group.Key, group => group.OrderBy(quote => quote.TimestampUtc).ToArray());
        var trades = new List<OptionBacktestTrade>();
        var rejected = new List<RejectedOptionCandidate>();
        var capital = settings.InitialCapital;
        DateTime? occupiedUntil = null;

        foreach (var signal in signals)
        {
            if (!candleIndex.TryGetValue(signal.OpenTimeUtc, out var index) || index == underlyingCandles.Count - 1)
            {
                rejected.Add(new(signal.OpenTimeUtc, OptionCandidateRejection.NoFollowingUnderlyingBar));
                continue;
            }
            if (occupiedUntil is not null && signal.OpenTimeUtc < occupiedUntil)
            {
                rejected.Add(new(signal.OpenTimeUtc, OptionCandidateRejection.PositionAlreadyOpen));
                continue;
            }
            var entryCandle = underlyingCandles[index + 1];
            var signalSession = Session(signal.OpenTimeUtc, exchangeTimeZone);
            if (Session(entryCandle.OpenTimeUtc, exchangeTimeZone) != signalSession ||
                Clock(entryCandle.OpenTimeUtc, exchangeTimeZone) >= settings.SessionExitTime)
            {
                rejected.Add(new(signal.OpenTimeUtc, OptionCandidateRejection.FollowingBarOutsideSession));
                continue;
            }
            var right = signal.Direction == TradeDirection.Long ? OptionRight.Call : OptionRight.Put;
            var expiry = contracts.Where(contract => contract.Right == right && contract.ExpiryDate >= signalSession)
                .Select(contract => contract.ExpiryDate).DefaultIfEmpty().Min();
            if (expiry == default)
            {
                rejected.Add(new(signal.OpenTimeUtc, OptionCandidateRejection.NoEligibleExpiry));
                continue;
            }
            var expiryContracts = contracts.Where(contract => contract.Right == right && contract.ExpiryDate == expiry);
            var contract = right == OptionRight.Call
                ? expiryContracts.Where(item => item.Strike < entryCandle.Open).OrderByDescending(item => item.Strike).FirstOrDefault()
                : expiryContracts.Where(item => item.Strike > entryCandle.Open).OrderBy(item => item.Strike).FirstOrDefault();
            if (contract is null)
            {
                rejected.Add(new(signal.OpenTimeUtc, OptionCandidateRejection.NoItmStrike));
                continue;
            }
            if (!quoteIndex.TryGetValue((contract.Id, entryCandle.OpenTimeUtc), out var entryQuote))
            {
                rejected.Add(new(signal.OpenTimeUtc, OptionCandidateRejection.MissingEntryQuote, contract.Id));
                continue;
            }
            if (entryQuote.Volume < settings.MinimumVolume)
            {
                rejected.Add(new(signal.OpenTimeUtc, OptionCandidateRejection.InsufficientVolume, contract.Id));
                continue;
            }
            if (entryQuote.OpenInterest < settings.MinimumOpenInterest)
            {
                rejected.Add(new(signal.OpenTimeUtc, OptionCandidateRejection.InsufficientOpenInterest, contract.Id));
                continue;
            }
            if (entryQuote.SpreadBasisPoints > settings.MaximumSpreadBasisPoints)
            {
                rejected.Add(new(signal.OpenTimeUtc, OptionCandidateRejection.SpreadTooWide, contract.Id));
                continue;
            }
            var entry = RoundUp(entryQuote.Ask * (1m + settings.SlippageBasisPointsPerSide / 10_000m), contract.TickSize);
            var riskPerUnit = entry * settings.PremiumStopPercent;
            var stop = RoundDown(entry - riskPerUnit, contract.TickSize);
            var target = RoundUp(entry + riskPerUnit * settings.RewardRiskMultiple, contract.TickSize);
            if (stop <= 0 || target <= entry)
            {
                rejected.Add(new(signal.OpenTimeUtc, OptionCandidateRejection.InvalidPremiumLevels, contract.Id));
                continue;
            }
            var size = PositionSizer.Calculate(new(settings.AllowedRiskPerTrade, entry, stop, contract.LotSize,
                settings.MaximumCapitalPerTrade, settings.MaximumLots));
            if (size.Quantity == 0)
            {
                rejected.Add(new(signal.OpenTimeUtc, OptionCandidateRejection.InsufficientRiskOrCapital, contract.Id));
                continue;
            }
            var future = quotesByContract[contract.Id].Where(quote => quote.TimestampUtc > entryQuote.TimestampUtc &&
                quote.TimestampUtc <= underlyingCandles[^1].OpenTimeUtc &&
                Session(quote.TimestampUtc, exchangeTimeZone) == signalSession).ToArray();
            if (future.Length == 0)
            {
                rejected.Add(new(signal.OpenTimeUtc, OptionCandidateRejection.MissingExitQuote, contract.Id));
                continue;
            }
            var exitQuote = future.FirstOrDefault(quote =>
                Clock(quote.TimestampUtc, exchangeTimeZone) >= settings.SessionExitTime || quote.Bid <= stop || quote.Bid >= target)
                ?? future[^1];
            var exitReason = Clock(exitQuote.TimestampUtc, exchangeTimeZone) >= settings.SessionExitTime
                    ? BacktestExitReason.SessionExit
                    : exitQuote.Bid <= stop ? BacktestExitReason.StopLoss
                    : exitQuote.Bid >= target ? BacktestExitReason.Target : BacktestExitReason.EndOfData;
            var exit = decimal.Max(contract.TickSize,
                RoundDown(exitQuote.Bid * (1m - settings.SlippageBasisPointsPerSide / 10_000m), contract.TickSize));
            var gross = (exit - entry) * size.Quantity;
            var initialRisk = (entry - stop) * size.Quantity;
            var breakdown = settings.CostModel?.Calculate(new(entry, exit, size.Quantity, TradeDirection.Long))
                ?? TradeCostBreakdown.None;
            var costs = breakdown.Total;
            var net = gross - costs;
            capital += net;
            trades.Add(new(strategy.Id, signal.InstrumentId, signal.Direction, contract.Id, contract.Symbol,
                right, contract.ExpiryDate, contract.Strike, signal.OpenTimeUtc, entryQuote.TimestampUtc,
                exitQuote.TimestampUtc, size.Quantity, entryQuote.Ask, entry, stop, target, exitQuote.Bid,
                exit, exitReason, entryQuote.SpreadBasisPoints, initialRisk, gross, breakdown, costs, net,
                net / initialRisk, capital));
            occupiedUntil = exitQuote.TimestampUtc;
        }

        return new(settings.InitialCapital, capital, trades.AsReadOnly(), rejected.AsReadOnly());
    }

    private static void Validate(IReadOnlyList<Candle> candles, IReadOnlyList<OptionContract> contracts,
        IReadOnlyList<OptionQuote> quotes, TimeZoneInfo exchangeTimeZone, OptionsBacktestSettings settings)
    {
        if (candles.Count == 0) throw new ArgumentException("Underlying candles are required.", nameof(candles));
        if (settings.InitialCapital <= 0 || settings.AllowedRiskPerTrade <= 0 || settings.MaximumCapitalPerTrade <= 0 ||
            settings.MaximumLots is <= 0 || settings.PremiumStopPercent is <= 0 or >= 1 ||
            settings.RewardRiskMultiple <= 0 || settings.MinimumVolume < 0 || settings.MinimumOpenInterest < 0 ||
            settings.MaximumSpreadBasisPoints < 0 || settings.SlippageBasisPointsPerSide is < 0 or >= 10_000)
            throw new ArgumentException("Options backtest settings are invalid.", nameof(settings));
        for (var index = 1; index < candles.Count; index++)
        {
            if (candles[index].InstrumentId != candles[0].InstrumentId || candles[index].Timeframe != candles[0].Timeframe)
                throw new ArgumentException("Underlying candles must share an instrument and timeframe.", nameof(candles));
            if (candles[index].OpenTimeUtc <= candles[index - 1].OpenTimeUtc)
                throw new ArgumentException("Underlying candles must be chronological.", nameof(candles));
        }
        if (contracts.Any(contract => contract.UnderlyingInstrumentId != candles[0].InstrumentId) ||
            contracts.Select(contract => contract.Id).Distinct().Count() != contracts.Count)
            throw new ArgumentException("Contracts must be unique and belong to the underlying.", nameof(contracts));
        var ids = contracts.Select(contract => contract.Id).ToHashSet();
        if (quotes.Any(quote => !ids.Contains(quote.OptionContractId)) ||
            quotes.Select(quote => (quote.OptionContractId, quote.TimestampUtc)).Distinct().Count() != quotes.Count)
            throw new ArgumentException("Quotes must be unique and match the contract universe.", nameof(quotes));
        var expiryByContract = contracts.ToDictionary(contract => contract.Id, contract => contract.ExpiryDate);
        if (quotes.Any(quote => Session(quote.TimestampUtc, exchangeTimeZone) > expiryByContract[quote.OptionContractId]))
            throw new ArgumentException("Quotes after contract expiry are invalid.", nameof(quotes));
    }

    private static void ValidateSignals(ITradingStrategy strategy, IReadOnlyList<Candle> candles,
        IReadOnlyList<StrategySignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        var times = candles.Select(candle => candle.OpenTimeUtc).ToHashSet();
        DateTime? previous = null;
        foreach (var signal in signals)
        {
            if (signal.StrategyId != strategy.Id || signal.InstrumentId != candles[0].InstrumentId ||
                !times.Contains(signal.OpenTimeUtc) || !Enum.IsDefined(signal.Direction))
                throw new InvalidOperationException("Strategy returned an invalid or unmatched option candidate.");
            if (previous is not null && signal.OpenTimeUtc <= previous)
                throw new InvalidOperationException("Strategy candidates must be unique and chronological.");
            previous = signal.OpenTimeUtc;
        }
    }

    private static DateOnly Session(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));
    private static TimeOnly Clock(DateTime utc, TimeZoneInfo zone) =>
        TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));
    private static decimal RoundUp(decimal value, decimal tick) => decimal.Ceiling(value / tick) * tick;
    private static decimal RoundDown(decimal value, decimal tick) => decimal.Floor(value / tick) * tick;
}
