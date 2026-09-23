using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trading.Application.AI;

public enum ResearchAnalystV2Recommendation { QualificationReview = 1, MoreResearch = 2, Reject = 3 }

public sealed record AstraResearchAnalystV2Output(
    int SchemaVersion,
    Guid CertificateId,
    string StrategyId,
    string ExecutiveSummary,
    IReadOnlyList<string> ResearchFindings,
    IReadOnlyList<string> CrossEngineFindings,
    IReadOnlyList<string> RobustnessFindings,
    IReadOnlyList<string> QualificationCaveats,
    IReadOnlyList<string> RequiredNextTests,
    IReadOnlyList<string> Warnings,
    ResearchAnalystV2Recommendation Recommendation);

public sealed record AstraResearchAnalystV2Request(Guid CertificateId, string StrategyId,
    string CertificateSha256, string EvidenceBundleSha256, string EvidenceBundleJson);

public sealed record AstraResearchAnalystV2Response(string ProviderResponseId, string ResponseModel,
    int InputTokens, int OutputTokens, AstraResearchAnalystV2Output Output);

public sealed record AstraResearchAnalysisV2Artifact(
    int SchemaVersion,
    Guid AnalysisId,
    DateTime CreatedAtUtc,
    Guid CertificateId,
    string StrategyId,
    string CertificateSha256,
    string EvidenceBundleSha256,
    string Deployment,
    string ResponseModel,
    string ProviderResponseId,
    string PromptVersion,
    string PromptSha256,
    int InputTokens,
    int OutputTokens,
    AstraResearchAnalystV2Output Analysis,
    bool DeterministicQualificationChanged,
    string AnalysisSha256);

public interface IAstraResearchAnalystV2
{
    string PromptVersion { get; }
    string PromptSha256 { get; }
    string Deployment { get; }
    Task<AstraResearchAnalystV2Response> AnalyzeAsync(AstraResearchAnalystV2Request request,
        CancellationToken cancellationToken = default);
}

public static class AstraResearchAnalysisV2Codec
{
    private static readonly JsonSerializerOptions Canonical = Options(false);
    private static readonly JsonSerializerOptions Display = Options(true);

    public static AstraResearchAnalysisV2Artifact Seal(AstraResearchAnalysisV2Artifact value)
    {
        if (value.SchemaVersion != 2 || value.AnalysisId == Guid.Empty || value.CertificateId == Guid.Empty ||
            value.CreatedAtUtc.Kind != DateTimeKind.Utc || string.IsNullOrWhiteSpace(value.StrategyId) ||
            !Hash(value.CertificateSha256) || !Hash(value.EvidenceBundleSha256) ||
            !Hash(value.PromptSha256) || value.InputTokens < 0 || value.OutputTokens < 0 ||
            string.IsNullOrWhiteSpace(value.Deployment) || string.IsNullOrWhiteSpace(value.ResponseModel) ||
            string.IsNullOrWhiteSpace(value.ProviderResponseId) || string.IsNullOrWhiteSpace(value.PromptVersion) ||
            value.Analysis is null || value.Analysis.SchemaVersion != 2 ||
            value.Analysis.CertificateId != value.CertificateId || value.Analysis.StrategyId != value.StrategyId ||
            string.IsNullOrWhiteSpace(value.Analysis.ExecutiveSummary) ||
            value.Analysis.ResearchFindings is null || value.Analysis.CrossEngineFindings is null ||
            value.Analysis.RobustnessFindings is null || value.Analysis.QualificationCaveats is null ||
            value.Analysis.RequiredNextTests is null || value.Analysis.Warnings is null ||
            !Enum.IsDefined(value.Analysis.Recommendation) ||
            value.DeterministicQualificationChanged)
            throw new ArgumentException("Astra research analysis V2 artifact is invalid.", nameof(value));
        var unsigned = value with { AnalysisSha256 = string.Empty };
        return unsigned with { AnalysisSha256 = Digest(unsigned) };
    }

    public static bool Verify(AstraResearchAnalysisV2Artifact value)
    {
        if (value is null || !Hash(value.AnalysisSha256)) return false;
        try { return Seal(value).AnalysisSha256 == value.AnalysisSha256.ToLowerInvariant(); }
        catch (Exception exception) when (exception is ArgumentException or OverflowException) { return false; }
    }

    public static string Serialize(AstraResearchAnalysisV2Artifact value) => JsonSerializer.Serialize(value, Display);
    private static bool Hash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Canonical)))).ToLowerInvariant();
    private static JsonSerializerOptions Options(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = indented };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return options;
    }
}
