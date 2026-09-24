using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trading.Application.Backtesting;
using Trading.Domain.MarketData;

namespace Trading.ExternalValidation.Lean;

public static class LeanInputMapper
{
    private sealed record Request(int SchemaVersion, string RunId, string SpecificationSha256,
        string ConsumedMarketDataSha256, string CandleFileSha256, int CandleCount,
        string CandleFileName, string LeanImage, string AlgorithmSourceRevision,
        string StrategyImplementationVersion, BacktestSpecification Specification);

    public static async Task<LeanMappedInput> WriteAsync(string dataDirectory,
        SealedBacktestSpecification sealedSpecification, IReadOnlyList<Candle> candles,
        LeanAlgorithmProjectEvidence project, string leanImage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sealedSpecification);
        ArgumentNullException.ThrowIfNull(candles);
        if (!BacktestSpecificationCodec.Verify(sealedSpecification))
            throw new ArgumentException("The sealed specification hash is invalid.", nameof(sealedSpecification));
        var specification = BacktestSpecificationCodec.NormalizeAndValidate(sealedSpecification.Specification);
        if (candles.Count == 0 || candles.Any(candle =>
                candle.InstrumentId != specification.Instrument.InstrumentId ||
                (int)candle.Timeframe != specification.Data.TimeframeMinutes ||
                candle.OpenTimeUtc < specification.Data.FromUtc ||
                candle.OpenTimeUtc >= specification.Data.ToUtc))
            throw new ArgumentException("LEAN input candles do not match the declared instrument, timeframe or range.",
                nameof(candles));
        for (var index = 1; index < candles.Count; index++)
            if (candles[index].OpenTimeUtc <= candles[index - 1].OpenTimeUtc)
                throw new ArgumentException("LEAN input candles must be unique and chronological.", nameof(candles));

        var runId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetFullPath(dataDirectory), "tcc-lean", runId);
        Directory.CreateDirectory(directory);
        var candlePath = Path.Combine(directory, "candles.csv");
        var requestPath = Path.Combine(directory, "request.json");
        var evidencePath = Path.Combine(directory, "evidence.json");
        var outputDirectory = Path.Combine(directory, "lean-output");
        Directory.CreateDirectory(outputDirectory);
        var csv = Csv(candles);
        await File.WriteAllTextAsync(candlePath, csv, new UTF8Encoding(false), cancellationToken);
        var consumed = Fingerprint(specification, candles);
        var candleHash = Sha256(csv);
        var request = new Request(2, runId, sealedSpecification.SpecificationSha256,
            consumed, candleHash, candles.Count, "candles.csv", leanImage,
            project.AlgorithmSourceRevision, project.StrategyImplementationVersion, specification);
        var requestJson = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await File.WriteAllTextAsync(requestPath, requestJson,
            new UTF8Encoding(false), cancellationToken);
        return new(runId, requestPath, candlePath, evidencePath, outputDirectory, consumed, candles.Count,
            Sha256(requestJson), candleHash, specification.StrategyId, project.AlgorithmSourceRevision,
            project.StrategyImplementationVersion, leanImage);
    }

    private static string Csv(IReadOnlyList<Candle> candles)
    {
        var builder = new StringBuilder("openTimeUtc,open,high,low,close,volume,openInterest\n");
        foreach (var candle in candles)
            builder.Append(candle.OpenTimeUtc.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(Invariant(candle.Open)).Append(',').Append(Invariant(candle.High)).Append(',')
                .Append(Invariant(candle.Low)).Append(',').Append(Invariant(candle.Close)).Append(',')
                .Append(candle.Volume.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(candle.OpenInterest?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
                .Append('\n');
        return builder.ToString();
    }

    private static string Fingerprint(BacktestSpecification specification, IEnumerable<Candle> candles)
    {
        // Same consumed-row identity as native-csharp; no strategy code or native result is reused.
        var builder = new StringBuilder();
        builder.Append(specification.Instrument.InstrumentId).Append('|')
            .Append(specification.Data.TimeframeMinutes).Append('|')
            .Append(specification.Data.FromUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
            .Append(specification.Data.ToUtc.ToString("O", CultureInfo.InvariantCulture)).AppendLine();
        foreach (var candle in candles)
            builder.Append(candle.OpenTimeUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                .Append(Invariant(candle.Open)).Append('|').Append(Invariant(candle.High)).Append('|')
                .Append(Invariant(candle.Low)).Append('|').Append(Invariant(candle.Close)).Append('|')
                .Append(candle.Volume.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(candle.OpenInterest?.ToString(CultureInfo.InvariantCulture) ?? "null").AppendLine();
        return Sha256(builder.ToString());
    }

    private static string Invariant(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
