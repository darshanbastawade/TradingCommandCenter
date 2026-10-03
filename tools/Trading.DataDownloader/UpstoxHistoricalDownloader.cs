using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Trading.DataDownloader;

public sealed record DownloadOptions(string InstrumentKey, int IntervalMinutes, DateOnly From, DateOnly To,
    string OutputDirectory, bool Resume, bool ShowHelp = false)
{
    public const string Usage = "Usage: Trading.DataDownloader --instrument-key <key> --interval <1|5> --from yyyy-MM-dd --to yyyy-MM-dd --output <directory> [--resume] [--help]";

    public static DownloadOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var allowed = new HashSet<string>(["instrument-key", "interval", "from", "to", "output", "resume", "help"], StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument: {argument}");
            var key = argument[2..];
            if (!allowed.Contains(key)) throw new ArgumentException($"Unsupported argument: --{key}");
            if (key is "resume" or "help")
            {
                if (!flags.Add(key)) throw new ArgumentException($"Duplicate argument: --{key}");
                continue;
            }
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for --{key}");
            if (!values.TryAdd(key, args[++index])) throw new ArgumentException($"Duplicate argument: --{key}");
        }
        if (flags.Contains("help"))
        {
            if (args.Length != 1) throw new ArgumentException("--help must be used by itself.");
            return new("", 1, default, default, "", false, true);
        }
        string Required(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Missing --{key}");
        if (!int.TryParse(Required("interval"), NumberStyles.None, CultureInfo.InvariantCulture, out var interval) || interval is not (1 or 5))
            throw new ArgumentException("--interval must be 1 or 5.");
        static DateOnly ParseDate(string value, string option) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : throw new ArgumentException($"--{option} must use yyyy-MM-dd.");
        var from = ParseDate(Required("from"), "from");
        var to = ParseDate(Required("to"), "to");
        if (string.IsNullOrWhiteSpace(Required("instrument-key"))) throw new ArgumentException("--instrument-key cannot be empty.");
        if (from < new DateOnly(2022, 1, 1)) throw new ArgumentException("Upstox Historical Candle V3 minute history is documented as available from 2022-01-01; requested --from is outside documented availability.");
        if (from > to) throw new ArgumentException("--from must be on or before --to.");
        var india = UpstoxHistoricalDownloader.IndiaTimeZone();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, india).DateTime);
        if (from >= today || to >= today) throw new ArgumentException($"Historical backfill dates must be completed exchange dates before today ({today:yyyy-MM-dd} India time). Current-session ingestion is not supported.");
        var output = Path.GetFullPath(Required("output"));
        return new(Required("instrument-key").Trim(), interval, from, to, output, flags.Contains("resume"));
    }
}

public sealed class UpstoxAuthenticationException(string message) : Exception(message);

public sealed record DownloadSummary(int TotalRows, int ZeroVolumeRows, int MissingVolumeRows,
    int CompletedWindows, int EmptyWindows, int FailedWindows);

public sealed class UpstoxHistoricalDownloader(HttpClient httpClient)
{
    public const int MaximumRowsPerFile = 9500;
    public const int MaximumBytesPerFile = 4 * 1024 * 1024;
    private const int MaximumRetries = 4;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static TimeZoneInfo IndiaTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
    }

    public static Uri BuildRequestUri(string instrumentKey, int interval, DateOnly from, DateOnly to) =>
        new($"https://api.upstox.com/v3/historical-candle/{Uri.EscapeDataString(instrumentKey)}/minutes/{interval}/{to:yyyy-MM-dd}/{from:yyyy-MM-dd}");

    public async Task<DownloadSummary> DownloadAsync(DownloadOptions options, TextWriter output,
        CancellationToken cancellationToken = default)
    {
        Validate(options);
        var keySlug = SafeSlug(options.InstrumentKey);
        var dataset = Path.Combine(options.OutputDirectory, keySlug, $"{options.IntervalMinutes}m");
        Directory.CreateDirectory(dataset);
        var manifestPath = Path.Combine(dataset, "manifest.json");
        DownloadManifest manifest;
        if (options.Resume)
        {
            if (!File.Exists(manifestPath)) throw new InvalidDataException("--resume requires an existing manifest at " + manifestPath);
            manifest = await ReadManifestAsync(manifestPath, cancellationToken);
            VerifyIdentity(manifest, options);
            VerifyExistingFiles(dataset, manifest);
        }
        else
        {
            if (File.Exists(manifestPath)) throw new IOException($"Dataset manifest already exists: {manifestPath}. Use --resume or choose another output directory.");
            manifest = NewManifest(options);
        }
        var windows = BuildWindows(options.From, options.To);
        if (manifest.Windows.Count > windows.Count || !manifest.Windows.Select(x => (x.From, x.To))
            .SequenceEqual(windows.Take(manifest.Windows.Count).Select(x => (x.From, x.To))))
            throw new InvalidDataException("Manifest windows do not match the requested date range.");
        foreach (var window in windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prior = manifest.Windows.SingleOrDefault(x => x.From == window.From && x.To == window.To);
            if (prior?.Status == "completed") continue;
            var item = prior ?? new WindowEvidence(window.From, window.To, "failed", 0, null, null, []);
            if (prior is null) manifest.Windows.Add(item);
            item.Status = "failed";
            await WriteManifestAsync(manifestPath, manifest, cancellationToken);
            await output.WriteAsync($"Downloading {window.From:yyyy-MM-dd} to {window.To:yyyy-MM-dd} ... ");
            try
            {
                var candles = await DownloadWindowAsync(options, window, cancellationToken);
                if (candles.Count == 0)
                {
                    item.Status = "empty";
                    item.RowCount = 0;
                    item.Files = [];
                    item.FirstTimestamp = null;
                    item.LastTimestamp = null;
                }
                else
                {
                    var files = await WriteWindowFilesAsync(dataset, keySlug, options.IntervalMinutes, window, candles,
                        options.Resume, cancellationToken);
                    item.Status = "completed";
                    item.RowCount = candles.Count;
                    item.FirstTimestamp = candles[0].Timestamp;
                    item.LastTimestamp = candles[^1].Timestamp;
                    item.Files = files;
                }
                await WriteManifestAsync(manifestPath, manifest, cancellationToken);
                await output.WriteLineAsync(item.Status == "empty" ? "empty (not certified as coverage)" : $"{item.RowCount:N0} candles");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await WriteManifestAsync(manifestPath, manifest, CancellationToken.None);
                throw;
            }
            catch (UpstoxAuthenticationException)
            {
                await WriteManifestAsync(manifestPath, manifest, CancellationToken.None);
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or IOException or JsonException)
            {
                item.Status = "failed";
                item.Error = SafeError(exception.Message);
                await WriteManifestAsync(manifestPath, manifest, CancellationToken.None);
                await output.WriteLineAsync("failed: " + item.Error);
            }
        }
        manifest.DownloadedAtUtc = DateTimeOffset.UtcNow;
        manifest.TotalRows = manifest.Windows.Where(x => x.Status == "completed").Sum(x => x.RowCount);
        manifest.ZeroVolumeRows = manifest.Windows.SelectMany(x => x.Files).Sum(x => x.ZeroVolumeRows);
        var timestamps = manifest.Windows.Where(x => x.Status == "completed").SelectMany(x => x.Files).ToArray();
        manifest.ActualFrom = timestamps.Length == 0 ? null : timestamps.Min(x => x.FirstTimestamp);
        manifest.ActualTo = timestamps.Length == 0 ? null : timestamps.Max(x => x.LastTimestamp);
        await WriteManifestAsync(manifestPath, manifest, cancellationToken);
        var referenced = manifest.Windows.Where(x => x.Status == "completed").SelectMany(x => x.Files)
            .Select(x => Path.GetFullPath(ResolveInside(dataset, x.RelativePath))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var csv in Directory.EnumerateFiles(dataset, "*.csv", SearchOption.AllDirectories))
            if (!referenced.Contains(Path.GetFullPath(csv))) throw new InvalidDataException($"Unmanifested CSV detected; refusing to trust filenames: {Path.GetRelativePath(dataset, csv)}");
        return new(manifest.TotalRows, manifest.ZeroVolumeRows, manifest.MissingVolumeRows,
            manifest.Windows.Count(x => x.Status == "completed"),
            manifest.Windows.Count(x => x.Status == "empty"), manifest.Windows.Count(x => x.Status == "failed"));
    }

    public static IReadOnlyList<(DateOnly From, DateOnly To)> BuildWindows(DateOnly from, DateOnly to)
    {
        var result = new List<(DateOnly, DateOnly)>();
        var cursor = from;
        while (cursor <= to)
        {
            var end = new DateOnly(cursor.Year, cursor.Month, 1).AddMonths(1).AddDays(-1);
            if (end > to) end = to;
            result.Add((cursor, end));
            cursor = end.AddDays(1);
        }
        return result;
    }

    private static void Validate(DownloadOptions options)
    {
        if (options.IntervalMinutes is not (1 or 5)) throw new ArgumentException("Only intervals 1 and 5 are supported.");
        if (string.IsNullOrWhiteSpace(options.InstrumentKey) || options.InstrumentKey.Contains('\0')) throw new ArgumentException("A valid instrument key is required.");
        if (options.From < new DateOnly(2022, 1, 1)) throw new ArgumentException("Upstox Historical Candle V3 minute history is documented from 2022-01-01.");
        if (options.From > options.To) throw new ArgumentException("The start date must not follow the end date.");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, IndiaTimeZone()).DateTime);
        if (options.From >= today || options.To >= today) throw new ArgumentException($"Historical backfill dates must be completed exchange dates before today ({today:yyyy-MM-dd} India time).");
        if (string.IsNullOrWhiteSpace(options.OutputDirectory)) throw new ArgumentException("An output directory is required.");
    }

    private async Task<List<DownloadedCandle>> DownloadWindowAsync(DownloadOptions options,
        (DateOnly From, DateOnly To) window, CancellationToken token)
    {
        using var response = await SendWithRetryAsync(BuildRequestUri(options.InstrumentKey, options.IntervalMinutes, window.From, window.To), token);
        var json = await response.Content.ReadAsStringAsync(token);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "success" ||
            !root.TryGetProperty("data", out var data) || !data.TryGetProperty("candles", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Upstox response must contain status=success and data.candles.");
        var items = new List<DownloadedCandle>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Upstox returned a malformed candle.");
            var values = item.EnumerateArray().ToArray();
            if (values.Length < 6 || values[0].ValueKind != JsonValueKind.String || values.Skip(1).Take(5).Any(x => x.ValueKind != JsonValueKind.Number))
                throw new InvalidDataException("Upstox candle is missing timestamp, OHLC or volume; no values will be fabricated.");
            var timestamp = DateTimeOffset.Parse(values[0].GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var candle = new DownloadedCandle(timestamp, values[1].GetDecimal(), values[2].GetDecimal(), values[3].GetDecimal(), values[4].GetDecimal(), values[5].GetInt64(),
                values.Length > 6 && values[6].ValueKind == JsonValueKind.Number ? values[6].GetInt64() : null);
            ValidateCandle(candle, window, options.IntervalMinutes);
            items.Add(candle);
        }
        items.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        for (var index = 1; index < items.Count; index++)
            if (items[index - 1].Timestamp == items[index].Timestamp) throw new InvalidDataException($"Duplicate timestamp returned by Upstox: {items[index].Timestamp:O}");
        return items;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Uri uri, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try { response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token); }
            catch (HttpRequestException) when (attempt < MaximumRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << attempt)), token);
                continue;
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested && attempt < MaximumRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << attempt)), token);
                continue;
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                response.Dispose();
                throw new UpstoxAuthenticationException("Upstox rejected authentication (401/403). Refresh UPSTOX_ACCESS_TOKEN and retry; the token was not logged or written.");
            }
            if ((response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500) && attempt < MaximumRetries)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromMilliseconds(250 * (1 << attempt));
                response.Dispose();
                await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds, 30)), token);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"Upstox returned HTTP {statusCode} after {attempt + 1} attempt(s).");
            }
            return response;
        }
    }

    private static async Task<List<FileEvidence>> WriteWindowFilesAsync(string dataset, string slug, int interval,
        (DateOnly From, DateOnly To) window, IReadOnlyList<DownloadedCandle> candles, bool resume, CancellationToken token)
    {
        var directory = Path.Combine(dataset, window.From.Year.ToString(CultureInfo.InvariantCulture), window.From.ToString("MM", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        var result = new List<FileEvidence>();
        var chunk = new List<DownloadedCandle>();
        var chunkBytes = Encoding.UTF8.GetByteCount(CandleCsvReaderHeader);
        var part = 1;
        foreach (var candle in candles)
        {
            var line = CsvLine(candle) + "\n";
            var bytes = Encoding.UTF8.GetByteCount(line);
            if (bytes + Encoding.UTF8.GetByteCount(CandleCsvReaderHeader) > MaximumBytesPerFile) throw new InvalidDataException("A candle row exceeds the 4 MiB CSV limit.");
            if (chunk.Count == MaximumRowsPerFile || chunkBytes + bytes > MaximumBytesPerFile)
            {
                result.Add(await WriteChunkAsync(dataset, directory, slug, interval, window, part++, chunk, resume, token));
                chunk = [];
                chunkBytes = Encoding.UTF8.GetByteCount(CandleCsvReaderHeader);
            }
            chunk.Add(candle);
            chunkBytes += bytes;
        }
        if (chunk.Count > 0) result.Add(await WriteChunkAsync(dataset, directory, slug, interval, window, part, chunk, resume, token));
        return result;
    }

    private static async Task<FileEvidence> WriteChunkAsync(string dataset, string directory, string slug, int interval,
        (DateOnly From, DateOnly To) window, int part, IReadOnlyList<DownloadedCandle> candles, bool resume,
        CancellationToken token)
    {
        var filename = $"{slug}-{interval}m-{window.From:yyyy-MM}-{part:00}.csv";
        var path = Path.Combine(directory, filename);
        var temporary = path + ".tmp";
        var builder = new StringBuilder(CandleCsvReaderHeader + "\n");
        foreach (var candle in candles) builder.Append(CsvLine(candle)).Append('\n');
        var contents = builder.ToString();
        if (candles.Count > MaximumRowsPerFile || Encoding.UTF8.GetByteCount(contents) > MaximumBytesPerFile)
            throw new InvalidDataException("Generated CSV exceeded an enforced row or byte limit.");
        if (File.Exists(path))
        {
            if (!resume) throw new IOException($"Output already exists: {path}. Use --resume to verify and reuse matching output.");
            var existingContents = await File.ReadAllTextAsync(path, new UTF8Encoding(false, true), token);
            if (!string.Equals(existingContents, contents, StringComparison.Ordinal))
                throw new InvalidDataException($"Existing output was not recorded in the manifest and differs from the requested batch: {path}");
        }
        else
        {
            await File.WriteAllTextAsync(temporary, contents, new UTF8Encoding(false), token);
            File.Move(temporary, path, false);
        }
        var relative = Path.GetRelativePath(dataset, path);
        return new(relative, candles.Count, candles[0].Timestamp, candles[^1].Timestamp,
            await HashAsync(path, token), candles.Count(x => x.Volume == 0));
    }

    private static void ValidateCandle(DownloadedCandle candle, (DateOnly From, DateOnly To) window, int interval)
    {
        if (candle.Timestamp.UtcTicks % TimeSpan.TicksPerMinute != 0) throw new InvalidDataException($"Timestamp is not aligned to a whole minute: {candle.Timestamp:O}");
        var india = TimeZoneInfo.ConvertTime(candle.Timestamp, IndiaTimeZone());
        var localDate = DateOnly.FromDateTime(india.DateTime);
        if (localDate < window.From || localDate > window.To) throw new InvalidDataException($"Timestamp {candle.Timestamp:O} is outside requested window {window.From:yyyy-MM-dd}..{window.To:yyyy-MM-dd} India time.");
        if (candle.Timestamp.AddMinutes(interval) > DateTimeOffset.UtcNow) throw new InvalidDataException($"Uncompleted candle returned at {candle.Timestamp:O}.");
        if (candle.Open <= 0 || candle.High <= 0 || candle.Low <= 0 || candle.Close <= 0 || candle.High < Math.Max(candle.Open, candle.Close) || candle.Low > Math.Min(candle.Open, candle.Close) || candle.High < candle.Low)
            throw new InvalidDataException($"Invalid OHLC relationship at {candle.Timestamp:O}.");
        if (candle.Open > 99999999999999.9999m || candle.High > 99999999999999.9999m || candle.Low > 99999999999999.9999m || candle.Close > 99999999999999.9999m ||
            new[] { candle.Open, candle.High, candle.Low, candle.Close }.Any(value => decimal.Round(value, 4) != value))
            throw new InvalidDataException($"Price precision exceeds the database's supported four decimal places at {candle.Timestamp:O}.");
        if (candle.Volume < 0 || candle.OpenInterest is < 0) throw new InvalidDataException($"Negative volume or open interest at {candle.Timestamp:O}.");
    }

    private static string CsvLine(DownloadedCandle candle) => string.Join(',',
        candle.Timestamp.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
        Price(candle.Open), Price(candle.High), Price(candle.Low), Price(candle.Close),
        candle.Volume.ToString(CultureInfo.InvariantCulture), candle.OpenInterest?.ToString(CultureInfo.InvariantCulture) ?? "");
    private static string Price(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    private const string CandleCsvReaderHeader = "timestamp,open,high,low,close,volume,openInterest";
    private static string SafeSlug(string key)
    {
        var slug = new string(key.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        if (slug.Length == 0) throw new ArgumentException("Instrument key does not contain a filesystem-safe identifier.");
        return slug.Length > 100 ? slug[..100] : slug;
    }
    private static string SafeError(string message) => message.Length <= 500 ? message : message[..500];
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }
    private static async Task<DownloadManifest> ReadManifestAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<DownloadManifest>(stream, JsonOptions, token)
            ?? throw new InvalidDataException("Manifest is empty or invalid.");
    }
    private static DownloadManifest NewManifest(DownloadOptions options) => new()
    {
        SchemaVersion = 2,
        Provider = "Upstox",
        ProviderApi = "historical-candle-v3",
        InstrumentKey = options.InstrumentKey,
        IntervalMinutes = options.IntervalMinutes,
        RequestedFrom = options.From,
        RequestedTo = options.To,
        DownloadedAtUtc = DateTimeOffset.UtcNow,
        CoverageWarnings = ["Exchange session completeness is unverified; this is raw provider data and is not research-certified.", "Zero-volume index candles may be unsuitable for VWAP and other volume-dependent strategies.", "Missing-volume candles are rejected rather than replaced; missingVolumeRows counts accepted records and is zero."],
        SessionCompleteness = "unverified"
    };
    private static void VerifyIdentity(DownloadManifest manifest, DownloadOptions options)
    {
        if (manifest.SchemaVersion != 2 || manifest.Provider != "Upstox" || manifest.ProviderApi != "historical-candle-v3" ||
            manifest.InstrumentKey != options.InstrumentKey || manifest.IntervalMinutes != options.IntervalMinutes ||
            manifest.RequestedFrom != options.From || manifest.RequestedTo != options.To)
            throw new InvalidDataException("--resume manifest identity or parameters do not match this request.");
    }
    private static void VerifyExistingFiles(string dataset, DownloadManifest manifest)
    {
        if (Directory.EnumerateFiles(dataset, "*.tmp", SearchOption.AllDirectories).Any()) throw new InvalidDataException("Incomplete temporary output detected; inspect and remove it before resuming.");
        foreach (var evidence in manifest.Windows.Where(x => x.Status == "completed").SelectMany(x => x.Files))
        {
            var path = ResolveInside(dataset, evidence.RelativePath);
            if (!File.Exists(path)) throw new InvalidDataException($"Manifest file is missing: {evidence.RelativePath}");
            var info = new FileInfo(path);
            if (info.Length > MaximumBytesPerFile || !string.Equals(HashFile(path), evidence.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Manifest hash or byte-size mismatch: {evidence.RelativePath}");
            var rows = File.ReadLines(path).Skip(1).Count();
            if (rows != evidence.RowCount || rows > MaximumRowsPerFile) throw new InvalidDataException($"Manifest row-count mismatch: {evidence.RelativePath}");
        }
    }
    private static string ResolveInside(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Manifest paths must be relative.");
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relative));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Manifest file path escapes the dataset directory.");
        return fullPath;
    }
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static async Task WriteManifestAsync(string path, DownloadManifest manifest, CancellationToken token)
    {
        manifest.DownloadedAtUtc = DateTimeOffset.UtcNow;
        manifest.TotalRows = manifest.Windows.Where(x => x.Status == "completed").Sum(x => x.RowCount);
        manifest.ZeroVolumeRows = manifest.Windows.Where(x => x.Status == "completed").SelectMany(x => x.Files).Sum(x => x.ZeroVolumeRows);
        var evidence = manifest.Windows.Where(x => x.Status == "completed").SelectMany(x => x.Files).ToArray();
        manifest.ActualFrom = evidence.Length == 0 ? null : evidence.Min(x => x.FirstTimestamp);
        manifest.ActualTo = evidence.Length == 0 ? null : evidence.Max(x => x.LastTimestamp);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false), token);
        File.Move(temporary, path, true);
    }
}

internal sealed record DownloadedCandle(DateTimeOffset Timestamp, decimal Open, decimal High, decimal Low,
    decimal Close, long Volume, long? OpenInterest);

internal sealed class DownloadManifest
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
    public List<WindowEvidence> Windows { get; set; } = [];
}

internal sealed class WindowEvidence(DateOnly from, DateOnly to, string status, int rowCount,
    DateTimeOffset? firstTimestamp, DateTimeOffset? lastTimestamp, List<FileEvidence> files)
{
    public DateOnly From { get; set; } = from;
    public DateOnly To { get; set; } = to;
    public string Status { get; set; } = status;
    public int RowCount { get; set; } = rowCount;
    public DateTimeOffset? FirstTimestamp { get; set; } = firstTimestamp;
    public DateTimeOffset? LastTimestamp { get; set; } = lastTimestamp;
    public List<FileEvidence> Files { get; set; } = files;
    public string? Error { get; set; }
}

internal sealed record FileEvidence(string RelativePath, int RowCount, DateTimeOffset FirstTimestamp,
    DateTimeOffset LastTimestamp, string Sha256, int ZeroVolumeRows);