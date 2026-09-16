using Trading.Backtesting.Metrics;
using Trading.Domain.MarketData;

namespace Trading.Backtesting.Reporting;

public sealed record TradeOutcomeReport(
    decimal? TotalNetR,
    decimal? MedianNetR,
    decimal? MaximumDrawdownR,
    decimal? AverageDurationMinutes,
    decimal? MedianDurationMinutes,
    decimal? TargetExitRate,
    decimal? StopLossExitRate,
    decimal? SessionExitRate,
    decimal? EndOfDataExitRate);

public sealed record PerformanceBreakdown(
    string Key,
    int TotalTrades,
    int WinningTrades,
    int LosingTrades,
    int BreakEvenTrades,
    decimal NetPnl,
    decimal? WinRate,
    decimal? AverageNetR);

public sealed record BacktestReport(
    int SchemaVersion,
    IReadOnlyList<string> StrategyIds,
    Guid? InstrumentId,
    Timeframe? Timeframe,
    DateOnly? StartSession,
    DateOnly? EndSession,
    BacktestMetrics Summary,
    TradeOutcomeReport Outcomes,
    IReadOnlyList<PerformanceBreakdown> Monthly,
    IReadOnlyList<PerformanceBreakdown> Yearly,
    IReadOnlyList<PerformanceBreakdown> Weekdays,
    IReadOnlyList<PerformanceBreakdown> TimeBuckets);
