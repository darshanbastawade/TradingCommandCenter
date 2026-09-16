namespace Trading.Backtesting.Metrics;

public sealed record PerformanceMetricSettings
{
    public int PeriodsPerYear { get; init; } = 252;
    public decimal PeriodicRiskFreeRate { get; init; }
    public decimal PeriodicMinimumAcceptableReturn { get; init; }
}
