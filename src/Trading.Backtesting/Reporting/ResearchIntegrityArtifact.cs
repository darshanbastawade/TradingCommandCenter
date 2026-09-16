using Trading.Backtesting.Ranking;
using Trading.Backtesting.Robustness;
using Trading.Backtesting.Validation;
using Trading.MarketData.Quality;

namespace Trading.Backtesting.Reporting;

public sealed record ResearchRunConfiguration(
    int TrainingSessionCount,
    int TestingSessionCount,
    int EmbargoSessionCount,
    string TrainingMode,
    decimal InitialCapital,
    decimal AllowedRisk,
    decimal MaximumCapital,
    int? MaximumLots,
    decimal SlippageBasisPoints,
    string CostProfile,
    StrategyRankingSettings Ranking);

public sealed record StrategyResearchEvidence(
    string StrategyId,
    OosTestArtifact Holdout,
    WalkForwardTestArtifact WalkForward,
    ParameterRobustnessReport ParameterRobustness,
    IReadOnlyList<RegimePerformanceRow> Regimes,
    BestTradeRemovalReport BestTradeRemoval,
    ExecutionStressReport ExecutionSensitivity);

public sealed record ResearchIntegrityArtifact(
    int SchemaVersion,
    Guid RunId,
    DateTime CreatedAtUtc,
    string SourceRevision,
    DatasetQualityCertificate Dataset,
    string ConfigurationSha256,
    ResearchRunConfiguration Configuration,
    IReadOnlyList<StrategyResearchEvidence> Strategies,
    StrategyRankingResult Ranking);
