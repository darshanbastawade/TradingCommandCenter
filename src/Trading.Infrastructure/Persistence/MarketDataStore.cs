using Microsoft.EntityFrameworkCore;
using Trading.Application.MarketData;
using Trading.Domain.MarketData;

namespace Trading.Infrastructure.Persistence;

public sealed class MarketDataStore(TradingDbContext db) : IMarketDataStore
{
    public async Task AddInstrumentAsync(Instrument instrument, CancellationToken cancellationToken = default)
    {
        db.Instruments.Add(instrument);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<Instrument?> FindInstrumentAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Instruments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task AddCandlesAsync(IReadOnlyCollection<Candle> candles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (candles.Count > 10000) throw new ArgumentException("A batch may contain at most 10,000 candles.", nameof(candles));
        db.Candles.AddRange(candles);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Candle>> ReadCandlesAsync(Guid instrumentId, Timeframe timeframe,
        DateTimeOffset from, DateTimeOffset to, int limit = 10000, CancellationToken cancellationToken = default)
    {
        if (instrumentId == Guid.Empty) throw new ArgumentException("Instrument ID is required.", nameof(instrumentId));
        if (!Enum.IsDefined(timeframe)) throw new ArgumentOutOfRangeException(nameof(timeframe));
        if (from >= to) throw new ArgumentException("The start must precede the end.", nameof(from));
        if (limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(limit));
        var start = from.UtcDateTime;
        var end = to.UtcDateTime;
        return await db.Candles.AsNoTracking()
            .Where(x => x.InstrumentId == instrumentId && x.Timeframe == timeframe && x.OpenTimeUtc >= start && x.OpenTimeUtc < end)
            .OrderBy(x => x.OpenTimeUtc).Take(limit).ToListAsync(cancellationToken);
    }
}
