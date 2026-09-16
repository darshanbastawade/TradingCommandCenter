namespace Trading.Domain.Research;

public static class BacktestCandidateStatus
{
    public const string ResearchProposed = "ResearchProposed";
    public const string NativeVerified = "NativeVerified";
    public const string NativeFailed = "NativeFailed";
}

public sealed class BacktestCandidate
{
    private BacktestCandidate() { }

    public BacktestCandidate(Guid id, Guid parameterSweepId, int rank, string strategyId,
        string specificationSha256, string datasetSha256, string candidateSpecificationJson,
        string parametersJson, decimal researchScore, string researchMetricsJson,
        string researchEvidenceSha256)
    {
        if (id == Guid.Empty || parameterSweepId == Guid.Empty || rank < 1)
            throw new ArgumentException("Candidate identity and rank are invalid.");
        Id = id; ParameterSweepId = parameterSweepId; Rank = rank;
        StrategyId = ParameterSweep.Required(strategyId, 128, nameof(strategyId));
        SpecificationSha256 = ParameterSweep.Hash(specificationSha256, nameof(specificationSha256));
        DatasetSha256 = ParameterSweep.Hash(datasetSha256, nameof(datasetSha256));
        CandidateSpecificationJson = ParameterSweep.Json(candidateSpecificationJson, nameof(candidateSpecificationJson));
        ParametersJson = ParameterSweep.Json(parametersJson, nameof(parametersJson));
        ResearchScore = researchScore;
        ResearchMetricsJson = ParameterSweep.Json(researchMetricsJson, nameof(researchMetricsJson));
        ResearchEvidenceSha256 = ParameterSweep.Hash(researchEvidenceSha256, nameof(researchEvidenceSha256));
        Status = BacktestCandidateStatus.ResearchProposed;
    }

    public Guid Id { get; private set; }
    public Guid ParameterSweepId { get; private set; }
    public int Rank { get; private set; }
    public string StrategyId { get; private set; } = string.Empty;
    public string SpecificationSha256 { get; private set; } = string.Empty;
    public string DatasetSha256 { get; private set; } = string.Empty;
    public string CandidateSpecificationJson { get; private set; } = string.Empty;
    public string ParametersJson { get; private set; } = string.Empty;
    public decimal ResearchScore { get; private set; }
    public string ResearchMetricsJson { get; private set; } = string.Empty;
    public string ResearchEvidenceSha256 { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public DateTime? VerifiedAtUtc { get; private set; }
    public string NativeEngineId { get; private set; } = string.Empty;
    public string NativeEngineVersion { get; private set; } = string.Empty;
    public string NativeResultSha256 { get; private set; } = string.Empty;
    public decimal? NativeNetPnl { get; private set; }
    public int? NativeTradeCount { get; private set; }
    public string NativeRunJson { get; private set; } = string.Empty;
    public string VerificationFailure { get; private set; } = string.Empty;

    public void Apply(NativeCandidateOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.CandidateId != Id || Status != BacktestCandidateStatus.ResearchProposed)
            throw new InvalidOperationException("Candidate cannot accept this verification outcome.");
        VerifiedAtUtc = outcome.VerifiedAtUtc;
        NativeEngineId = ParameterSweep.Required(outcome.EngineId, 64, nameof(outcome.EngineId));
        NativeEngineVersion = ParameterSweep.Required(outcome.EngineVersion, 64, nameof(outcome.EngineVersion));
        if (outcome.Succeeded)
        {
            NativeResultSha256 = ParameterSweep.Hash(outcome.ResultSha256, nameof(outcome.ResultSha256));
            NativeNetPnl = outcome.NetPnl;
            NativeTradeCount = outcome.TradeCount >= 0 ? outcome.TradeCount :
                throw new ArgumentException("Trade count cannot be negative.", nameof(outcome));
            NativeRunJson = ParameterSweep.Json(outcome.RunJson, nameof(outcome.RunJson));
            Status = BacktestCandidateStatus.NativeVerified;
        }
        else
        {
            VerificationFailure = ParameterSweep.Required(outcome.Failure, 512, nameof(outcome.Failure));
            Status = BacktestCandidateStatus.NativeFailed;
        }
    }
}

public sealed record NativeCandidateOutcome(Guid CandidateId, bool Succeeded, DateTime VerifiedAtUtc,
    string EngineId, string EngineVersion, string ResultSha256, decimal? NetPnl, int TradeCount,
    string RunJson, string Failure);
