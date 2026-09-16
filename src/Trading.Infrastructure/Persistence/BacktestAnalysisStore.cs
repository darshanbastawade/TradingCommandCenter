using Microsoft.EntityFrameworkCore;
using Trading.Application.Research;
using Trading.Domain.Research;

namespace Trading.Infrastructure.Persistence;

public sealed class BacktestAnalysisStore(TradingDbContext db) : IBacktestAnalysisStore
{
    public async Task AddAsync(BacktestAnalysis analysis, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        db.BacktestAnalyses.Add(analysis);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<BacktestAnalysis?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.BacktestAnalyses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

    public Task<BacktestAnalysis?> FindExistingAsync(Guid researchRunId, string promptSha256, string deployment,
        CancellationToken cancellationToken = default) => db.BacktestAnalyses.AsNoTracking().SingleOrDefaultAsync(
        item => item.ResearchRunId == researchRunId && item.PromptSha256 == promptSha256 &&
                item.Deployment == deployment, cancellationToken);

    public async Task<IReadOnlyList<BacktestAnalysisSummary>> ListAsync(int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        return await db.BacktestAnalyses.AsNoTracking().OrderByDescending(item => item.CreatedAtUtc).Take(limit)
            .Select(item => new BacktestAnalysisSummary(item.Id, item.ResearchRunId, item.CreatedAtUtc,
                item.Deployment, item.ResponseModel, item.ProviderResponseId, item.PromptVersion,
                item.PromptSha256, item.ResearchArtifactSha256, item.InputTokens, item.OutputTokens,
                item.AnalysisSha256)).ToListAsync(cancellationToken);
    }
}
