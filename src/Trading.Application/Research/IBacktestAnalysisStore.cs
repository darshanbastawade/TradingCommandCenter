using Trading.Domain.Research;

namespace Trading.Application.Research;

public sealed record BacktestAnalysisSummary(Guid Id, Guid ResearchRunId, DateTime CreatedAtUtc,
    string Deployment, string ResponseModel, string ProviderResponseId, string PromptVersion,
    string PromptSha256, string ResearchArtifactSha256, int InputTokens, int OutputTokens,
    string AnalysisSha256);

public interface IBacktestAnalysisStore
{
    Task AddAsync(BacktestAnalysis analysis, CancellationToken cancellationToken = default);
    Task<BacktestAnalysis?> FindAsync(Guid id, CancellationToken cancellationToken = default);
    Task<BacktestAnalysis?> FindExistingAsync(Guid researchRunId, string promptSha256, string deployment,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BacktestAnalysisSummary>> ListAsync(int limit = 100,
        CancellationToken cancellationToken = default);
}
