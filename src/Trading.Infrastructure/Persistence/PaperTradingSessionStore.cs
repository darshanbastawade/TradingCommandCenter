using Microsoft.EntityFrameworkCore;
using Trading.Application.Execution;
using Trading.Domain.Execution;

namespace Trading.Infrastructure.Persistence;

public sealed class PaperTradingSessionStore(TradingDbContext db) : IPaperTradingSessionStore
{
    public async Task AddAsync(PaperTradingSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        db.PaperTradingSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<PaperTradingSession?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.PaperTradingSessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

    public async Task<IReadOnlyList<PaperTradingSessionSummary>> ListAsync(int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        return await db.PaperTradingSessions.AsNoTracking().OrderByDescending(item => item.CreatedAtUtc).Take(limit)
            .Select(item => new PaperTradingSessionSummary(item.Id, item.StrategyCertificateId,
                item.MarketFeedCaptureId, item.CreatedAtUtc, item.StrategyId, item.InitialCash,
                item.EndingCash, item.RealizedNetPnl, item.SubmittedOrders, item.FilledTrades,
                item.RejectedOrders, item.ConfigurationSha256, item.ArtifactSha256)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PaperTradingSession>> ListForCertificateAsync(Guid certificateId,
        CancellationToken cancellationToken = default) => await db.PaperTradingSessions.AsNoTracking()
        .Where(item => item.StrategyCertificateId == certificateId).OrderBy(item => item.CreatedAtUtc)
        .ToListAsync(cancellationToken);
}
