using Trading.Domain.Research;

namespace Trading.Application.Research;

public sealed record ParameterSweepSummary(Guid Id, DateTime CreatedAtUtc, string StrategyId,
    string WorkerId, string WorkerVersion, string BaseSpecificationSha256, string DatasetSha256,
    string GridSha256, int EvaluatedCandidates, int StoredCandidates, string ArtifactSha256,
    bool NativeVerificationCompleted);

public interface IBacktestCandidateStore
{
    Task AddSweepAsync(ParameterSweep sweep, IReadOnlyCollection<BacktestCandidate> candidates,
        CancellationToken cancellationToken = default);
    Task<ParameterSweep?> FindSweepAsync(Guid id, CancellationToken cancellationToken = default);
    Task<BacktestCandidate?> FindCandidateAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BacktestCandidate>> ListCandidatesAsync(Guid sweepId, int limit = 100,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ParameterSweepSummary>> ListSweepsAsync(int limit = 100,
        CancellationToken cancellationToken = default);
    Task CompleteVerificationAsync(NativeCandidateVerificationRun verification,
        IReadOnlyCollection<NativeCandidateOutcome> outcomes, CancellationToken cancellationToken = default);
}
