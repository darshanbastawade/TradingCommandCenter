using Trading.Strategies.Contracts;
using Trading.Backtesting.Costs;

namespace Trading.Backtesting;

public enum BacktestExitReason
{
    StopLoss = 1,
    Target = 2,
    SessionExit = 3,
    EndOfData = 4
}

public sealed record BacktestTrade(
    string StrategyId,
    Guid InstrumentId,
    TradeDirection Direction,
    DateTime SignalBarOpenTimeUtc,
    DateTime EntryBarOpenTimeUtc,
    DateTime ExitBarOpenTimeUtc,
    int Quantity,
    decimal EntryPrice,
    decimal StopPrice,
    decimal TargetPrice,
    decimal ExitPrice,
    BacktestExitReason ExitReason,
    decimal GrossPnl,
    TradeCostBreakdown CostBreakdown,
    decimal Costs,
    decimal NetPnl,
    decimal CapitalAfterTrade);
