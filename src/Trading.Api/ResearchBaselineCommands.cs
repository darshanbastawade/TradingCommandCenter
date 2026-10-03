using System.Globalization;
using System.Text.Json;
using Trading.Application.MarketData;
using Trading.Domain.MarketData;
using Trading.MarketData.Quality;
using Trading.Strategies.PriceOnly;

namespace Trading.Api;

/// <summary>Read-only SQL audit and research-only compatibility check. Never inserts candles or trades.</summary>
public static class ResearchBaselineCommands
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "audit-price-baseline";

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output,
        TextWriter error, CancellationToken token = default)
    {
        try
        {
            var options = new Dictionary<string, string>();
            var allowed = new[] { "instrument-id", "from", "to-exclusive", "calendar", "output" };
            for (var i = 1; i < args.Length; i += 2)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || !allowed.Contains(args[i][2..]) ||
                    i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal) ||
                    !options.TryAdd(args[i][2..], args[i + 1])) throw new ArgumentException("Invalid, missing or duplicate argument.");
            }
            string Required(string key) => options.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Missing --{key}.");
            var id = Guid.Parse(Required("instrument-id"));
            var from = DateOnly.ParseExact(Required("from"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var to = DateOnly.ParseExact(Required("to-exclusive"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var zone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
            if (from >= to || to > today) throw new ArgumentException("Use a nonempty range of completed dates; --to-exclusive may be today.");
            var repositoryRoot = FindRepositoryRoot();
            var destination = Path.GetFullPath(Required("output"), repositoryRoot);
            if (File.Exists(destination) || !Directory.Exists(Path.GetDirectoryName(destination)))
                throw new ArgumentException("Output parent must exist and output file must be new.");
            var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
            var calendar = options.TryGetValue("calendar", out var calendarPath)
                ? JsonSerializer.Deserialize<ResearchCalendar>(await File.ReadAllTextAsync(
                    Path.GetFullPath(calendarPath, repositoryRoot), token), json)
                    ?? throw new ArgumentException("Invalid calendar JSON.")
                : ResearchBaseline.Provisional(from, to);
            await using var scope = services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IMarketDataStore>();
            var instrument = await store.FindInstrumentAsync(id, token) ?? throw new ArgumentException("Instrument is not registered.");
            DateTimeOffset Midnight(DateOnly day) => new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);
            var cursor = Midnight(from);
            var end = Midnight(to);
            var candles = new List<Candle>();
            while (cursor < end)
            {
                var page = await store.ReadCandlesAsync(id, Timeframe.Minute1, cursor, end, 10000, token);
                candles.AddRange(page);
                if (page.Count < 10000) break;
                cursor = new DateTimeOffset(page[^1].OpenTimeUtc, TimeSpan.Zero).AddTicks(1);
            }
            var result = ResearchBaseline.Build(candles, id, from, to, calendar, zone);
            var compatible = new List<object>();
            if (result.SelectedSliceComplete)
                foreach (var setup in Enum.GetValues<PriceOnlySetup>())
                {
                    var strategy = new PriceOnlyStrategy(setup);
                    var counts = result.Blocks.Select(block => strategy.Evaluate(block, zone).Count).ToArray();
                    compatible.Add(new
                    {
                        strategy.Id,
                        status = "compatible-price-only",
                        signalCount = counts.Sum(),
                        signalsByHistoryBlock = counts,
                        meaning = "Signal diagnostics only; not a performance or execution backtest."
                    });
                }
            var report = new
            {
                schemaVersion = 1,
                instrumentId = id,
                instrument.Symbol,
                instrument.Name,
                from,
                toExclusive = to,
                result.CalendarVerified,
                result.SelectedSliceComplete,
                result.RawRows,
                result.ZeroVolumeRows,
                result.FiveMinuteRows,
                result.PolicyId,
                result.DatasetSha256,
                indicatorPolicy = "Carry across declared closures; reset all indicators after excluded/invalid dates. Re-warm from native indicator nulls (EMA50 requires 50 complete 5m bars).",
                sessionPolicy = "Initial baseline excludes all nonstandard sessions; their candles remain in raw SQL and their windows are audited.",
                warning = result.CalendarVerified ? "Selected slice completeness does not certify strategy profitability or raw-file provenance."
                    : "PROVISIONAL: weekday gaps may be holidays; missing special dates may be undeclared sessions. Supply a source-verified daily calendar before compatibility evaluation.",
                calendar,
                blockSizes = result.Blocks.Select(b => b.Count).ToArray(),
                statusCounts = result.Days.GroupBy(d => d.Status).ToDictionary(g => g.Key, g => g.Count()),
                days = result.Days,
                strategies = compatible,
                deferredStrategies = new[] { "vwap-ema-trend-breakout-v1", "vwap-reclaim-rejection-v1" }
            };
            await using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, report, json, token);
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                report = destination,
                result.RawRows,
                result.FiveMinuteRows,
                result.CalendarVerified,
                result.SelectedSliceComplete,
                daysNeedingReview = result.Days.Count(d => d.Status.StartsWith("blocked", StringComparison.Ordinal) || d.Status.StartsWith("unverified", StringComparison.Ordinal))
            }));
            return result.SelectedSliceComplete ? 0 : 2;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { await error.WriteLineAsync("Audit cancelled."); return 130; }
        catch (Exception e) when (e is ArgumentException or FormatException or IOException or JsonException or InvalidOperationException)
        { await error.WriteLineAsync(e.Message); return 2; }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TradingCommandCenter.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("TradingCommandCenter.sln could not be located to resolve audit paths.");
    }
}
