using Trading.Backtesting.Costs;

namespace Trading.Backtesting;

public sealed record RiskBasedSizingSettings(
    decimal AllowedRiskPerTrade,
    int LotSize,
    decimal MaximumCapitalPerTrade,
    int? MaximumLots = null);

public sealed record BacktestSettings
{
    public decimal InitialCapital { get; init; } = 100_000m;
    public int Quantity { get; init; } = 1;
    public decimal SlippageBasisPointsPerSide { get; init; }
    public decimal VariableCostBasisPointsPerSide { get; init; }
    public decimal FixedCostPerSide { get; init; }
    public ITradeCostModel? CostModel { get; init; }
    public RiskBasedSizingSettings? RiskBasedSizing { get; init; }
    public TimeOnly SessionExitTime { get; init; } = new(15, 25);
}
