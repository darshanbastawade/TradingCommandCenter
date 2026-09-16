using Trading.Domain.MarketData;

namespace Trading.Application.MarketData;

public interface IOptionMarketDataStore
{
    Task AddContractAsync(OptionContract contract, CancellationToken cancellationToken = default);
    Task<OptionContract?> FindContractAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OptionContract>> ReadContractsAsync(Guid underlyingInstrumentId, DateOnly fromExpiry,
        DateOnly toExpiryInclusive, CancellationToken cancellationToken = default);
    Task AddQuotesAsync(IReadOnlyCollection<OptionQuote> quotes, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OptionQuote>> ReadQuotesAsync(IReadOnlyCollection<Guid> contractIds,
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
}
