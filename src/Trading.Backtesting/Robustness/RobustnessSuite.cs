namespace Trading.Backtesting.Robustness;

public sealed record RobustnessSuiteSettings
{
    public ulong Seed { get; init; } = 31031;
    public int Iterations { get; init; } = 2000;
    public decimal RuinEquityFraction { get; init; } = .80m;
    public IReadOnlyList<decimal> AdditionalSlippageBasisPointsPerSide { get; init; } = [0, 5, 10, 20];
    public IReadOnlyList<decimal> CostMultipliers { get; init; } = [1, 1.25m, 1.5m, 2];
    public IReadOnlyList<int> EntryDelaySeconds { get; init; } = [0, 5, 15, 30];
    public decimal EntryDelayPenaltyBasisPointsPerSecond { get; init; } = .02m;
    public IReadOnlyList<decimal> MissedTradeProbabilities { get; init; } = [0, .05m, .10m];
}

public sealed record RobustnessPathDistribution(
    int Iterations,
    decimal FifthPercentileNetPnl,
    decimal MedianNetPnl,
    decimal NinetyFifthPercentileNetPnl,
    decimal NinetyFifthPercentileMaximumDrawdownPercent,
    decimal NinetyFifthPercentileMaximumConsecutiveLosses,
    decimal ProbabilityOfLoss,
    decimal ProbabilityOfRuin,
    decimal MedianIncludedTradeCount);

public sealed record RobustnessScenarioResult(
    string Id,
    decimal StressValue,
    string StressUnit,
    int IncludedTradeCount,
    decimal NetPnl,
    decimal FinalCapital,
    decimal MaximumDrawdownPercent,
    int MaximumConsecutiveLosses);

public sealed record MissedTradeSimulationResult(
    decimal MissedTradeProbability,
    RobustnessPathDistribution Distribution);

public sealed record RobustnessSuiteArtifact(
    int SchemaVersion,
    DateTime CreatedAtUtc,
    string SourceEngineId,
    string SourceEngineVersion,
    string SourceResultSha256,
    string SpecificationSha256,
    string DatasetSha256,
    string ConsumedMarketDataSha256,
    int OriginalTradeCount,
    decimal InitialCapital,
    RobustnessSuiteSettings Settings,
    RobustnessPathDistribution MonteCarlo,
    RobustnessPathDistribution Bootstrap,
    IReadOnlyList<RobustnessScenarioResult> SlippageStress,
    IReadOnlyList<RobustnessScenarioResult> CostStress,
    IReadOnlyList<RobustnessScenarioResult> EntryDelayStress,
    IReadOnlyList<MissedTradeSimulationResult> MissedTradeSimulation,
    string ArtifactSha256);
