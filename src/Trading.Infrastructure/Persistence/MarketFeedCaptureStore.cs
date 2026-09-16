using Microsoft.EntityFrameworkCore;
using Trading.Application.MarketData;
using Trading.Domain.MarketData;

namespace Trading.Infrastructure.Persistence;

public sealed class MarketFeedCaptureStore(TradingDbContext db) : IMarketFeedCaptureStore
{
    public async Task AddAsync(MarketFeedCapture capture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        db.MarketFeedCaptures.Add(capture);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<MarketFeedCapture?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.MarketFeedCaptures.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

    public async Task<IReadOnlyList<MarketFeedCaptureSummary>> ListAsync(int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        return await db.MarketFeedCaptures.AsNoTracking().OrderByDescending(item => item.CreatedAtUtc).Take(limit)
            .Select(item => new MarketFeedCaptureSummary(item.Id, item.CreatedAtUtc, item.Source, item.QuoteMode,
                item.TickCount, item.FirstReceivedAtUtc, item.LastReceivedAtUtc, item.ArtifactSha256))
            .ToListAsync(cancellationToken);
    }
}
