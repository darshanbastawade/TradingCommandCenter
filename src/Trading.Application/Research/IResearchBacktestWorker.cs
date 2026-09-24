using Trading.Application.Backtesting;
using System.Text.Json.Serialization;

namespace Trading.Application.Research;

public sealed record ResearchWorkerCandle(DateTime OpenTimeUtc, decimal Open, decimal High, decimal Low,
    decimal Close, long Volume);

public sealed record ResearchWorkerCandidate(string CandidateKey, IReadOnlyDictionary<string, decimal> Parameters);

public sealed record ResearchWorkerRequest(int SchemaVersion, string RequestId, string RequestSha256,
    string BaseSpecificationSha256, string StrategyId, int TimeframeMinutes, string ExchangeTimeZoneId,
    decimal InitialCapital, decimal SlippageBasisPointsPerSide, TimeOnly SessionExitTime,
    int TopCandidates, IReadOnlyList<ResearchWorkerCandle> Candles,
    IReadOnlyList<ResearchWorkerCandidate> Candidates);

public sealed record ResearchCandidateMetrics(decimal Score, decimal TotalReturnPercent,
    decimal MaximumDrawdownPercent, int TradeCount, decimal WinRatePercent, decimal SharpeRatio);

public sealed record ResearchWorkerCandidateResult(string CandidateKey, ResearchCandidateMetrics Metrics);

public sealed record ResearchWorkerParityEvidence(string StrategyId, bool Passed,
    string StrategyPortVersion, string StrategyPortSha256);

public sealed record ResearchWorkerResult(int SchemaVersion, string WorkerId, string WorkerVersion,
    BacktestEngineRole Role, string RequestId, string RequestSha256,
    IReadOnlyList<ResearchWorkerCandidateResult> Candidates, string EvidenceSha256)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResearchWorkerParityEvidence? ParityEvidence { get; init; }
}

public interface IResearchBacktestWorker
{
    string WorkerId { get; }
    string WorkerVersion { get; }
    BacktestEngineRole Role { get; }

    Task<ResearchWorkerResult> RunAsync(ResearchWorkerRequest request,
        CancellationToken cancellationToken = default);
}
