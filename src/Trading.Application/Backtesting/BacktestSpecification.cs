namespace Trading.Application.Backtesting;

public enum BacktestAssetClass { Equity = 1, EquityIndex = 2, IndexOption = 3 }
public enum BacktestMarketDataMode { OhlcvBars = 1, ObservedOptionQuotes = 2 }
public enum BacktestSignalTiming { CompletedBar = 1 }
public enum BacktestEntryFillPolicy { NextObservedBarOpen = 1, ObservedAsk = 2 }
public enum BacktestAmbiguousBarPolicy { StopFirst = 1 }
public enum BacktestEndOfDataPolicy { CloseLastObserved = 1 }

public sealed record BacktestInstrumentSpecification(
    Guid InstrumentId,
    string Exchange,
    string TradingSymbol,
    BacktestAssetClass AssetClass,
    string Currency,
    int LotSize,
    decimal TickSize);

public sealed record BacktestDataSpecification(
    DateTime FromUtc,
    DateTime ToUtc,
    int TimeframeMinutes,
    string ExchangeTimeZoneId,
    string CalendarId,
    string Source,
    string Version,
    string DatasetSha256,
    BacktestMarketDataMode MarketDataMode);

public sealed record BacktestCapitalSpecification(
    decimal InitialCapital,
    decimal RiskPerTrade,
    decimal MaximumCapitalPerTrade,
    int? MaximumLots);

public sealed record BacktestExecutionSpecification(
    decimal RewardRiskMultiple,
    decimal SlippageBasisPointsPerSide,
    string CostProfileId,
    TimeOnly SessionExitTime,
    BacktestSignalTiming SignalTiming,
    BacktestEntryFillPolicy EntryFillPolicy,
    BacktestAmbiguousBarPolicy AmbiguousBarPolicy,
    BacktestEndOfDataPolicy EndOfDataPolicy);

/// <summary>
/// Versioned, engine-neutral input shared by native and external research engines.
/// The range is half-open: FromUtc is inclusive and ToUtc is exclusive.
/// </summary>
public sealed record BacktestSpecification(
    int SchemaVersion,
    string StrategyId,
    BacktestInstrumentSpecification Instrument,
    BacktestDataSpecification Data,
    BacktestCapitalSpecification Capital,
    BacktestExecutionSpecification Execution,
    IReadOnlyDictionary<string, decimal> Parameters);

public sealed record SealedBacktestSpecification(
    BacktestSpecification Specification,
    string SpecificationSha256);
