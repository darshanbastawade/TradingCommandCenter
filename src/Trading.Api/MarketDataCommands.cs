using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Trading.Application.MarketData;
using Trading.Domain.MarketData;
using Trading.MarketData.Import;

namespace Trading.Api;

public static class MarketDataCommands
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] is "import-candles" or "add-instrument";

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output,
        TextWriter error, CancellationToken cancellationToken = default)
    {
        try
        {
            var values = Parse(args);
            var id = Guid.Parse(Required(values, "instrument-id"));
            if (args[0] == "add-instrument")
            {
                var instrument = new Instrument(id, Required(values, "exchange"), Required(values, "symbol"),
                    Required(values, "name"), int.Parse(Required(values, "lot-size"), CultureInfo.InvariantCulture),
                    decimal.Parse(Required(values, "tick-size"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture));
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IMarketDataStore>().AddInstrumentAsync(instrument, cancellationToken);
                await output.WriteLineAsync(JsonSerializer.Serialize(new { instrumentId = id, status = "registered" }));
                return 0;
            }
            var minutes = int.Parse(Required(values, "timeframe"), CultureInfo.InvariantCulture);
            var file = Required(values, "file");
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > CandleCsvReader.MaximumBytes) throw new CandleImportException("CSV exceeds 4 MiB.");
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            stream.Position = 0;
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            var dryRun = values.ContainsKey("dry-run");
            CandleImportPreview preview;
            if (dryRun)
                preview = await CandleCsvReader.ReadAsync(reader, id, (Timeframe)minutes, DateTimeOffset.UtcNow, cancellationToken);
            else
            {
                await using var scope = services.CreateAsyncScope();
                preview = await new HistoricalCandleImporter(scope.ServiceProvider.GetRequiredService<IMarketDataStore>())
                    .ImportAsync(reader, id, (Timeframe)minutes, DateTimeOffset.UtcNow, cancellationToken);
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                status = dryRun ? "validated-file-only" : "imported",
                instrumentId = id, timeframeMinutes = minutes, rows = preview.Candles.Count, sha256,
                firstUtc = preview.Candles[0].OpenTimeUtc, lastUtc = preview.Candles[^1].OpenTimeUtc,
                gapCount = preview.GapCount,
                note = "Exchange holidays/session alignment are not validated. Gaps are preserved, never filled."
            }));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Command cancelled. If cancellation occurred during commit, verify the stored range before retrying.");
            return 130;
        }
        catch (Exception exception) when (exception is CandleImportException or ArgumentException or FormatException or OverflowException)
        {
            await error.WriteLineAsync(exception.Message);
            return 2;
        }
        catch (IOException) { await error.WriteLineAsync("Unable to read the CSV file. Check its path and file access."); return 2; }
        catch (UnauthorizedAccessException) { await error.WriteLineAsync("Access to the CSV file was denied."); return 2; }
        catch (Exception)
        {
            await error.WriteLineAsync("Persistence failed. Check database configuration, migrations and duplicate keys. For an interrupted connection, verify the stored range before retrying.");
            return 3;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var allowed = args[0] == "import-candles"
            ? new[] { "file", "instrument-id", "timeframe", "dry-run" }
            : new[] { "instrument-id", "exchange", "symbol", "name", "lot-size", "tick-size" };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected a named --option.");
            var key = args[i][2..];
            if (!allowed.Contains(key)) throw new ArgumentException($"Unknown option: --{key}");
            var value = "true";
            if (key != "dry-run")
            {
                if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing value for --{key}");
                value = args[i];
            }
            if (!result.TryAdd(key, value)) throw new ArgumentException($"Duplicate option: --{key}");
        }
        return result;
    }
    private static string Required(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Missing --{key}");
}
