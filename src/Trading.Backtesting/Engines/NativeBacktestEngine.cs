using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Trading.Application.Backtesting;
using Trading.Application.MarketData;
using Trading.Backtesting.Costs;
using Trading.Domain.MarketData;
using Trading.Strategies.Contracts;

namespace Trading.Backtesting.Engines;

public sealed class NativeBacktestEngine(IMarketDataStore marketData) : IBacktestEngine
{
    public const string Id = "native-csharp";
    public const string Version = "1";
    public const BacktestEngineRole EngineRole = BacktestEngineRole.Authoritative;

    public string EngineId => Id;
    public string EngineVersion => Version;
    public BacktestEngineRole Role => EngineRole;

    public async Task<BacktestRun> RunAsync(SealedBacktestSpecification sealedSpecification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sealedSpecification);
        if (!BacktestSpecificationCodec.Verify(sealedSpecification))
            throw new ArgumentException("The sealed backtest specification hash is invalid.", nameof(sealedSpecification));
        var specification = BacktestSpecificationCodec.NormalizeAndValidate(sealedSpecification.Specification);
        if (specification.Data.MarketDataMode != BacktestMarketDataMode.OhlcvBars ||
            specification.Instrument.AssetClass == BacktestAssetClass.IndexOption)
            throw new NotSupportedException("Native engine v1 supports OHLCV equity and equity-index specifications only.");

        var instrument = await marketData.FindInstrumentAsync(specification.Instrument.InstrumentId, cancellationToken) ??
            throw new ArgumentException("The specification instrument is not registered.", nameof(sealedSpecification));
        if (!string.Equals(instrument.Exchange, specification.Instrument.Exchange, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(instrument.Symbol, specification.Instrument.TradingSymbol, StringComparison.OrdinalIgnoreCase) ||
            instrument.LotSize != specification.Instrument.LotSize || instrument.TickSize != specification.Instrument.TickSize)
            throw new InvalidOperationException("Registered instrument metadata does not match the specification.");

        var timeframe = (Timeframe)specification.Data.TimeframeMinutes;
        var candles = await ReadAllAsync(instrument.Id, timeframe, specification.Data.FromUtc,
            specification.Data.ToUtc, cancellationToken);
        if (candles.Count == 0) throw new InvalidOperationException("No candles exist for the specification range.");
        cancellationToken.ThrowIfCancellationRequested();
        var strategy = NativeStrategyFactory.Create(specification.StrategyId, specification.Parameters,
            specification.Execution.RewardRiskMultiple);
        var result = BacktestEngine.Run(strategy, candles,
            TimeZoneInfo.FindSystemTimeZoneById(specification.Data.ExchangeTimeZoneId), Settings(specification));
        cancellationToken.ThrowIfCancellationRequested();

        var mappedTrades = result.Trades.Select(Map).ToArray();
        var netPnl = mappedTrades.Sum(item => item.NetPnl);
        var run = new BacktestRun(1, EngineId, EngineVersion, Role, sealedSpecification.SpecificationSha256,
            specification.Data.DatasetSha256, Fingerprint(specification, candles), result.InitialCapital,
            result.InitialCapital + netPnl, netPnl, result.WinningTrades, result.LosingTrades,
            mappedTrades,
            result.IgnoredCandidates.Select(item => new BacktestRunIgnoredCandidate(item.SignalTimeUtc,
                Kebab(item.Reason.ToString()))).ToArray(), string.Empty);
        return BacktestRunCodec.Seal(run);
    }

    private BacktestSettings Settings(BacktestSpecification specification) => new()
    {
        InitialCapital = specification.Capital.InitialCapital,
        Quantity = specification.Instrument.LotSize,
        SlippageBasisPointsPerSide = specification.Execution.SlippageBasisPointsPerSide,
        CostModel = CostModel(specification.Execution.CostProfileId, specification.Instrument.AssetClass),
        RiskBasedSizing = new(specification.Capital.RiskPerTrade, specification.Instrument.LotSize,
            specification.Capital.MaximumCapitalPerTrade, specification.Capital.MaximumLots),
        SessionExitTime = specification.Execution.SessionExitTime
    };

    private static ITradeCostModel? CostModel(string id, BacktestAssetClass assetClass) => (id, assetClass) switch
    {
        ("none", _) => null,
        ("zerodha-nse-equity-intraday-2026-03-01", BacktestAssetClass.Equity or BacktestAssetClass.EquityIndex) =>
            IndianCostProfiles.ZerodhaNseEquityIntraday2026(),
        _ => throw new ArgumentException($"Native engine does not support cost profile '{id}'.", nameof(id))
    };

    private async Task<IReadOnlyList<Candle>> ReadAllAsync(Guid instrumentId, Timeframe timeframe,
        DateTime fromUtc, DateTime toUtc, CancellationToken token)
    {
        var result = new List<Candle>();
        var cursor = new DateTimeOffset(fromUtc);
        var end = new DateTimeOffset(toUtc);
        while (cursor < end)
        {
            var page = await marketData.ReadCandlesAsync(instrumentId, timeframe, cursor, end, 10_000, token);
            result.AddRange(page);
            if (page.Count < 10_000) break;
            cursor = new DateTimeOffset(page[^1].OpenTimeUtc.AddTicks(1));
        }
        return result.AsReadOnly();
    }

    private static BacktestRunTrade Map(BacktestTrade trade) => new(trade.StrategyId, trade.InstrumentId,
        trade.Direction == TradeDirection.Long ? BacktestRunTradeDirection.Long : BacktestRunTradeDirection.Short,
        trade.SignalBarOpenTimeUtc, trade.EntryBarOpenTimeUtc, trade.ExitBarOpenTimeUtc, trade.Quantity,
        trade.EntryPrice, trade.StopPrice, trade.TargetPrice, trade.ExitPrice, Kebab(trade.ExitReason.ToString()),
        trade.GrossPnl, trade.Costs, trade.NetPnl, trade.CapitalAfterTrade);

    private static string Fingerprint(BacktestSpecification specification, IEnumerable<Candle> candles)
    {
        var builder = new StringBuilder();
        builder.Append(specification.Instrument.InstrumentId).Append('|')
            .Append(specification.Data.TimeframeMinutes).Append('|')
            .Append(specification.Data.FromUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
            .Append(specification.Data.ToUtc.ToString("O", CultureInfo.InvariantCulture)).AppendLine();
        foreach (var candle in candles)
            builder.Append(candle.OpenTimeUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                .Append(Invariant(candle.Open)).Append('|').Append(Invariant(candle.High)).Append('|')
                .Append(Invariant(candle.Low)).Append('|').Append(Invariant(candle.Close)).Append('|')
                .Append(candle.Volume.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(candle.OpenInterest?.ToString(CultureInfo.InvariantCulture) ?? "null").AppendLine();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string Invariant(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Kebab(string value)
    {
        var builder = new StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            if (index > 0 && char.IsUpper(value[index])) builder.Append('-');
            builder.Append(char.ToLowerInvariant(value[index]));
        }
        return builder.ToString();
    }
}
