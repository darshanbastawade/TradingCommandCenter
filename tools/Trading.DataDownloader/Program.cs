using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const string instrumentKey = "NSE_INDEX|Nifty 50";
const int intervalMinutes = 5;
const int year = 2025;
const int maximumRowsPerFile = 9_500;

var token = Environment.GetEnvironmentVariable("UPSTOX_ACCESS_TOKEN");

if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine(
        "UPSTOX_ACCESS_TOKEN environment variable is not configured.");
    return 2;
}

var repositoryRoot = FindRepositoryRoot();

var outputDirectory = Path.Combine(
    repositoryRoot,
    "data",
    "nifty",
    "2025");

Directory.CreateDirectory(outputDirectory);

Console.WriteLine("Trading Command Center - Upstox downloader");
Console.WriteLine($"Instrument : {instrumentKey}");
Console.WriteLine($"Interval   : {intervalMinutes} minutes");
Console.WriteLine($"Year       : {year}");
Console.WriteLine($"Output     : {outputDirectory}");
Console.WriteLine();

using var httpClient = new HttpClient
{
    BaseAddress = new Uri("https://api.upstox.com/")
};

httpClient.DefaultRequestHeaders.Accept.Add(
    new MediaTypeWithQualityHeaderValue("application/json"));

httpClient.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", token.Trim());

var candles = new List<Candle>();

for (var month = 1; month <= 12; month++)
{
    var from = new DateOnly(year, month, 1);
    var to = from.AddMonths(1).AddDays(-1);

    Console.Write(
        $"Downloading {from:yyyy-MM-dd} -> {to:yyyy-MM-dd} ... ");

    var monthlyCandles = await DownloadMonthAsync(
        httpClient,
        instrumentKey,
        intervalMinutes,
        from,
        to);

    Console.WriteLine($"{monthlyCandles.Count:N0} candles");

    candles.AddRange(monthlyCandles);

    // Be polite to the API and make troubleshooting easier.
    await Task.Delay(TimeSpan.FromMilliseconds(400));
}

Console.WriteLine();
Console.WriteLine($"Raw downloaded candles : {candles.Count:N0}");

var normalized = candles
    .OrderBy(candle => candle.Timestamp)
    .GroupBy(candle => candle.Timestamp)
    .Select(group =>
    {
        if (group.Count() != 1)
        {
            throw new InvalidOperationException(
                $"Duplicate timestamp returned by provider: " +
                $"{group.Key:O}");
        }

        return group.Single();
    })
    .ToList();

ValidateChronology(normalized);

Console.WriteLine($"Unique candles          : {normalized.Count:N0}");

if (normalized.Count == 0)
{
    Console.Error.WriteLine("No candles were downloaded.");
    return 3;
}

var files = new List<FileEvidence>();

for (var index = 0;
     index < normalized.Count;
     index += maximumRowsPerFile)
{
    var chunkNumber = (index / maximumRowsPerFile) + 1;

    var chunk = normalized
        .Skip(index)
        .Take(maximumRowsPerFile)
        .ToList();

    var fileName =
        $"nifty-50-5m-{year}-{chunkNumber:00}.csv";

    var path = Path.Combine(outputDirectory, fileName);

    if (File.Exists(path))
    {
        throw new IOException(
            $"Output already exists: {path}");
    }

    var csv = BuildM2Csv(chunk);

    await File.WriteAllTextAsync(
        path,
        csv,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    var sha256 = ComputeSha256(path);

    var evidence = new FileEvidence(
        FileName: fileName,
        RowCount: chunk.Count,
        FirstTimestamp: chunk[0].Timestamp,
        LastTimestamp: chunk[^1].Timestamp,
        Sha256: sha256);

    files.Add(evidence);

    Console.WriteLine();
    Console.WriteLine($"Created : {fileName}");
    Console.WriteLine($"Rows    : {chunk.Count:N0}");
    Console.WriteLine($"From    : {chunk[0].Timestamp:O}");
    Console.WriteLine($"To      : {chunk[^1].Timestamp:O}");
    Console.WriteLine($"SHA256  : {sha256}");
}

var manifest = new DownloadManifest(
    SchemaVersion: 1,
    Provider: "Upstox",
    ProviderApi: "historical-candle-v3",
    InstrumentKey: instrumentKey,
    TimeframeMinutes: intervalMinutes,
    RequestedYear: year,
    GeneratedAtUtc: DateTimeOffset.UtcNow,
    TotalCandles: normalized.Count,
    Files: files);

var manifestPath = Path.Combine(
    outputDirectory,
    $"nifty-50-5m-{year}-manifest.json");

await File.WriteAllTextAsync(
    manifestPath,
    JsonSerializer.Serialize(
        manifest,
        new JsonSerializerOptions
        {
            WriteIndented = true
        }),
    new UTF8Encoding(false));

Console.WriteLine();
Console.WriteLine("Download complete.");
Console.WriteLine($"Manifest: {manifestPath}");

return 0;

static async Task<List<Candle>> DownloadMonthAsync(
    HttpClient httpClient,
    string instrumentKey,
    int intervalMinutes,
    DateOnly from,
    DateOnly to)
{
    var encodedInstrument =
        Uri.EscapeDataString(instrumentKey);

    var url =
        $"v3/historical-candle/" +
        $"{encodedInstrument}/minutes/" +
        $"{intervalMinutes}/" +
        $"{to:yyyy-MM-dd}/" +
        $"{from:yyyy-MM-dd}";

    using var request =
        new HttpRequestMessage(HttpMethod.Get, url);

    using var response =
        await httpClient.SendAsync(request);

    var json = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        throw new HttpRequestException(
            $"Upstox returned HTTP {(int)response.StatusCode} " +
            $"{response.ReasonPhrase}. Body: {Limit(json, 500)}");
    }

    using var document = JsonDocument.Parse(json);

    var root = document.RootElement;

    if (!root.TryGetProperty("status", out var status) ||
        !string.Equals(
            status.GetString(),
            "success",
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException(
            "Upstox response status was not success.");
    }

    if (!root.TryGetProperty("data", out var data) ||
        !data.TryGetProperty("candles", out var candleArray) ||
        candleArray.ValueKind != JsonValueKind.Array)
    {
        throw new InvalidDataException(
            "Upstox response did not contain data.candles.");
    }

    var result = new List<Candle>();

    foreach (var item in candleArray.EnumerateArray())
    {
        if (item.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "Unexpected candle representation.");
        }

        var values = item.EnumerateArray().ToArray();

        if (values.Length < 6)
        {
            throw new InvalidDataException(
                "Upstox candle contains fewer than 6 values.");
        }

        var timestamp = DateTimeOffset.Parse(
            values[0].GetString()
                ?? throw new InvalidDataException(
                    "Timestamp is missing."),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

        var open = values[1].GetDecimal();
        var high = values[2].GetDecimal();
        var low = values[3].GetDecimal();
        var close = values[4].GetDecimal();
        var volume = values[5].GetInt64();

        long? openInterest = null;

        if (values.Length >= 7 &&
            values[6].ValueKind == JsonValueKind.Number &&
            values[6].TryGetInt64(out var oi))
        {
            openInterest = oi;
        }

        ValidateCandle(
            timestamp,
            open,
            high,
            low,
            close,
            volume,
            openInterest);

        result.Add(
            new Candle(
                timestamp,
                open,
                high,
                low,
                close,
                volume,
                openInterest));
    }

    return result;
}

static void ValidateCandle(
    DateTimeOffset timestamp,
    decimal open,
    decimal high,
    decimal low,
    decimal close,
    long volume,
    long? openInterest)
{
    if (open <= 0 ||
        high <= 0 ||
        low <= 0 ||
        close <= 0)
    {
        throw new InvalidDataException(
            $"Non-positive OHLC at {timestamp:O}.");
    }

    if (high < open ||
        high < close ||
        low > open ||
        low > close ||
        high < low)
    {
        throw new InvalidDataException(
            $"Invalid OHLC relationship at {timestamp:O}.");
    }

    if (volume < 0)
    {
        throw new InvalidDataException(
            $"Negative volume at {timestamp:O}.");
    }

    if (openInterest is < 0)
    {
        throw new InvalidDataException(
            $"Negative open interest at {timestamp:O}.");
    }
}

static void ValidateChronology(
    IReadOnlyList<Candle> candles)
{
    for (var i = 1; i < candles.Count; i++)
    {
        if (candles[i].Timestamp <=
            candles[i - 1].Timestamp)
        {
            throw new InvalidDataException(
                "Candles are not strictly chronological.");
        }
    }
}

static string BuildM2Csv(
    IReadOnlyList<Candle> candles)
{
    var builder = new StringBuilder();

    builder.AppendLine(
        "timestamp,open,high,low,close,volume,openInterest");

    foreach (var candle in candles)
    {
        builder
            .Append(
                candle.Timestamp.ToString(
                    "yyyy-MM-dd'T'HH:mm:sszzz",
                    CultureInfo.InvariantCulture))
            .Append(',')
            .Append(FormatDecimal(candle.Open))
            .Append(',')
            .Append(FormatDecimal(candle.High))
            .Append(',')
            .Append(FormatDecimal(candle.Low))
            .Append(',')
            .Append(FormatDecimal(candle.Close))
            .Append(',')
            .Append(
                candle.Volume.ToString(
                    CultureInfo.InvariantCulture))
            .Append(',');

        if (candle.OpenInterest.HasValue)
        {
            builder.Append(
                candle.OpenInterest.Value.ToString(
                    CultureInfo.InvariantCulture));
        }

        builder.AppendLine();
    }

    return builder.ToString();
}

static string FormatDecimal(decimal value)
{
    return value.ToString(
        "0.####",
        CultureInfo.InvariantCulture);
}

static string ComputeSha256(string path)
{
    using var stream = File.OpenRead(path);

    var hash = SHA256.HashData(stream);

    return Convert.ToHexString(hash)
        .ToLowerInvariant();
}

static string FindRepositoryRoot()
{
    var directory =
        new DirectoryInfo(Environment.CurrentDirectory);

    while (directory is not null)
    {
        if (File.Exists(
                Path.Combine(
                    directory.FullName,
                    "TradingCommandCenter.sln")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException(
        "TradingCommandCenter.sln could not be located.");
}

static string Limit(
    string value,
    int maximumCharacters)
{
    return value.Length <= maximumCharacters
        ? value
        : value[..maximumCharacters] + "...";
}

internal sealed record Candle(
    DateTimeOffset Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    long? OpenInterest);

internal sealed record FileEvidence(
    string FileName,
    int RowCount,
    DateTimeOffset FirstTimestamp,
    DateTimeOffset LastTimestamp,
    string Sha256);

internal sealed record DownloadManifest(
    int SchemaVersion,
    string Provider,
    string ProviderApi,
    string InstrumentKey,
    int TimeframeMinutes,
    int RequestedYear,
    DateTimeOffset GeneratedAtUtc,
    int TotalCandles,
    IReadOnlyList<FileEvidence> Files);
