using Microsoft.EntityFrameworkCore;
using Trading.Application.Research;
using Trading.Domain.Research;

namespace Trading.Infrastructure.Persistence;

public sealed class ResearchRunStore(TradingDbContext db) : IResearchRunStore
{
    public async Task AddAsync(ResearchRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        db.ResearchRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<ResearchRun?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.ResearchRuns.AsNoTracking().SingleOrDefaultAsync(run => run.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ResearchRunSummary>> ListAsync(int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        return await db.ResearchRuns.AsNoTracking().OrderByDescending(run => run.CreatedAtUtc)
            .Take(limit).Select(run => new ResearchRunSummary(run.Id, run.CreatedAtUtc, run.InstrumentId,
                run.Timeframe, run.FromUtc, run.ToUtc, run.DataSource, run.DataVersion, run.CalendarId,
                run.DatasetSha256, run.ConfigurationSha256, run.ArtifactSha256, run.SourceRevision))
            .ToListAsync(cancellationToken);
    }
}
