using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Trading.Application.MarketData;
using Trading.Domain.MarketData;
using Trading.MarketData.Import;
using Trading.MarketData.Quality;

namespace Trading.Api;

public static class MarketDataCommands
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] is "import-candles" or "import-candle-manifest" or "add-instrument";

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
            if (args[0] == "import-candle-manifest")
            {
                var manifestDryRun = values.ContainsKey("dry-run");
                var resume = values.ContainsKey("resume");
                if (manifestDryRun == resume) throw new ArgumentException("Choose exactly one of --dry-run or --resume. --resume commits idempotently and is also used to continue after interruption.");
                if (!values.ContainsKey("confirm-instrument-mapping"))
                    throw new ArgumentException("Instrument metadata has no provider key. Review the manifest key and registered instrument, then explicitly pass --confirm-instrument-mapping.");
                var manifestFile = Required(values, "manifest");
                var calendarKeys = new[] { "calendar", "calendar-id", "calendar-from", "calendar-to" };
                var suppliedCalendarKeys = calendarKeys.Count(values.ContainsKey);
                if (suppliedCalendarKeys is not (0 or 4))
                    throw new ArgumentException("Calendar validation requires all of --calendar, --calendar-id, --calendar-from and --calendar-to.");
                Trading.Domain.MarketData.ExchangeSessionCalendar? calendar = null;
                DateOnly? calendarFrom = null;
                DateOnly? calendarTo = null;
                if (suppliedCalendarKeys == 4)
                {
                    calendarFrom = ParseDate(Required(values, "calendar-from"), "calendar-from");
                    calendarTo = ParseDate(Required(values, "calendar-to"), "calendar-to");
                    if (calendarFrom > calendarTo) throw new ArgumentException("--calendar-from must not follow --calendar-to.");
                    var calendarLines = await File.ReadAllLinesAsync(Required(values, "calendar"), cancellationToken);
                    var declaredScope = DeclaredCalendarScope(calendarLines)
                        ?? throw new ArgumentException("Calendar file must declare scope using '# coverage,yyyy-MM-dd,yyyy-MM-dd' or a calendar year in its title comment.");
                    if (calendarFrom < declaredScope.From || calendarTo > declaredScope.To)
                        throw new ArgumentException($"Requested calendar scope exceeds the file's declared coverage {declaredScope.From:yyyy-MM-dd}..{declaredScope.To:yyyy-MM-dd}.");
                    var entries = DatasetQualityCertifier.ParseCalendar(calendarLines);
                    TimeZoneInfo india;
                    try { india = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
                    catch (TimeZoneNotFoundException) { india = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
                    calendar = new Trading.Domain.MarketData.ExchangeSessionCalendar(Required(values, "calendar-id"),
                        india, new(9, 15), new(15, 30), entries.Holidays, entries.SpecialSessions);
                }
                await using var scope = services.CreateAsyncScope();
                var importer = new CandleManifestImporter(scope.ServiceProvider.GetRequiredService<IMarketDataStore>());
                var summary = await importer.ImportAsync(manifestFile, id, commit: resume, DateTimeOffset.UtcNow,
                    calendar, calendarFrom, calendarTo, cancellationToken);
                await output.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    status = manifestDryRun ? "validated-no-writes" : "imported-and-db-reconciled",
                    instrumentId = id,
                    providerInstrumentKey = summary.ProviderInstrumentKey,
                    registeredInstrument = new
                    {
                        summary.RegisteredExchange,
                        summary.RegisteredSymbol,
                        summary.RegisteredName,
                        summary.RegisteredTickSize
                    },
                    fileValidation = "passed",
                    instrumentValidation = "passed-explicit-mapping-confirmed",
                    databaseValidation = "passed",
                    manifest = Path.GetFullPath(manifestFile),
                    summary.RequestedFrom,
                    summary.RequestedTo,
                    summary.ActualFrom,
                    summary.ActualTo,
                    summary.DownloadedRows,
                    summary.NewlyInsertedRows,
                    summary.IdenticalSkippedRows,
                    wouldInsertRows = manifestDryRun ? summary.DownloadedRows - summary.IdenticalSkippedRows : 0,
                    summary.Conflicts,
                    summary.FailedWindows,
                    summary.EmptyWindows,
                    remainingWork = summary.FailedWindows + summary.EmptyWindows,
                    summary.ZeroVolumeRows,
                    summary.MissingVolumeRows,
                    summary.CalendarValidationStatus,
                    summary.CalendarId,
                    summary.CalendarSha256,
                    summary.ResearchCertified,
                    sessionNote = summary.CalendarValidationStatus == "unverified"
                        ? "Session completeness is unverified; raw data is not research-certified."
                        : "Calendar grid validation is reported separately; raw data is not research-certified."
                }));
                return summary.FailedWindows + summary.EmptyWindows == 0 ? 0 : 3;
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
                instrumentId = id,
                timeframeMinutes = minutes,
                rows = preview.Candles.Count,
                sha256,
                firstUtc = preview.Candles[0].OpenTimeUtc,
                lastUtc = preview.Candles[^1].OpenTimeUtc,
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
        catch (Exception exception) when (exception is CandleImportException or ArgumentException or FormatException or OverflowException or JsonException)
        {
            if (exception is CandleManifestConflictException conflict)
            {
                var summary = conflict.Summary;
                await error.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    status = "failed-conflict",
                    requestedFrom = summary.RequestedFrom,
                    requestedTo = summary.RequestedTo,
                    summary.ActualFrom,
                    summary.ActualTo,
                    summary.DownloadedRows,
                    summary.NewlyInsertedRows,
                    summary.IdenticalSkippedRows,
                    conflicts = summary.Conflicts,
                    summary.FailedWindows,
                    summary.EmptyWindows,
                    remainingWorkRows = summary.DownloadedRows - summary.NewlyInsertedRows - summary.IdenticalSkippedRows,
                    message = conflict.Message,
                    nextStep = "Resolve the conflicting database row, then rerun the manifest import. Existing values were not overwritten."
                }));
                return 3;
            }
            await error.WriteLineAsync(exception.Message);
            return 2;
        }
        catch (IOException) { await error.WriteLineAsync("Unable to read an input file. Check its path and file access."); return 2; }
        catch (UnauthorizedAccessException) { await error.WriteLineAsync("Access to the CSV file was denied."); return 2; }
        catch (Exception)
        {
            await error.WriteLineAsync("Persistence failed. Check database configuration, migrations and duplicate keys. For an interrupted connection, verify the stored range before retrying.");
            return 3;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        string[] allowed = args[0] switch
        {
            "import-candles" => new[] { "file", "instrument-id", "timeframe", "dry-run" },
            "import-candle-manifest" => new[] { "manifest", "instrument-id", "dry-run", "resume", "confirm-instrument-mapping", "calendar", "calendar-id", "calendar-from", "calendar-to" },
            _ => new[] { "instrument-id", "exchange", "symbol", "name", "lot-size", "tick-size" }
        };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected a named --option.");
            var key = args[i][2..];
            if (!allowed.Contains(key)) throw new ArgumentException($"Unknown option: --{key}");
            var value = "true";
            if (key is not ("dry-run" or "resume" or "confirm-instrument-mapping"))
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

    private static DateOnly ParseDate(string value, string option) => DateOnly.TryParseExact(value, "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date : throw new ArgumentException($"--{option} must use yyyy-MM-dd.");

    private static (DateOnly From, DateOnly To)? DeclaredCalendarScope(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var comment = line.Trim();
            if (!comment.StartsWith('#')) continue;
            var fields = comment.TrimStart('#').Split(',').Select(value => value.Trim()).ToArray();
            if (fields.Length == 3 && fields[0].Equals("coverage", StringComparison.OrdinalIgnoreCase) &&
                DateOnly.TryParseExact(fields[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) &&
                DateOnly.TryParseExact(fields[2], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) && from <= to)
                return (from, to);
            var marker = comment.LastIndexOf("calendar", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) continue;
            var suffix = comment[(marker + "calendar".Length)..].Trim().TrimStart('-', ':').Trim();
            if (suffix.Length == 4 && int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 2000 and <= 9998)
                return (new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
        }
        return null;
    }
}
