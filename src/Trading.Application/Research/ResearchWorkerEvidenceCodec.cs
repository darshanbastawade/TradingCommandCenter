using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Application.Backtesting;

namespace Trading.Application.Research;

public static class ResearchWorkerEvidenceCodec
{
    private static readonly JsonSerializerOptions Json = Options();

    public static ResearchWorkerRequest Seal(ResearchWorkerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SchemaVersion != 1 || string.IsNullOrWhiteSpace(request.RequestId) ||
            !Hash(request.BaseSpecificationSha256) || string.IsNullOrWhiteSpace(request.StrategyId) ||
            request.TimeframeMinutes < 1 || string.IsNullOrWhiteSpace(request.ExchangeTimeZoneId) ||
            request.InitialCapital <= 0 || request.SlippageBasisPointsPerSide < 0 ||
            request.TopCandidates < 1 || request.Candles.Count < 2 || request.Candidates.Count < 1 ||
            request.TopCandidates > request.Candidates.Count)
            throw new ArgumentException("Research-worker request is invalid.", nameof(request));
        if (request.Candles.Any(item => item.OpenTimeUtc.Kind != DateTimeKind.Utc || item.Open <= 0 ||
                item.Low <= 0 || item.High < item.Low || item.Open < item.Low || item.Open > item.High ||
                item.Close < item.Low || item.Close > item.High || item.Volume < 0) ||
            request.Candles.Zip(request.Candles.Skip(1)).Any(pair => pair.First.OpenTimeUtc >= pair.Second.OpenTimeUtc))
            throw new ArgumentException("Research-worker candles are invalid.", nameof(request));
        if (request.Candidates.Select(item => item.CandidateKey).Distinct(StringComparer.Ordinal).Count() !=
            request.Candidates.Count || request.Candidates.Any(item => !Hash(item.CandidateKey) || item.Parameters.Count == 0))
            throw new ArgumentException("Research-worker candidates are invalid.", nameof(request));
        var unsigned = request with { RequestId = request.RequestId.Trim(), RequestSha256 = string.Empty };
        return unsigned with { RequestSha256 = Digest(unsigned) };
    }

    public static ResearchWorkerResult Seal(ResearchWorkerResult result, ResearchWorkerRequest request)
    {
        ArgumentNullException.ThrowIfNull(result); ArgumentNullException.ThrowIfNull(request);
        if (result.SchemaVersion != 1 || string.IsNullOrWhiteSpace(result.WorkerId) ||
            string.IsNullOrWhiteSpace(result.WorkerVersion) || result.Role != BacktestEngineRole.ResearchExploration ||
            result.RequestId != request.RequestId || result.RequestSha256 != request.RequestSha256 ||
            result.Candidates.Count is < 1 || result.Candidates.Count > request.TopCandidates ||
            result.Candidates.Select(item => item.CandidateKey).Distinct(StringComparer.Ordinal).Count() !=
            result.Candidates.Count)
            throw new ArgumentException("Research-worker result identity is invalid.", nameof(result));
        var expected = request.Candidates.Select(item => item.CandidateKey).ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in result.Candidates)
        {
            var metrics = candidate.Metrics;
            if (!expected.Contains(candidate.CandidateKey) || metrics.TradeCount < 0 ||
                Math.Abs(metrics.Score) > 9_999_999_999m ||
                metrics.WinRatePercent is < 0 or > 100 || metrics.MaximumDrawdownPercent is < 0 or > 100)
                throw new ArgumentException("Research-worker metrics are invalid.", nameof(result));
        }
        var unsigned = result with
        {
            WorkerId = result.WorkerId.Trim(), WorkerVersion = result.WorkerVersion.Trim(), EvidenceSha256 = string.Empty
        };
        return unsigned with { EvidenceSha256 = Digest(unsigned) };
    }

    public static bool Verify(ResearchWorkerResult result, ResearchWorkerRequest request)
    {
        if (result is null || !Hash(result.EvidenceSha256)) return false;
        try { return Seal(result, request).EvidenceSha256 == result.EvidenceSha256.ToLowerInvariant(); }
        catch (ArgumentException) { return false; }
    }

    public static string Serialize<T>(T value, bool indented = false) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions(Json) { WriteIndented = indented });

    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json)))).ToLowerInvariant();
    private static bool Hash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static JsonSerializerOptions Options()
    {
        var value = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        value.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return value;
    }
}
