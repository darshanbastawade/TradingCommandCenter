using System.Globalization;
using Trading.Domain.MarketData;

namespace Trading.MarketData.Import;

public sealed class CandleImportException(string message) : Exception(message);

public sealed record CandleImportPreview(IReadOnlyList<Candle> Candles, int GapCount);

/// <summary>Reads the canonical, unquoted numeric CSV format documented in docs/M2.md.</summary>
public static class CandleCsvReader
{
    public const string Header = "timestamp,open,high,low,close,volume,openInterest";
    public const int MaximumRows = 10000;
    public const int MaximumBytes = 4 * 1024 * 1024;

    public static async Task<CandleImportPreview> ReadAsync(TextReader reader, Guid instrumentId,
        Timeframe timeframe, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (instrumentId == Guid.Empty) throw new CandleImportException("A nonempty instrument ID is required.");
        if (!Enum.IsDefined(timeframe)) throw new CandleImportException("Unsupported timeframe.");
        var header = await reader.ReadLineAsync(cancellationToken);
        if (header?.TrimStart('\uFEFF') != Header) throw new CandleImportException($"Line 1: expected {Header}");
        var candles = new List<Candle>();
        var gaps = 0;
        var lineNumber = 1;
        var characters = header.Length;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            characters += line.Length + 1;
            if (characters > MaximumBytes || line.Length > 1024) throw Error(lineNumber, "CSV exceeds the size limit.");
            if (candles.Count == MaximumRows) throw Error(lineNumber, "Maximum 10,000 rows per atomic import.");
            if (line.Length == 0) throw Error(lineNumber, "Blank rows are not allowed.");
            var fields = line.Split(',');
            if (fields.Length != 7 || line.Contains('"')) throw Error(lineNumber, "Expected seven unquoted fields.");
            // An explicit offset is mandatory; never infer the developer machine's timezone.
            if (!DateTimeOffset.TryParseExact(fields[0],
                    ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss'Z'"],
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
                throw Error(lineNumber, "Timestamp must be ISO 8601 with Z or an explicit ±HH:mm offset.");
            if (timestamp.UtcTicks % TimeSpan.TicksPerMinute != 0) throw Error(lineNumber, "Timestamp must begin on a whole minute.");
            var duration = TimeSpan.FromMinutes((int)timeframe);
            if (timestamp > now - duration) throw Error(lineNumber, "Bar is future-dated or not yet complete.");
            try
            {
                var candle = new Candle(instrumentId, timeframe, timestamp,
                    Price(fields[1]), Price(fields[2]), Price(fields[3]), Price(fields[4]),
                    Count(fields[5]), fields[6].Length == 0 ? null : Count(fields[6]));
                if (candles.Count > 0)
                {
                    var delta = candle.OpenTimeUtc - candles[^1].OpenTimeUtc;
                    if (delta <= TimeSpan.Zero) throw Error(lineNumber, "Timestamps must be strictly ascending; duplicate UTC bars are rejected.");
                    if (delta > duration) gaps++;
                    if (delta < duration) throw Error(lineNumber, "Bars overlap for the selected timeframe.");
                }
                candles.Add(candle);
            }
            catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
            {
                throw Error(lineNumber, "Invalid OHLCV/open interest: " + exception.Message);
            }
        }
        if (candles.Count == 0) throw new CandleImportException("CSV must contain at least one data row.");
        return new(candles.AsReadOnly(), gaps);
    }

    private static decimal Price(string value) => decimal.Parse(value,
        NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    private static long Count(string value) => long.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    private static CandleImportException Error(int row, string message) => new($"Line {row}: {message}");
}
