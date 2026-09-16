using Microsoft.EntityFrameworkCore;
using Trading.Application.Execution;
using Trading.Domain.Execution;

namespace Trading.Infrastructure.Persistence;

public sealed class LiveOrderStore(TradingDbContext db) : ILiveOrderStore
{
    public async Task AddAsync(LiveOrderRecord record, CancellationToken cancellationToken = default)
    { db.LiveOrders.Add(record); await db.SaveChangesAsync(cancellationToken); }
    public async Task UpdateAsync(LiveOrderRecord record, CancellationToken cancellationToken = default)
    { db.LiveOrders.Update(record); await db.SaveChangesAsync(cancellationToken); }
    public Task<LiveOrderRecord?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.LiveOrders.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    public async Task<IReadOnlyList<LiveOrderSummary>> ListAsync(int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        return await db.LiveOrders.AsNoTracking().OrderByDescending(item => item.CreatedAtUtc).Take(limit)
            .Select(item => new LiveOrderSummary(item.Id, item.StrategyCertificateId, item.RequestId,
                item.CreatedAtUtc, item.Mode, item.Status, item.StrategyId, item.Exchange,
                item.TradingSymbol, item.Quantity, item.LimitPrice, item.BrokerOrderId,
                item.ArtifactSha256)).ToListAsync(cancellationToken);
    }
}
