using Trading.Backtesting.Metrics;
using Trading.Domain.MarketData;

namespace Trading.Backtesting.Robustness;

public sealed record ParameterScenario<TParameters>(
    string Id,
    TParameters Parameters,
    IReadOnlyDictionary<string, decimal> Values,
    bool IsBaseline = false);

public sealed record ParameterScenarioResult(
    string Id,
    bool IsBaseline,
    IReadOnlyDictionary<string, decimal> Values,
    BacktestMetrics Metrics,
    decimal? ExpectancyChangeFromBaseline,
    bool MeetsDegradationLimit);

public sealed record ParameterRobustnessSettings
{
    public int MaximumScenarioCount { get; init; } = 500;
    public decimal AllowedExpectancyDegradationFraction { get; init; } = .50m;
}

public sealed record ParameterRobustnessReport(
    int SchemaVersion,
    string BaselineScenarioId,
    int ScenarioCount,
    decimal ProfitableScenarioRate,
    decimal AcceptableDegradationRate,
    decimal? WorstExpectancy,
    decimal? MedianExpectancy,
    decimal? BestExpectancy,
    decimal StabilityScore,
    IReadOnlyList<ParameterScenarioResult> Scenarios);

/// <summary>Evaluates a declared parameter neighborhood. It does not choose or optimize parameters.</summary>
public static class ParameterRobustnessAnalyzer
{
    public static ParameterRobustnessReport Analyze<TParameters>(
        IReadOnlyList<ParameterScenario<TParameters>> scenarios,
        Func<TParameters, BacktestResult> evaluator,
        IReadOnlyList<Candle> evaluationCandles,
        TimeZoneInfo exchangeTimeZone,
        ParameterRobustnessSettings? settings = null,
        PerformanceMetricSettings? metricSettings = null)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(evaluationCandles);
        ArgumentNullException.ThrowIfNull(exchangeTimeZone);
        settings ??= new();
        Validate(scenarios, settings);

        var evaluated = scenarios.Select(scenario => (Scenario: scenario, Metrics:
            BacktestMetricsCalculator.Calculate(evaluator(scenario.Parameters), evaluationCandles,
                exchangeTimeZone, metricSettings))).ToArray();
        var baseline = evaluated.Single(item => item.Scenario.IsBaseline);
        var baselineExpectancy = baseline.Metrics.ExpectancyPerTrade;
        var floor = baselineExpectancy is null ? (decimal?)null
            : baselineExpectancy.Value - decimal.Abs(baselineExpectancy.Value) *
                settings.AllowedExpectancyDegradationFraction;

        var results = evaluated.Select(item =>
        {
            var expectancy = item.Metrics.ExpectancyPerTrade;
            var acceptable = expectancy is not null && floor is not null && expectancy >= floor;
            return new ParameterScenarioResult(item.Scenario.Id, item.Scenario.IsBaseline,
                item.Scenario.Values, item.Metrics,
                expectancy is not null && baselineExpectancy is not null ? expectancy - baselineExpectancy : null,
                acceptable);
        }).ToArray();
        var profitableRate = (decimal)results.Count(item => item.Metrics.NetPnl > 0) / results.Length;
        var acceptableRate = (decimal)results.Count(item => item.MeetsDegradationLimit) / results.Length;
        var expectancies = results.Where(item => item.Metrics.ExpectancyPerTrade is not null)
            .Select(item => item.Metrics.ExpectancyPerTrade!.Value).Order().ToArray();
        var stabilityScore = decimal.Round((profitableRate * 50m) + (acceptableRate * 50m), 2,
            MidpointRounding.AwayFromZero);
        return new(1, baseline.Scenario.Id, results.Length, profitableRate, acceptableRate,
            expectancies.Length == 0 ? null : expectancies[0], Median(expectancies),
            expectancies.Length == 0 ? null : expectancies[^1], stabilityScore, Array.AsReadOnly(results));
    }

    private static void Validate<TParameters>(IReadOnlyList<ParameterScenario<TParameters>> scenarios,
        ParameterRobustnessSettings settings)
    {
        if (settings.MaximumScenarioCount < 1 || settings.AllowedExpectancyDegradationFraction is < 0 or > 1)
            throw new ArgumentException("Robustness settings are invalid.", nameof(settings));
        if (scenarios.Count is 0 || scenarios.Count > 500 || scenarios.Count > settings.MaximumScenarioCount)
            throw new ArgumentException("The parameter neighborhood is empty or exceeds its scenario limit.", nameof(scenarios));
        if (scenarios.Count(item => item.IsBaseline) != 1)
            throw new ArgumentException("Exactly one parameter scenario must be the baseline.", nameof(scenarios));
        if (scenarios.Any(item => string.IsNullOrWhiteSpace(item.Id) || item.Values is null) ||
            scenarios.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != scenarios.Count)
            throw new ArgumentException("Parameter scenarios require unique stable IDs and values.", nameof(scenarios));
    }

    private static decimal? Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return null;
        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2m;
    }
}
