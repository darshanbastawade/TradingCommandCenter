using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trading.Application.MarketData;
using Trading.Domain.MarketData;
using Trading.MarketData.Quality;

namespace Trading.MarketData.Import;

public sealed record CandleManifestImportSummary(int DownloadedRows, int NewlyInsertedRows, int IdenticalSkippedRows,
    int Conflicts, int FailedWindows, int EmptyWindows, DateTimeOffset? ActualFrom, DateTimeOffset? ActualTo,
    int ZeroVolumeRows, int MissingVolumeRows, DateOnly RequestedFrom, DateOnly RequestedTo,
    string ProviderInstrumentKey, string RegisteredExchange, string RegisteredSymbol, string RegisteredName,
    decimal RegisteredTickSize, string CalendarValidationStatus, string? CalendarId, string? CalendarSha256,
    bool ResearchCertified);

public sealed class CandleManifestConflictException(string message, CandleManifestImportSummary summary)
    : CandleImportException(message, summary.Conflicts)
{
    public CandleManifestImportSummary Summary { get; } = summary;
}

public sealed class CandleManifestImporter(IMarketDataStore store)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const int MaximumBatchRows = 9500;

    public async Task<CandleManifestImportSummary> ImportAsync(string manifestPath, Guid instrumentId,
        bool commit, DateTimeOffset now, ExchangeSessionCalendar? calendar = null,
        DateOnly? calendarCoverageFrom = null, DateOnly? calendarCoverageTo = null,
        CancellationToken cancellationToken = default)
    {
        if (instrumentId == Guid.Empty) throw new CandleImportException("A nonempty instrument ID is required.");
        var fullManifestPath = Path.GetFullPath(manifestPath);
        await using var manifestStream = new FileStream(fullManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var manifest = await JsonSerializer.DeserializeAsync<Manifest>(manifestStream, JsonOptions, cancellationToken)
            ?? throw new CandleImportException("Manifest is empty or invalid.");
        ValidateManifestShape(manifest);
        var datasetRoot = Path.GetDirectoryName(fullManifestPath)!;
        var expectedWindows = BuildWindows(manifest.RequestedFrom, manifest.RequestedTo);
        if (manifest.Windows.Count != expectedWindows.Count || !manifest.Windows.Select(x => (x.From, x.To))
                .SequenceEqual(expectedWindows))
            throw new CandleImportException("Manifest windows do not exactly cover the requested monthly date range.");

        var instrument = await store.FindInstrumentAsync(instrumentId, cancellationToken)
            ?? throw new CandleImportException("Selected instrument does not exist. Register it before importing.");
        var allCandles = new List<Candle>();
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileFirst = new List<DateTimeOffset>();
        var fileLast = new List<DateTimeOffset>();
        var verifiedZeroVolume = 0;
        foreach (var window in manifest.Windows)
        {
            if (window.Status == "failed" || window.Status == "empty")
            {
                if (window.RowCount != 0 || window.Files.Count != 0)
                    throw new CandleImportException($"{window.Status} window {window.From:yyyy-MM} contains file evidence.");
                continue;
            }
            var windowRows = 0;
            foreach (var evidence in window.Files)
            {
                var path = ResolveManifestPath(datasetRoot, evidence.RelativePath);
                if (!filePaths.Add(path)) throw new CandleImportException("Manifest contains a duplicate file path.");
                if (!File.Exists(path)) throw new CandleImportException($"Manifest file is missing: {evidence.RelativePath}");
                var info = new FileInfo(path);
                if (info.Length > CandleCsvReader.MaximumBytes) throw new CandleImportException($"CSV exceeds 4 MiB: {evidence.RelativePath}");
                var hash = await HashAsync(path, cancellationToken);
                if (!string.Equals(hash, evidence.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new CandleImportException($"SHA-256 mismatch for {evidence.RelativePath}.");
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
                var preview = await CandleCsvReader.ReadAsync(reader, instrumentId, (Timeframe)manifest.IntervalMinutes,
                    now, cancellationToken);
                if (preview.Candles.Count != evidence.RowCount || preview.Candles.Count > MaximumBatchRows)
                    throw new CandleImportException($"Manifest row count does not match {evidence.RelativePath}.");
                var first = new DateTimeOffset(preview.Candles[0].OpenTimeUtc, TimeSpan.Zero);
                var last = new DateTimeOffset(preview.Candles[^1].OpenTimeUtc, TimeSpan.Zero);
                if (first != evidence.FirstTimestamp || last != evidence.LastTimestamp)
                    throw new CandleImportException($"Manifest timestamp bounds do not match {evidence.RelativePath}.");
                var fileZeroVolume = preview.Candles.Count(candle => candle.Volume == 0);
                if (fileZeroVolume != evidence.ZeroVolumeRows)
                    throw new CandleImportException($"Manifest volume statistics do not match {evidence.RelativePath}.");
                foreach (var candle in preview.Candles)
                {
                    var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(candle.OpenTimeUtc, IndiaTimeZone()));
                    if (localDate < window.From || localDate > window.To)
                        throw new CandleImportException($"Candle in {evidence.RelativePath} is outside its manifest window.");
                    if (new[] { candle.Open, candle.High, candle.Low, candle.Close }.Any(price => price % instrument.TickSize != 0))
                        throw new CandleImportException($"A candle price in {evidence.RelativePath} is not a multiple of registered tick size {instrument.TickSize}. Index prices are not exempt from this rule.");
                }
                verifiedZeroVolume += preview.Candles.Count(candle => candle.Volume == 0);
                fileFirst.Add(first);
                fileLast.Add(last);
                windowRows += preview.Candles.Count;
                allCandles.AddRange(preview.Candles);
            }
            if (window.Status != "completed" || windowRows == 0 || windowRows != window.RowCount || window.Files.Count == 0)
                throw new CandleImportException($"Completed window {window.From:yyyy-MM} has incomplete or empty file evidence.");
            var windowCandles = allCandles.Skip(allCandles.Count - windowRows).ToArray();
            if (window.FirstTimestamp != fileFirst[^window.Files.Count] || window.LastTimestamp != fileLast[^window.Files.Count] ||
                windowCandles[0].OpenTimeUtc != window.FirstTimestamp?.UtcDateTime || windowCandles[^1].OpenTimeUtc != window.LastTimestamp?.UtcDateTime)
                throw new CandleImportException($"Manifest window timestamp bounds do not match {window.From:yyyy-MM}.");
        }
        if (allCandles.Count != manifest.TotalRows || verifiedZeroVolume != manifest.ZeroVolumeRows || manifest.MissingVolumeRows != 0)
            throw new CandleImportException("Manifest total or volume quality statistics do not match verified CSV rows.");
        if (allCandles.Count > 0 && (manifest.ActualFrom != fileFirst.Min() || manifest.ActualTo != fileLast.Max()))
            throw new CandleImportException("Manifest actual returned range does not match its files.");
        if (allCandles.Count == 0 && (manifest.ActualFrom is not null || manifest.ActualTo is not null))
            throw new CandleImportException("Manifest declares an actual range but has no completed rows.");
        foreach (var csv in Directory.EnumerateFiles(datasetRoot, "*.csv", SearchOption.AllDirectories))
            if (!filePaths.Contains(Path.GetFullPath(csv)))
                throw new CandleImportException($"Unmanifested CSV detected; refusing to trust filenames: {Path.GetRelativePath(datasetRoot, csv)}");
        var duration = TimeSpan.FromMinutes(manifest.IntervalMinutes);
        for (var index = 1; index < allCandles.Count; index++)
        {
            var difference = allCandles[index].OpenTimeUtc - allCandles[index - 1].OpenTimeUtc;
            if (difference <= TimeSpan.Zero) throw new CandleImportException("Manifest files contain duplicate or reversed timestamps.");
            if (difference < duration) throw new CandleImportException("Manifest files contain overlapping candles.");
        }

        var calendarStatus = "unverified";
        DatasetQualityCertificate? certificate = null;
        var hasCoverage = calendar is not null || calendarCoverageFrom is not null || calendarCoverageTo is not null;
        if (hasCoverage)
        {
            if (calendar is null || calendarCoverageFrom is null || calendarCoverageTo is null ||
                calendarCoverageFrom > manifest.RequestedFrom || calendarCoverageTo < manifest.RequestedTo)
                throw new CandleImportException("Calendar coverage must declare inclusive dates containing the entire manifest requested range.");
            certificate = DatasetQualityCertifier.Certify(allCandles, instrumentId,
                (Timeframe)manifest.IntervalMinutes, manifest.RequestedFrom, manifest.RequestedTo.AddDays(1),
                calendar, "Upstox", "historical-candle-v3").Certificate;
            calendarStatus = certificate.Passed ? "calendar-validation-passed" : "calendar-validation-failed";
        }

        var inserted = 0;
        var skipped = 0;
        var conflicts = 0;
        CandleManifestImportSummary Summary() => new(manifest.TotalRows, inserted, skipped, conflicts,
            manifest.Windows.Count(x => x.Status == "failed"), manifest.Windows.Count(x => x.Status == "empty"),
            manifest.ActualFrom, manifest.ActualTo, verifiedZeroVolume, manifest.MissingVolumeRows,
            manifest.RequestedFrom, manifest.RequestedTo, manifest.InstrumentKey, instrument.Exchange,
            instrument.Symbol, instrument.Name, instrument.TickSize, calendarStatus, certificate?.CalendarId,
            certificate?.CalendarSha256, ResearchCertified: false);

        foreach (var batch in allCandles.Chunk(MaximumBatchRows))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = new DateTimeOffset(batch[0].OpenTimeUtc, TimeSpan.Zero);
            var end = new DateTimeOffset(batch[^1].OpenTimeUtc, TimeSpan.Zero).AddTicks(1);
            var existing = await ReadExistingAsync(instrumentId, (Timeframe)manifest.IntervalMinutes,
                start, end, cancellationToken);
            var byTime = existing.ToDictionary(candle => candle.OpenTimeUtc);
            var toInsert = new List<Candle>();
            foreach (var candle in batch)
            {
                if (!byTime.TryGetValue(candle.OpenTimeUtc, out var stored)) toInsert.Add(candle);
                else if (Equivalent(stored, candle)) skipped++;
                else
                {
                    conflicts++;
                    throw new CandleManifestConflictException($"Conflicting existing candle at {candle.OpenTimeUtc:O}; no values were overwritten.", Summary());
                }
            }
            if (toInsert.Count == 0 || !commit) continue;
            try
            {
                await store.AddCandlesAsync(toInsert, cancellationToken);
                inserted += toInsert.Count;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                var reconciled = await ReadExistingAsync(instrumentId, (Timeframe)manifest.IntervalMinutes,
                    start, end, cancellationToken);
                var reconciledByTime = reconciled.ToDictionary(candle => candle.OpenTimeUtc);
                var missing = new List<Candle>();
                foreach (var candle in toInsert)
                {
                    if (!reconciledByTime.TryGetValue(candle.OpenTimeUtc, out var stored)) missing.Add(candle);
                    else if (!Equivalent(stored, candle))
                    {
                        conflicts++;
                        throw new CandleManifestConflictException($"Conflicting candle observed while reconciling an interrupted or concurrent commit at {candle.OpenTimeUtc:O}.", Summary());
                    }
                }
                skipped += toInsert.Count - missing.Count;
                if (missing.Count == 0) continue;
                try { await store.AddCandlesAsync(missing, cancellationToken); inserted += missing.Count; }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    var final = await ReadExistingAsync(instrumentId, (Timeframe)manifest.IntervalMinutes,
                        start, end, cancellationToken);
                    var finalByTime = final.ToDictionary(candle => candle.OpenTimeUtc);
                    foreach (var candle in missing)
                    {
                        if (!finalByTime.TryGetValue(candle.OpenTimeUtc, out var stored))
                            throw new CandleImportException("A DB batch failed and reconciliation found candles still missing. Retry the manifest import; no local progress file is trusted.");
                        if (!Equivalent(stored, candle))
                        {
                            conflicts++;
                            throw new CandleManifestConflictException($"Conflicting candle after concurrent insertion at {candle.OpenTimeUtc:O}; no values were overwritten.", Summary());
                        }
                        skipped++;
                    }
                }
            }
        }
        return Summary();
    }

    private static void ValidateManifestShape(Manifest manifest)
    {
        if (manifest.SchemaVersion != 2 || manifest.Provider != "Upstox" || manifest.ProviderApi != "historical-candle-v3")
            throw new CandleImportException("Unsupported manifest schema or provider API.");
        if (string.IsNullOrWhiteSpace(manifest.InstrumentKey) || manifest.IntervalMinutes is not (1 or 5))
            throw new CandleImportException("Manifest instrument or timeframe is invalid.");
        if (manifest.RequestedFrom < new DateOnly(2022, 1, 1) || manifest.RequestedFrom > manifest.RequestedTo)
            throw new CandleImportException("Manifest requested range is invalid or outside documented Upstox V3 availability.");
        if (manifest.TotalRows < 0 || manifest.ZeroVolumeRows < 0 || manifest.MissingVolumeRows < 0)
            throw new CandleImportException("Manifest statistics cannot be negative.");
        if (manifest.Windows.Any(x => x.Status is not ("completed" or "empty" or "failed")))
            throw new CandleImportException("Manifest contains an unsupported window status.");
    }

    private static IReadOnlyList<(DateOnly From, DateOnly To)> BuildWindows(DateOnly from, DateOnly to)
    {
        var result = new List<(DateOnly, DateOnly)>();
        while (from <= to)
        {
            var end = new DateOnly(from.Year, from.Month, 1).AddMonths(1).AddDays(-1);
            if (end > to) end = to;
            result.Add((from, end));
            from = end.AddDays(1);
        }
        return result;
    }

    private static string ResolveManifestPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new CandleImportException("Manifest paths must be relative.");
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new CandleImportException("Manifest path escapes its dataset directory.");
        var current = Path.GetFullPath(root);
        foreach (var segment in Path.GetRelativePath(current, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new CandleImportException("Manifest paths may not traverse symbolic links or reparse points.");
        }
        return full;
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }

    private static bool Equivalent(Candle left, Candle right) => left.InstrumentId == right.InstrumentId &&
        left.Timeframe == right.Timeframe && left.OpenTimeUtc == right.OpenTimeUtc && left.Open == right.Open &&
        left.High == right.High && left.Low == right.Low && left.Close == right.Close && left.Volume == right.Volume &&
        left.OpenInterest == right.OpenInterest;

    private async Task<IReadOnlyList<Candle>> ReadExistingAsync(Guid instrumentId, Timeframe timeframe,
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var result = new List<Candle>();
        var cursor = from;
        while (cursor < to)
        {
            var page = await store.ReadCandlesAsync(instrumentId, timeframe, cursor, to, 10000, cancellationToken);
            result.AddRange(page);
            if (page.Count < 10000) break;
            cursor = new DateTimeOffset(page[^1].OpenTimeUtc, TimeSpan.Zero).AddTicks(1);
        }
        return result;
    }

    private static TimeZoneInfo IndiaTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
    }

    private sealed class Manifest
    {
        public int SchemaVersion { get; set; }
        public string Provider { get; set; } = "";
        public string ProviderApi { get; set; } = "";
        public string InstrumentKey { get; set; } = "";
        public int IntervalMinutes { get; set; }
        public DateOnly RequestedFrom { get; set; }
        public DateOnly RequestedTo { get; set; }
        public DateTimeOffset? ActualFrom { get; set; }
        public DateTimeOffset? ActualTo { get; set; }
        public DateTimeOffset DownloadedAtUtc { get; set; }
        public int TotalRows { get; set; }
        public int ZeroVolumeRows { get; set; }
        public int MissingVolumeRows { get; set; }
        public string SessionCompleteness { get; set; } = "unverified";
        public List<string> CoverageWarnings { get; set; } = [];
        public List<Window> Windows { get; set; } = [];
    }

    private sealed class Window
    {
        public DateOnly From { get; set; }
        public DateOnly To { get; set; }
        public string Status { get; set; } = "";
        public int RowCount { get; set; }
        public DateTimeOffset? FirstTimestamp { get; set; }
        public DateTimeOffset? LastTimestamp { get; set; }
        public List<FileEvidence> Files { get; set; } = [];
    }

    private sealed class FileEvidence
    {
        public string RelativePath { get; set; } = "";
        public int RowCount { get; set; }
        public DateTimeOffset FirstTimestamp { get; set; }
        public DateTimeOffset LastTimestamp { get; set; }
        public string Sha256 { get; set; } = "";
        public int ZeroVolumeRows { get; set; }
    }
}