using Trading.Backtesting.Metrics;

namespace Trading.Backtesting.Ranking;

public sealed record StrategyEvaluation(
    string StrategyId,
    BacktestMetrics OutOfSampleMetrics,
    decimal WalkForwardProfitableFoldRate,
    decimal ParameterStabilityScore,
    decimal ProfitableRegimeRate);

public sealed record StrategyQualificationSettings
{
    public int MinimumTrades { get; init; } = 30;
    public decimal MinimumAverageNetR { get; init; } = .10m;
    public decimal MinimumProfitFactor { get; init; } = 1.20m;
    public decimal MaximumDrawdownPercent { get; init; } = .20m;
    public decimal MinimumWalkForwardProfitableFoldRate { get; init; } = .60m;
    public decimal MinimumParameterStabilityScore { get; init; } = 70m;
    public decimal MinimumProfitableRegimeRate { get; init; } = .50m;
    public int MaximumSelections { get; init; } = 2;
}

public sealed record StrategyScoreWeights
{
    public decimal AverageNetR { get; init; } = 20m;
    public decimal ProfitFactor { get; init; } = 15m;
    public decimal Drawdown { get; init; } = 15m;
    public decimal WalkForward { get; init; } = 20m;
    public decimal ParameterRobustness { get; init; } = 15m;
    public decimal RegimeBreadth { get; init; } = 10m;
    public decimal SampleSize { get; init; } = 5m;
}

public sealed record StrategyScoreScales
{
    public decimal FullCreditAverageNetR { get; init; } = .50m;
    public decimal FullCreditProfitFactor { get; init; } = 2m;
    public int FullCreditTradeCount { get; init; } = 60;
}

public sealed record StrategyRankingSettings
{
    public StrategyQualificationSettings Qualification { get; init; } = new();
    public StrategyScoreWeights Weights { get; init; } = new();
    public StrategyScoreScales Scales { get; init; } = new();
}

public sealed record StrategyScore(
    int Rank,
    string StrategyId,
    decimal Score,
    bool Qualified,
    IReadOnlyList<string> QualificationFailures,
    decimal EdgeScore,
    decimal ProfitFactorScore,
    decimal DrawdownScore,
    decimal WalkForwardScore,
    decimal RobustnessScore,
    decimal RegimeScore,
    decimal SampleSizeScore);

public sealed record StrategyRankingResult(
    int SchemaVersion,
    IReadOnlyList<StrategyScore> Rankings,
    IReadOnlyList<StrategyScore> SelectedStrategies);

/// <summary>Ranks supplied evidence and applies hard gates. It never authorizes trading.</summary>
public static class StrategyRankingEngine
{
    public static StrategyRankingResult Rank(IReadOnlyList<StrategyEvaluation> evaluations,
        StrategyQualificationSettings? settings = null)
        => Rank(evaluations, new StrategyRankingSettings { Qualification = settings ?? new() });

    public static StrategyRankingResult Rank(IReadOnlyList<StrategyEvaluation> evaluations,
        StrategyRankingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(evaluations);
        ArgumentNullException.ThrowIfNull(settings);
        Validate(evaluations, settings);
        var unordered = evaluations.Select(evaluation => Score(evaluation, settings)).ToArray();
        var ranked = unordered.OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Qualified)
            .ThenBy(item => item.StrategyId, StringComparer.Ordinal)
            .Select((item, index) => item with { Rank = index + 1 }).ToArray();
        var selected = ranked.Where(item => item.Qualified).Take(settings.Qualification.MaximumSelections).ToArray();
        return new(1, Array.AsReadOnly(ranked), Array.AsReadOnly(selected));
    }

    private static StrategyScore Score(StrategyEvaluation evaluation, StrategyRankingSettings settings)
    {
        var metrics = evaluation.OutOfSampleMetrics;
        var qualification = settings.Qualification;
        var weights = settings.Weights;
        var scales = settings.Scales;
        var failures = new List<string>();
        if (metrics.TotalTrades < qualification.MinimumTrades) failures.Add("insufficient-trades");
        if (metrics.AverageNetRMultiple is null || metrics.AverageNetRMultiple < qualification.MinimumAverageNetR)
            failures.Add("average-net-r-below-minimum");
        if (metrics.NetProfitFactor is null || metrics.NetProfitFactor < qualification.MinimumProfitFactor)
            failures.Add("profit-factor-below-minimum");
        if (metrics.ClosedEquityMaximumDrawdownPercent is null ||
            metrics.ClosedEquityMaximumDrawdownPercent > qualification.MaximumDrawdownPercent)
            failures.Add("drawdown-above-maximum");
        if (evaluation.WalkForwardProfitableFoldRate < qualification.MinimumWalkForwardProfitableFoldRate)
            failures.Add("walk-forward-instability");
        if (evaluation.ParameterStabilityScore < qualification.MinimumParameterStabilityScore)
            failures.Add("parameter-instability");
        if (evaluation.ProfitableRegimeRate < qualification.MinimumProfitableRegimeRate)
            failures.Add("regime-concentration");

        var edge = weights.AverageNetR * Normalize(metrics.AverageNetRMultiple ?? 0, 0, scales.FullCreditAverageNetR);
        var profitFactor = weights.ProfitFactor * Normalize(metrics.NetProfitFactor ?? 0, 1m, scales.FullCreditProfitFactor);
        var drawdown = metrics.ClosedEquityMaximumDrawdownPercent is null ? 0
            : weights.Drawdown * (1m - Normalize(metrics.ClosedEquityMaximumDrawdownPercent.Value, 0,
                qualification.MaximumDrawdownPercent));
        var walkForward = weights.WalkForward * Normalize(evaluation.WalkForwardProfitableFoldRate, 0, 1);
        var robustness = weights.ParameterRobustness * Normalize(evaluation.ParameterStabilityScore, 0, 100);
        var regime = weights.RegimeBreadth * Normalize(evaluation.ProfitableRegimeRate, 0, 1);
        var sample = weights.SampleSize * Normalize(metrics.TotalTrades, 0, scales.FullCreditTradeCount);
        var total = decimal.Round(edge + profitFactor + drawdown + walkForward + robustness + regime + sample,
            2, MidpointRounding.AwayFromZero);
        return new(0, evaluation.StrategyId, total, failures.Count == 0, failures.AsReadOnly(),
            edge, profitFactor, drawdown, walkForward, robustness, regime, sample);
    }

    private static decimal Normalize(decimal value, decimal minimum, decimal maximum) =>
        maximum == minimum ? 0 : decimal.Clamp((value - minimum) / (maximum - minimum), 0, 1);

    private static void Validate(IReadOnlyList<StrategyEvaluation> evaluations,
        StrategyRankingSettings settings)
    {
        var qualification = settings.Qualification;
        var weights = settings.Weights;
        var scales = settings.Scales;
        if (evaluations.Count == 0 || evaluations.Any(item => item.OutOfSampleMetrics is null ||
                string.IsNullOrWhiteSpace(item.StrategyId)) ||
            evaluations.Select(item => item.StrategyId).Distinct(StringComparer.Ordinal).Count() != evaluations.Count)
            throw new ArgumentException("Strategy evaluations require unique stable IDs and metrics.", nameof(evaluations));
        if (qualification.MinimumTrades < 1 || qualification.MinimumAverageNetR < 0 || qualification.MinimumProfitFactor < 1 ||
            qualification.MaximumDrawdownPercent <= 0 || qualification.MinimumWalkForwardProfitableFoldRate is < 0 or > 1 ||
            qualification.MinimumParameterStabilityScore is < 0 or > 100 ||
            qualification.MinimumProfitableRegimeRate is < 0 or > 1 || qualification.MaximumSelections < 1)
            throw new ArgumentException("Strategy qualification settings are invalid.", nameof(settings));
        var allWeights = new[] { weights.AverageNetR, weights.ProfitFactor, weights.Drawdown,
            weights.WalkForward, weights.ParameterRobustness, weights.RegimeBreadth, weights.SampleSize };
        if (allWeights.Any(value => value < 0) || allWeights.Sum() != 100m ||
            scales.FullCreditAverageNetR <= 0 || scales.FullCreditProfitFactor <= 1 ||
            scales.FullCreditTradeCount < 1)
            throw new ArgumentException("Strategy score weights must total 100 and score scales must be positive.", nameof(settings));
        if (evaluations.Any(item => item.WalkForwardProfitableFoldRate is < 0 or > 1 ||
                item.ParameterStabilityScore is < 0 or > 100 || item.ProfitableRegimeRate is < 0 or > 1))
            throw new ArgumentException("Strategy evidence rates are outside their valid ranges.", nameof(evaluations));
    }
}
