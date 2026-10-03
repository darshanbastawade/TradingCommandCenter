using System.Globalization;
using System.Text;
using System.Text.Json;
using Trading.Application.MarketData;
using Trading.Domain.MarketData;

namespace Trading.Api;

public static class PriceBaselineAuditCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "audit-price-baseline";

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output,
        TextWriter error, CancellationToken cancellationToken = default)
    {
        try
        {
            var values = Parse(args);
            var instrumentId = Guid.Parse(Required(values, "instrument-id"));
            var from = ParseDate(Required(values, "from"), "from");
            var toExclusive = ParseDate(Required(values, "to-exclusive"), "to-exclusive");
            if (from >= toExclusive) throw new ArgumentException("--from must precede --to-exclusive.");
            var destination = Path.GetFullPath(Required(values, "output"), FindRepositoryRoot());
            if (!string.Equals(Path.GetExtension(destination), ".json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--output must use the .json extension.");
            if (!Directory.Exists(Path.GetDirectoryName(destination)))
                throw new ArgumentException("The --output directory does not exist.");
            if (File.Exists(destination)) throw new IOException("The output report already exists; choose a new path.");

            var zone = IndiaTimeZone();
            var fromUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(
                from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
            var toUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(
                toExclusive.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
            await using var scope = services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IMarketDataStore>();
            var instrument = await store.FindInstrumentAsync(instrumentId, cancellationToken)
                ?? throw new ArgumentException("The requested instrument is not registered.");
            var timeframeAudits = new List<TimeframePriceAudit>();
            foreach (var timeframe in Enum.GetValues<Timeframe>().OrderBy(value => (int)value))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candles = await ReadAllAsync(store, instrumentId, timeframe, fromUtc, toUtc, cancellationToken);
                timeframeAudits.Add(BuildAudit(timeframe, candles));
            }
            var populated = timeframeAudits.Where(item => item.CandleCount > 0).ToArray();
            var report = new PriceBaselineAuditReport(1, "provisional", DateTimeOffset.UtcNow,
                new(instrument.Id, instrument.Exchange, instrument.Symbol, instrument.Name, instrument.TickSize),
                new(from, toExclusive, fromUtc, toUtc, "India Standard Time"), timeframeAudits,
                new(populated.Sum(item => item.CandleCount), populated.Sum(item => item.ZeroVolumeRows),
                    0),
                false,
                ["Raw database observations only; no exchange calendar or research certification was applied.",
                    "Each timeframe is summarized separately; counts across timeframes are not additive unique observations.",
                    "Volume is non-null in the candle schema. Missing-volume count is therefore zero for stored rows; zero volume is counted as recorded."]);
            await WriteNewAtomicallyAsync(destination, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                status = populated.Length == 0 ? "price-baseline-empty" : "price-baseline-audit-created",
                output = destination,
                instrumentId,
                from,
                toExclusive,
                timeframeGroups = populated.Length,
                candleRowsByTimeframe = populated.Select(item => new { timeframeMinutes = item.TimeframeMinutes, item.CandleCount }),
                zeroVolumeRows = report.Quality.ZeroVolumeRows,
                missingVolumeRows = report.Quality.MissingVolumeRows,
                researchCertified = false
            }));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Price-baseline audit cancelled; no completed report should be assumed.");
            return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(exception.Message);
            return 2;
        }
        catch (Exception)
        {
            await error.WriteLineAsync("Price-baseline audit failed. Check database configuration, connectivity, and the requested instrument.");
            return 3;
        }
    }

    private static TimeframePriceAudit BuildAudit(Timeframe timeframe, IReadOnlyList<Candle> candles)
    {
        if (candles.Count == 0) return new((int)timeframe, 0, null, null, null, null, null, null, null,
            null, null, null, null, null, null, 0, 0, null, null);
        var prices = candles.SelectMany(candle => new[] { candle.Open, candle.High, candle.Low, candle.Close })
            .Order().ToArray();
        var returns = candles.Zip(candles.Skip(1), (previous, current) =>
            previous.Close == 0 ? 0m : (current.Close - previous.Close) / previous.Close * 100m).ToArray();
        return new((int)timeframe, candles.Count, new DateTimeOffset(candles[0].OpenTimeUtc, TimeSpan.Zero),
            new DateTimeOffset(candles[^1].OpenTimeUtc, TimeSpan.Zero), prices[0], prices[^1], Percentile(prices, .01m),
            Percentile(prices, .50m), Percentile(prices, .99m), candles.Min(candle => candle.Open),
            candles.Max(candle => candle.Open), candles.Min(candle => candle.Close), candles.Max(candle => candle.Close),
            returns.Length == 0 ? null : returns.Min(), returns.Length == 0 ? null : returns.Max(),
            candles.Count(candle => candle.Volume == 0), candles.Count(candle => candle.OpenInterest is null),
            candles.Min(candle => candle.Volume), candles.Max(candle => candle.Volume));
    }

    private static decimal Percentile(IReadOnlyList<decimal> sorted, decimal percentile)
    {
        var position = (sorted.Count - 1) * percentile;
        var lower = (int)decimal.Floor(position);
        var upper = (int)decimal.Ceiling(position);
        if (lower == upper) return sorted[lower];
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static async Task<IReadOnlyList<Candle>> ReadAllAsync(IMarketDataStore store, Guid instrumentId,
        Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        const int pageSize = 10_000;
        var candles = new List<Candle>();
        var cursor = from;
        while (cursor < to)
        {
            var page = await store.ReadCandlesAsync(instrumentId, timeframe, cursor, to, pageSize, cancellationToken);
            candles.AddRange(page);
            if (page.Count < pageSize) break;
            cursor = new DateTimeOffset(page[^1].OpenTimeUtc, TimeSpan.Zero).AddTicks(1);
        }
        return candles;
    }

    private static async Task WriteNewAtomicallyAsync(string destination, string contents, CancellationToken cancellationToken)
    {
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, contents, new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, destination, false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var allowed = new HashSet<string>(["instrument-id", "from", "to-exclusive", "output"], StringComparer.Ordinal);
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
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Missing --{key}");

    private static DateOnly ParseDate(string value, string option) => DateOnly.TryParseExact(value, "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date : throw new ArgumentException($"--{option} must use yyyy-MM-dd.");

    private static TimeZoneInfo IndiaTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TradingCommandCenter.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("TradingCommandCenter.sln could not be located to resolve --output.");
    }

    private sealed record PriceBaselineAuditReport(int SchemaVersion, string Status, DateTimeOffset GeneratedAtUtc,
        InstrumentAudit Instrument, RequestedRange Range, IReadOnlyList<TimeframePriceAudit> Timeframes,
        QualitySummary Quality, bool ResearchCertified, IReadOnlyList<string> Warnings);
    private sealed record InstrumentAudit(Guid Id, string Exchange, string Symbol, string Name, decimal TickSize);
    private sealed record RequestedRange(DateOnly From, DateOnly ToExclusive, DateTimeOffset FromUtc,
        DateTimeOffset ToExclusiveUtc, string ExchangeTimeZone);
    private sealed record QualitySummary(int CandleRows, int ZeroVolumeRows, int MissingVolumeRows);
    private sealed record TimeframePriceAudit(int TimeframeMinutes, int CandleCount, DateTimeOffset? FirstOpenTimeUtc,
        DateTimeOffset? LastOpenTimeUtc, decimal? PriceMin, decimal? PriceMax, decimal? PriceP01, decimal? PriceP50,
        decimal? PriceP99, decimal? OpenMin, decimal? OpenMax, decimal? CloseMin, decimal? CloseMax,
        decimal? CloseChangeMinPercent, decimal? CloseChangeMaxPercent, int ZeroVolumeRows, int MissingOpenInterestRows,
        long? VolumeMin, long? VolumeMax);
}