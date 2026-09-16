using Microsoft.EntityFrameworkCore;
using Trading.Application.Research;
using Trading.Domain.Research;

namespace Trading.Infrastructure.Persistence;

public sealed class BacktestCandidateStore(TradingDbContext db) : IBacktestCandidateStore
{
    public async Task AddSweepAsync(ParameterSweep sweep, IReadOnlyCollection<BacktestCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sweep); ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count != sweep.StoredCandidates || candidates.Any(item => item.ParameterSweepId != sweep.Id))
            throw new ArgumentException("Stored candidates do not match the sweep.", nameof(candidates));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.ParameterSweeps.Add(sweep); db.BacktestCandidates.AddRange(candidates);
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    public Task<ParameterSweep?> FindSweepAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.ParameterSweeps.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

    public Task<BacktestCandidate?> FindCandidateAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.BacktestCandidates.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

    public async Task<IReadOnlyList<BacktestCandidate>> ListCandidatesAsync(Guid sweepId, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        return await db.BacktestCandidates.AsNoTracking().Where(item => item.ParameterSweepId == sweepId)
            .OrderBy(item => item.Rank).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ParameterSweepSummary>> ListSweepsAsync(int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        return await db.ParameterSweeps.AsNoTracking().OrderByDescending(item => item.CreatedAtUtc).Take(limit)
            .Select(item => new ParameterSweepSummary(item.Id, item.CreatedAtUtc, item.StrategyId,
                item.WorkerId, item.WorkerVersion, item.BaseSpecificationSha256, item.DatasetSha256,
                item.GridSha256, item.EvaluatedCandidates, item.StoredCandidates, item.ArtifactSha256,
                item.NativeVerificationCompleted)).ToListAsync(cancellationToken);
    }

    public async Task CompleteVerificationAsync(NativeCandidateVerificationRun verification,
        IReadOnlyCollection<NativeCandidateOutcome> outcomes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verification); ArgumentNullException.ThrowIfNull(outcomes);
        if (outcomes.Count != verification.RequestedCandidates ||
            outcomes.Select(item => item.CandidateId).Distinct().Count() != outcomes.Count)
            throw new ArgumentException("Verification outcomes do not match the run.", nameof(outcomes));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var sweep = await db.ParameterSweeps.SingleOrDefaultAsync(item => item.Id == verification.ParameterSweepId,
            cancellationToken) ?? throw new InvalidOperationException("Parameter sweep was not found.");
        if (sweep.NativeVerificationCompleted) throw new InvalidOperationException("Sweep verification is already complete.");
        var ids = outcomes.Select(item => item.CandidateId).ToArray();
        var candidates = await db.BacktestCandidates.Where(item => item.ParameterSweepId == sweep.Id && ids.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        if (candidates.Count != outcomes.Count) throw new InvalidOperationException("A verification candidate was not found.");
        foreach (var outcome in outcomes) candidates[outcome.CandidateId].Apply(outcome);
        sweep.MarkNativeVerificationCompleted(); db.NativeCandidateVerificationRuns.Add(verification);
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }
}
