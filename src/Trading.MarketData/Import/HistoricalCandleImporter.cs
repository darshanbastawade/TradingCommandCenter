using Trading.Application.MarketData;
using Trading.Domain.MarketData;

namespace Trading.MarketData.Import;

public sealed class HistoricalCandleImporter(IMarketDataStore store)
{
    public async Task<CandleImportPreview> ImportAsync(TextReader reader, Guid instrumentId, Timeframe timeframe,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var preview = await CandleCsvReader.ReadAsync(reader, instrumentId, timeframe, now, cancellationToken);
        var instrument = await store.FindInstrumentAsync(instrumentId, cancellationToken)
            ?? throw new CandleImportException("Instrument does not exist. Register it before importing.");
        for (var i = 0; i < preview.Candles.Count; i++)
        {
            var candle = preview.Candles[i];
            if (new[] { candle.Open, candle.High, candle.Low, candle.Close }.Any(price => price % instrument.TickSize != 0))
                throw new CandleImportException($"Line {i + 2}: a price is not a multiple of the instrument tick size.");
        }
        // Preflight reports overlaps clearly. The database key remains authoritative for concurrent imports.
        var first = preview.Candles[0].OpenTimeUtc;
        var last = preview.Candles[^1].OpenTimeUtc;
        var timestamps = preview.Candles.Select(candle => candle.OpenTimeUtc).ToHashSet();
        var cursor = new DateTimeOffset(first);
        var end = new DateTimeOffset(last).AddTicks(1);
        while (true)
        {
            var existing = await store.ReadCandlesAsync(instrumentId, timeframe, cursor, end, 10000, cancellationToken);
            if (existing.Any(candle => timestamps.Contains(candle.OpenTimeUtc)))
                throw new CandleImportException("An imported candle already exists. Entire import rejected; nothing overwritten.");
            if (existing.Count < 10000) break;
            cursor = new DateTimeOffset(existing[^1].OpenTimeUtc).AddTicks(1);
        }
        await store.AddCandlesAsync(preview.Candles, cancellationToken);
        return preview;
    }
}
