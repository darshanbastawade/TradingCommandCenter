using Microsoft.EntityFrameworkCore;
using Trading.Application.MarketData;
using Trading.Domain.MarketData;

namespace Trading.Infrastructure.Persistence;

public sealed class OptionMarketDataStore(TradingDbContext db) : IOptionMarketDataStore
{
    public async Task AddContractAsync(OptionContract contract, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        db.OptionContracts.Add(contract);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<OptionContract?> FindContractAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.OptionContracts.AsNoTracking().SingleOrDefaultAsync(contract => contract.Id == id, cancellationToken);

    public async Task<IReadOnlyList<OptionContract>> ReadContractsAsync(Guid underlyingInstrumentId,
        DateOnly fromExpiry, DateOnly toExpiryInclusive, CancellationToken cancellationToken = default)
    {
        if (underlyingInstrumentId == Guid.Empty) throw new ArgumentException("Underlying ID is required.", nameof(underlyingInstrumentId));
        if (fromExpiry > toExpiryInclusive) throw new ArgumentException("Expiry range is invalid.");
        return await db.OptionContracts.AsNoTracking()
            .Where(contract => contract.UnderlyingInstrumentId == underlyingInstrumentId &&
                contract.ExpiryDate >= fromExpiry && contract.ExpiryDate <= toExpiryInclusive)
            .OrderBy(contract => contract.ExpiryDate).ThenBy(contract => contract.Strike)
            .ThenBy(contract => contract.Right).ToListAsync(cancellationToken);
    }

    public async Task AddQuotesAsync(IReadOnlyCollection<OptionQuote> quotes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quotes);
        if (quotes.Count is < 1 or > 10000) throw new ArgumentException("A batch must contain 1 to 10,000 quotes.", nameof(quotes));
        db.OptionQuotes.AddRange(quotes);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OptionQuote>> ReadQuotesAsync(IReadOnlyCollection<Guid> contractIds,
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contractIds);
        if (contractIds.Count is < 1 or > 2000 || contractIds.Any(id => id == Guid.Empty))
            throw new ArgumentException("Provide 1 to 2,000 valid contract IDs.", nameof(contractIds));
        if (from >= to) throw new ArgumentException("The start must precede the end.");
        var ids = contractIds.Distinct().ToArray();
        var start = from.UtcDateTime;
        var end = to.UtcDateTime;
        return await db.OptionQuotes.AsNoTracking()
            .Where(quote => ids.Contains(quote.OptionContractId) && quote.TimestampUtc >= start && quote.TimestampUtc < end)
            .OrderBy(quote => quote.TimestampUtc).ThenBy(quote => quote.OptionContractId)
            .ToListAsync(cancellationToken);
    }
}
