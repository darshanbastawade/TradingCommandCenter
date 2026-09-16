using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Application.Backtesting;
using Trading.Application.Research;

namespace Trading.Backtesting.Research;

public sealed record ParameterSweepCandidateArtifact(Guid CandidateId, int Rank,
    SealedBacktestSpecification Specification, ResearchCandidateMetrics Metrics);

public sealed record ParameterSweepArtifact(int SchemaVersion, Guid SweepId, DateTime CreatedAtUtc,
    string WorkerId, string WorkerVersion, string BaseSpecificationSha256, string DatasetSha256,
    string GridSha256, string WorkerRequestSha256, string WorkerEvidenceSha256,
    int EvaluatedCandidates, IReadOnlyList<ParameterSweepCandidateArtifact> Candidates,
    string ArtifactSha256);

public sealed record NativeVerificationCandidateArtifact(Guid CandidateId, int ResearchRank,
    string SpecificationSha256, bool Succeeded, string ResultSha256, decimal? NetPnl,
    int? TradeCount, string Failure);

public sealed record NativeVerificationArtifact(int SchemaVersion, Guid VerificationId, Guid SweepId,
    DateTime CreatedAtUtc, string EngineId, string EngineVersion,
    IReadOnlyList<NativeVerificationCandidateArtifact> Candidates, string ArtifactSha256);

public static class CandidateArtifactCodec
{
    private static readonly JsonSerializerOptions Canonical = Options(false);
    private static readonly JsonSerializerOptions Display = Options(true);

    public static ParameterSweepArtifact Seal(ParameterSweepArtifact artifact)
    {
        if (artifact.SchemaVersion != 1 || artifact.SweepId == Guid.Empty ||
            artifact.CreatedAtUtc.Kind != DateTimeKind.Utc || artifact.Candidates.Count < 1 ||
            artifact.Candidates.Count > artifact.EvaluatedCandidates)
            throw new ArgumentException("Parameter sweep artifact is invalid.", nameof(artifact));
        var unsigned = artifact with { ArtifactSha256 = string.Empty };
        return unsigned with { ArtifactSha256 = Digest(unsigned) };
    }

    public static NativeVerificationArtifact Seal(NativeVerificationArtifact artifact)
    {
        if (artifact.SchemaVersion != 1 || artifact.VerificationId == Guid.Empty || artifact.SweepId == Guid.Empty ||
            artifact.CreatedAtUtc.Kind != DateTimeKind.Utc || artifact.Candidates.Count < 1 ||
            artifact.Candidates.Any(item => item.Succeeded && item.ResultSha256.Length != 64 ||
                !item.Succeeded && string.IsNullOrWhiteSpace(item.Failure)))
            throw new ArgumentException("Native verification artifact is invalid.", nameof(artifact));
        var unsigned = artifact with { ArtifactSha256 = string.Empty };
        return unsigned with { ArtifactSha256 = Digest(unsigned) };
    }

    public static string Serialize<T>(T artifact) => JsonSerializer.Serialize(artifact, Display);
    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Canonical)))).ToLowerInvariant();
    private static JsonSerializerOptions Options(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = indented };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return options;
    }
}
