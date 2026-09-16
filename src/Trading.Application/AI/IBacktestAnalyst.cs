namespace Trading.Application.AI;

public enum AnalystRecommendation { PaperTest = 1, MoreResearch = 2, Reject = 3 }

public sealed record StrategyAnalysis(
    string StrategyId,
    string EvidenceSummary,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Weaknesses,
    IReadOnlyList<string> RobustnessConcerns,
    IReadOnlyList<string> RegimeObservations,
    IReadOnlyList<string> ExecutionConcerns,
    AnalystRecommendation Recommendation);

public sealed record BacktestAnalystOutput(
    int SchemaVersion,
    Guid ResearchRunId,
    string ExecutiveSummary,
    IReadOnlyList<StrategyAnalysis> StrategyAnalyses,
    IReadOnlyList<string> PortfolioObservations,
    IReadOnlyList<string> RequiredNextTests,
    IReadOnlyList<string> Warnings);

public sealed record BacktestAnalystRequest(Guid ResearchRunId, string ResearchArtifactSha256,
    string ResearchArtifactJson, IReadOnlyList<string> ExpectedStrategyIds);

public sealed record BacktestAnalystResponse(string ProviderResponseId, string ResponseModel,
    int InputTokens, int OutputTokens, BacktestAnalystOutput Output);

public sealed record BacktestAnalysisArtifact(int SchemaVersion, Guid AnalysisId, DateTime CreatedAtUtc,
    Guid ResearchRunId, string ResearchArtifactSha256, string Deployment, string ResponseModel,
    string ProviderResponseId, string PromptVersion, string PromptSha256, int InputTokens,
    int OutputTokens, BacktestAnalystOutput Analysis, string AnalysisSha256);

public interface IBacktestAnalyst
{
    string PromptVersion { get; }
    string PromptSha256 { get; }
    string Deployment { get; }
    Task<BacktestAnalystResponse> AnalyzeAsync(BacktestAnalystRequest request,
        CancellationToken cancellationToken = default);
}
