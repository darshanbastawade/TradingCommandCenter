using Microsoft.EntityFrameworkCore;
using Trading.Application.Execution;
using Trading.Domain.Execution;

namespace Trading.Infrastructure.Persistence;

public sealed class InternalTradingLedgerReader(TradingDbContext db) : IInternalTradingLedgerReader
{
    public async Task<InternalTradingLedgerRead> ReadAsync(string strategyId, DateTime asOfUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(strategyId) || asOfUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Internal ledger query is invalid.");
        var entity = await db.InternalTradingLedgerSnapshots.AsNoTracking()
            .Where(item => item.StrategyId == strategyId && item.AsOfUtc <= asOfUtc)
            .OrderByDescending(item => item.AsOfUtc).ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        InternalTradingLedgerArtifact? state = null;
        if (entity is not null)
        {
            state = InternalTradingLedgerCodec.Deserialize(entity.ArtifactJson);
            if (!InternalTradingLedgerCodec.Verify(state) || state.LedgerStateId != entity.Id ||
                state.AsOfUtc != entity.AsOfUtc || state.StrategyId != entity.StrategyId ||
                state.Revision != entity.Revision || state.ArtifactSha256 != entity.ArtifactSha256)
                throw new InvalidDataException("The durable internal ledger identity or SHA-256 is invalid.");
        }
        var orders = await db.LiveOrders.AsNoTracking().Where(item => item.StrategyId == strategyId &&
                item.CreatedAtUtc <= asOfUtc && item.Mode == "DirectLive")
            .OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id)
            .Select(item => new DurableLiveOrderEvidence(item.Id, item.RequestId, item.CreatedAtUtc,
                item.Mode, item.Status, item.StrategyId, item.Exchange, item.TradingSymbol, item.Quantity,
                item.BrokerOrderId, item.ArtifactSha256, item.ArtifactJson)).ToListAsync(cancellationToken);
        return new(state, orders);
    }
}

public sealed class ReconciledExecutionStateStore(TradingDbContext db) : IReconciledExecutionStateStore
{
    public async Task AddAsync(ReconciledExecutionState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var existing = await db.ReconciledExecutionStates.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ReconciliationId == state.ReconciliationId, cancellationToken);
        if (existing is not null)
        {
            if (existing.ReconciliationSha256 != state.ReconciliationSha256)
                throw new InvalidOperationException("A different reconciled state already uses this reconciliation ID.");
            return;
        }
        db.ReconciledExecutionStates.Add(state);
        await db.SaveChangesAsync(cancellationToken);
    }
}
