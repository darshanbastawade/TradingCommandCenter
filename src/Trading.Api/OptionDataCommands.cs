using System.Globalization;
using System.Text;
using System.Text.Json;
using Trading.Application.MarketData;
using Trading.Domain.MarketData;
using Trading.MarketData.Import;

namespace Trading.Api;

public static class OptionDataCommands
{
    public static bool IsCommand(string[] args) => args.Length > 0 &&
        args[0] is "add-option-contract" or "import-option-quotes";

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output,
        TextWriter error, CancellationToken cancellationToken = default)
    {
        try
        {
            var values = Parse(args);
            var contractId = Guid.Parse(Required(values, "contract-id"));
            await using var scope = services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IOptionMarketDataStore>();
            if (args[0] == "add-option-contract")
            {
                var right = Enum.Parse<OptionRight>(Required(values, "right"), true);
                var contract = new OptionContract(contractId, Guid.Parse(Required(values, "underlying-id")),
                    Required(values, "exchange"), Required(values, "symbol"),
                    DateOnly.ParseExact(Required(values, "expiry"), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Decimal(values, "strike"), right, Int(values, "lot-size"), Decimal(values, "tick-size"));
                await store.AddContractAsync(contract, cancellationToken);
                await output.WriteLineAsync(JsonSerializer.Serialize(new { status = "option-contract-registered", contractId }));
                return 0;
            }
            var contractRecord = await store.FindContractAsync(contractId, cancellationToken) ??
                throw new ArgumentException("The option contract is not registered.");
            var path = Path.GetFullPath(Required(values, "file"));
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > OptionQuoteCsvReader.MaximumBytes)
                throw new CandleImportException("Option quote CSV exceeds 4 MiB.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false);
            var quotes = await OptionQuoteCsvReader.ReadAsync(reader, contractId, DateTimeOffset.UtcNow, cancellationToken);
            var zone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            if (quotes.Any(quote => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(quote.TimestampUtc, zone)) > contractRecord.ExpiryDate))
                throw new CandleImportException("Option quotes after the contract expiry date are not accepted.");
            if (quotes.Any(quote => !OnTick(quote.Bid, contractRecord.TickSize) ||
                !OnTick(quote.Ask, contractRecord.TickSize) || !OnTick(quote.Last, contractRecord.TickSize)))
                throw new CandleImportException("Every option price must align to the contract tick size.");
            var existing = await store.ReadQuotesAsync([contractId], quotes[0].TimestampUtc,
                new DateTimeOffset(quotes[^1].TimestampUtc, TimeSpan.Zero).AddTicks(1), cancellationToken);
            if (existing.Count > 0) throw new CandleImportException("The option quote range overlaps stored evidence.");
            await store.AddQuotesAsync(quotes, cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(new { status = "option-quotes-imported",
                contractId, rows = quotes.Count, firstUtc = quotes[0].TimestampUtc, lastUtc = quotes[^1].TimestampUtc }));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Option-data command cancelled.");
            return 130;
        }
        catch (Exception exception) when (exception is CandleImportException or ArgumentException or
            FormatException or OverflowException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(exception.Message);
            return 2;
        }
        catch (Exception)
        {
            await error.WriteLineAsync("Option-data persistence failed. Check migrations, foreign keys and duplicate evidence.");
            return 3;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var allowed = args[0] == "add-option-contract"
            ? new[] { "contract-id", "underlying-id", "exchange", "symbol", "expiry", "strike", "right", "lot-size", "tick-size" }
            : new[] { "contract-id", "file" };
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected a named --option.");
            var key = args[index][2..];
            if (!allowed.Contains(key)) throw new ArgumentException($"Unknown option: --{key}");
            if (++index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for --{key}");
            if (!values.TryAdd(key, args[index])) throw new ArgumentException($"Duplicate option: --{key}");
        }
        return values;
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Missing --{key}");
    private static decimal Decimal(IReadOnlyDictionary<string, string> values, string key) =>
        decimal.Parse(Required(values, key), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    private static int Int(IReadOnlyDictionary<string, string> values, string key) =>
        int.Parse(Required(values, key), NumberStyles.None, CultureInfo.InvariantCulture);
    private static bool OnTick(decimal price, decimal tick) => price % tick == 0;
}
