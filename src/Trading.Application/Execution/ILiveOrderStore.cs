using Trading.Domain.Execution;

namespace Trading.Application.Execution;

public sealed record LiveOrderSummary(Guid Id, Guid StrategyCertificateId, Guid RequestId,
    DateTime CreatedAtUtc, string Mode, string Status, string StrategyId, string Exchange,
    string TradingSymbol, int Quantity, decimal LimitPrice, string BrokerOrderId,
    string ArtifactSha256);

public interface ILiveOrderStore
{
    Task AddAsync(LiveOrderRecord record, CancellationToken cancellationToken = default);
    Task UpdateAsync(LiveOrderRecord record, CancellationToken cancellationToken = default);
    Task<LiveOrderRecord?> FindAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LiveOrderSummary>> ListAsync(int limit = 100,
        CancellationToken cancellationToken = default);
}
