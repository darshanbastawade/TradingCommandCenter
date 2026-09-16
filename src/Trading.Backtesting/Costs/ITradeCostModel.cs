using Trading.Strategies.Contracts;

namespace Trading.Backtesting.Costs;

public sealed record TradeCostRequest(decimal EntryPrice, decimal ExitPrice, int Quantity, TradeDirection Direction);

public interface ITradeCostModel
{
    string Id { get; }
    TradeCostBreakdown Calculate(TradeCostRequest request);
}
