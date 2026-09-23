using Trading.Application.Backtesting;

namespace Trading.Backtesting.Comparison;

public enum CrossEngineVerdict { Pass = 1, Divergent = 2, NoTrades = 3 }

public sealed record CrossEngineComparisonPolicy(
    int EntryMatchWindowSeconds = 300,
    int TimestampToleranceSeconds = 0,
    decimal PriceToleranceBasisPoints = 1m,
    decimal MinimumMatchedTradeRate = 1m,
    decimal MaximumNetPnlDeviationPercent = 1m,
    decimal MaximumDrawdownDeviationPercentagePoints = .5m);

public sealed record CrossEngineTradeComparison(
    int? NativeIndex,
    int? LeanIndex,
    BacktestRunTrade? NativeTrade,
    BacktestRunTrade? LeanTrade,
    decimal? EntryTimeOffsetSeconds,
    decimal? ExitTimeOffsetSeconds,
    decimal? EntryPriceDeviationBasisPoints,
    decimal? ExitPriceDeviationBasisPoints,
    decimal? NetPnlDifference,
    IReadOnlyList<string> Differences);

public sealed record CrossEngineComparison(
    int SchemaVersion,
    string SpecificationSha256,
    string DatasetSha256,
    string ConsumedMarketDataSha256,
    string NativeEngineId,
    string NativeEngineVersion,
    string NativeResultSha256,
    string LeanEngineId,
    string LeanEngineVersion,
    string LeanResultSha256,
    CrossEngineComparisonPolicy Policy,
    CrossEngineVerdict Verdict,
    int NativeTradeCount,
    int LeanTradeCount,
    int MatchedTradeCount,
    decimal MatchedTradeRate,
    decimal EntryTimestampMatchRate,
    decimal ExitTimestampMatchRate,
    decimal MaximumEntryPriceDeviationBasisPoints,
    decimal MaximumExitPriceDeviationBasisPoints,
    decimal NativeNetPnl,
    decimal LeanNetPnl,
    decimal NetPnlDeviationPercent,
    decimal NativeClosedTradeMaxDrawdownPercent,
    decimal LeanClosedTradeMaxDrawdownPercent,
    decimal DrawdownDeviationPercentagePoints,
    IReadOnlyList<CrossEngineTradeComparison> Trades,
    string ComparisonSha256);
