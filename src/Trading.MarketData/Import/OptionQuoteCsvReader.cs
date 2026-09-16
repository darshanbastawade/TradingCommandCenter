using System.Globalization;
using Trading.Domain.MarketData;

namespace Trading.MarketData.Import;

public static class OptionQuoteCsvReader
{
    public const string Header = "timestamp,bid,ask,last,volume,openInterest";
    public const int MaximumRows = 10000;
    public const int MaximumBytes = 4 * 1024 * 1024;

    public static async Task<IReadOnlyList<OptionQuote>> ReadAsync(TextReader reader, Guid contractId,
        DateTimeOffset importedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (contractId == Guid.Empty) throw new ArgumentException("Contract ID is required.", nameof(contractId));
        var header = await reader.ReadLineAsync(cancellationToken);
        if (header != Header) throw new CandleImportException($"Option quote CSV header must be exactly: {Header}");
        var quotes = new List<OptionQuote>();
        string? row;
        var line = 1;
        while ((row = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            line++;
            if (string.IsNullOrWhiteSpace(row)) throw new CandleImportException($"Line {line}: blank rows are not allowed.");
            if (quotes.Count == MaximumRows) throw new CandleImportException($"CSV exceeds {MaximumRows:N0} rows.");
            var values = row.Split(',');
            if (values.Length != 6) throw new CandleImportException($"Line {line}: expected 6 columns.");
            if (!DateTimeOffset.TryParseExact(values[0], "yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var timestamp) || !HasOffset(values[0]))
                throw new CandleImportException($"Line {line}: timestamp must include Z or an explicit UTC offset.");
            if (timestamp > importedAt) throw new CandleImportException($"Line {line}: future quotes are not accepted.");
            try
            {
                var quote = new OptionQuote(contractId, timestamp, Decimal(values[1]), Decimal(values[2]),
                    Decimal(values[3]), Long(values[4]), Long(values[5]));
                if (quotes.Count > 0 && quote.TimestampUtc <= quotes[^1].TimestampUtc)
                    throw new CandleImportException($"Line {line}: timestamps must be unique and chronological.");
                quotes.Add(quote);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                throw new CandleImportException($"Line {line}: invalid quote values.");
            }
        }
        if (quotes.Count == 0) throw new CandleImportException("Option quote CSV contains no rows.");
        return quotes.AsReadOnly();
    }

    private static decimal Decimal(string value) => decimal.Parse(value,
        NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    private static long Long(string value) => long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    private static bool HasOffset(string value) => value.EndsWith('Z') ||
        (value.Length >= 6 && (value[^6] == '+' || value[^6] == '-') && value[^3] == ':');
}
