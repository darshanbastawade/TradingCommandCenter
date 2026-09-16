using Trading.Domain.MarketData;

namespace Trading.Application.MarketData;

public interface IMarketDataStore
{
    Task AddInstrumentAsync(Instrument instrument, CancellationToken cancellationToken = default);
    Task<Instrument?> FindInstrumentAsync(Guid id, CancellationToken cancellationToken = default);
    // Each batch is atomic. Duplicate keys are rejected rather than overwriting research evidence.
    Task AddCandlesAsync(IReadOnlyCollection<Candle> candles, CancellationToken cancellationToken = default);
    // Half-open UTC interval [from, to); ascending time order; at most 10,000 rows per request.
    Task<IReadOnlyList<Candle>> ReadCandlesAsync(Guid instrumentId, Timeframe timeframe,
        DateTimeOffset from, DateTimeOffset to, int limit = 10000, CancellationToken cancellationToken = default);
}
