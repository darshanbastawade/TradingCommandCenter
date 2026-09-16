using Trading.Backtesting.Metrics;
using Trading.Domain.MarketData;

namespace Trading.Backtesting.Robustness;

public sealed record BestTradeRemovalResult(
    decimal RemovedPercent,
    int RemovedTradeCount,
    BacktestMetrics RemainingMetrics);

public sealed record BestTradeRemovalReport(
    int SchemaVersion,
    int OriginalTradeCount,
    IReadOnlyList<BestTradeRemovalResult> Scenarios);

public sealed record ExecutionStressScenario(
    string Id,
    decimal SlippageBasisPointsPerSide,
    decimal AdditionalFixedCostPerSide,
    bool IsBaseline = false);

public sealed record ExecutionStressResult(
    string Id,
    bool IsBaseline,
    decimal SlippageBasisPointsPerSide,
    decimal AdditionalFixedCostPerSide,
    BacktestMetrics Metrics);

public sealed record ExecutionStressReport(
    int SchemaVersion,
    IReadOnlyList<ExecutionStressResult> Scenarios);

public static class ResearchStressAnalyzer
{
    public static BestTradeRemovalReport RemoveBestTrades(BacktestResult result,
        IReadOnlyList<Candle> candles, TimeZoneInfo exchangeTimeZone,
        IReadOnlyList<decimal>? percentages = null, PerformanceMetricSettings? metricSettings = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(exchangeTimeZone);
        percentages ??= [1m, 3m, 5m];
        if (percentages.Count == 0 || percentages.Any(value => value is <= 0 or >= 100) ||
            percentages.Distinct().Count() != percentages.Count)
            throw new ArgumentException("Removal percentages must be unique values between zero and 100.", nameof(percentages));
        var scenarios = percentages.Order().Select(percent =>
        {
            var removeCount = result.Trades.Count == 0 ? 0
                : (int)decimal.Ceiling(result.Trades.Count * percent / 100m);
            var removed = result.Trades.OrderByDescending(trade => trade.NetPnl)
                .ThenBy(trade => trade.ExitBarOpenTimeUtc).Take(removeCount).ToHashSet();
            var capital = result.InitialCapital;
            var remaining = result.Trades.Where(trade => !removed.Contains(trade))
                .OrderBy(trade => trade.ExitBarOpenTimeUtc).Select(trade =>
                {
                    capital += trade.NetPnl;
                    return trade with { CapitalAfterTrade = capital };
                }).ToArray();
            var stressed = new BacktestResult(result.InitialCapital, capital, Array.AsReadOnly(remaining),
                result.IgnoredCandidates);
            return new BestTradeRemovalResult(percent, removeCount,
                BacktestMetricsCalculator.Calculate(stressed, candles, exchangeTimeZone, metricSettings));
        }).ToArray();
        return new(1, result.Trades.Count, Array.AsReadOnly(scenarios));
    }

    public static ExecutionStressReport ExecutionSensitivity(IReadOnlyList<ExecutionStressScenario> scenarios,
        Func<ExecutionStressScenario, BacktestResult> evaluator, IReadOnlyList<Candle> candles,
        TimeZoneInfo exchangeTimeZone, PerformanceMetricSettings? metricSettings = null)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(evaluator);
        if (scenarios.Count == 0 || scenarios.Count(item => item.IsBaseline) != 1 ||
            scenarios.Any(item => string.IsNullOrWhiteSpace(item.Id) || item.SlippageBasisPointsPerSide < 0 ||
                item.AdditionalFixedCostPerSide < 0) ||
            scenarios.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != scenarios.Count)
            throw new ArgumentException("Execution stress scenarios are invalid.", nameof(scenarios));
        var results = scenarios.Select(scenario => new ExecutionStressResult(scenario.Id, scenario.IsBaseline,
            scenario.SlippageBasisPointsPerSide, scenario.AdditionalFixedCostPerSide,
            BacktestMetricsCalculator.Calculate(evaluator(scenario), candles, exchangeTimeZone, metricSettings))).ToArray();
        return new(1, Array.AsReadOnly(results));
    }
}
