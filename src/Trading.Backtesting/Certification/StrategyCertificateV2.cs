using Trading.Backtesting.Ranking;

namespace Trading.Backtesting.Certification;

public enum StrategyCertificateV2Status { EvidenceQualified = 1, EvidenceRejected = 2 }

public sealed record StrategyCertificateV2Policy
{
    public string PolicyVersion { get; init; } = "strategy-certificate-v2";
    public int ValidityDays { get; init; } = 90;
    public decimal MinimumCrossEngineMatchedTradeRate { get; init; } = 1m;
    public bool RequireExactCrossEngineTimestamps { get; init; } = true;
    public decimal MaximumCrossEnginePriceDeviationBasisPoints { get; init; } = 1m;
    public decimal MaximumCrossEngineNetPnlDeviationPercent { get; init; } = 1m;
    public decimal MaximumCrossEngineDrawdownDeviationPercentagePoints { get; init; } = .5m;
    public decimal MaximumMonteCarloDrawdownPercent { get; init; } = 20m;
    public decimal MaximumBootstrapProbabilityOfLoss { get; init; } = .20m;
    public decimal MaximumBootstrapProbabilityOfRuin { get; init; } = .05m;
    public decimal MinimumBootstrapFifthPercentileNetPnl { get; init; } = 0m;
    public decimal RequiredAdditionalSlippageBasisPointsPerSide { get; init; } = 10m;
    public decimal RequiredCostMultiplier { get; init; } = 1.5m;
    public int RequiredEntryDelaySeconds { get; init; } = 30;
    public decimal RequiredMissedTradeProbability { get; init; } = .10m;
}

public sealed record StrategyCertificateV2Source(
    Guid ResearchRunId,
    DateTime ResearchCreatedAtUtc,
    string StrategyId,
    string SourceRevision,
    string DatasetSha256,
    string ResearchArtifactSha256,
    string SpecificationSha256,
    string ParametersSha256,
    string NativeResultSha256,
    string LeanResultSha256,
    string CrossEngineComparisonSha256,
    string RobustnessArtifactSha256,
    StrategyScore Qualification,
    bool GenuineLeanValidation,
    bool CrossEnginePassed,
    decimal CrossEngineMatchedTradeRate,
    decimal CrossEngineEntryTimestampMatchRate,
    decimal CrossEngineExitTimestampMatchRate,
    decimal CrossEngineMaximumPriceDeviationBasisPoints,
    decimal CrossEngineNetPnlDeviationPercent,
    decimal CrossEngineDrawdownDeviationPercentagePoints,
    decimal MonteCarloDrawdownPercent,
    decimal BootstrapProbabilityOfLoss,
    decimal BootstrapProbabilityOfRuin,
    decimal BootstrapFifthPercentileNetPnl,
    decimal SlippageStressNetPnl,
    decimal CostStressNetPnl,
    decimal EntryDelayStressNetPnl,
    decimal MissedTradeMedianNetPnl);

public sealed record StrategyCertificateV2(
    int SchemaVersion,
    Guid CertificateId,
    string PolicyVersion,
    StrategyCertificateV2Status Status,
    Guid ResearchRunId,
    string StrategyId,
    DateTime IssuedAtUtc,
    DateTime ExpiresAtUtc,
    string SourceRevision,
    string DatasetSha256,
    string ResearchArtifactSha256,
    string SpecificationSha256,
    string ParametersSha256,
    string NativeResultSha256,
    string LeanResultSha256,
    string CrossEngineComparisonSha256,
    string RobustnessArtifactSha256,
    StrategyScore Qualification,
    StrategyCertificateV2Policy Policy,
    IReadOnlyList<string> EvidenceFailures,
    bool EligibleForQualificationPipeline,
    bool EligibleForPaperTrading,
    bool LiveTradingAuthorized,
    bool HumanApprovalRequired,
    string CertificateSha256);
