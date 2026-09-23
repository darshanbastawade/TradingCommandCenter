using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Application.AI;

namespace Trading.Backtesting.Certification;

public sealed record QualifiedStrategyPolicy
{
    public string PolicyVersion { get; init; } = "qualified-strategy-pipeline-v1";
    public int MaximumResearchRank { get; init; } = 2;
}

public sealed record QualifiedStrategyArtifact(
    int SchemaVersion,
    Guid QualificationId,
    DateTime QualifiedAtUtc,
    DateTime ExpiresAtUtc,
    string PolicyVersion,
    Guid CertificateId,
    string CertificateSha256,
    Guid AnalysisId,
    string AnalysisSha256,
    Guid ResearchRunId,
    string StrategyId,
    int ResearchRank,
    ResearchAnalystV2Recommendation AnalystRecommendation,
    string OperatorApprovalReference,
    bool DeterministicEvidenceQualified,
    bool AnalystChangedQualification,
    bool EligibleForPaperQualification,
    bool SemiLiveAuthorized,
    bool DirectLiveAuthorized,
    string QualificationSha256);

public static class QualifiedStrategyPipeline
{
    private static readonly JsonSerializerOptions Canonical = Options(false);
    private static readonly JsonSerializerOptions Display = Options(true);

    public static QualifiedStrategyArtifact Qualify(StrategyCertificateV2 certificate,
        AstraResearchAnalysisV2Artifact analysis, string operatorApprovalReference,
        DateTime qualifiedAtUtc, QualifiedStrategyPolicy? policy = null)
    {
        policy ??= new();
        if (!StrategyCertificateV2Issuer.Verify(certificate) ||
            !AstraResearchAnalysisV2Codec.Verify(analysis))
            throw new InvalidDataException("Qualification requires verified V2 certificate and analysis evidence.");
        if (certificate.Status != StrategyCertificateV2Status.EvidenceQualified ||
            !certificate.EligibleForQualificationPipeline || certificate.EvidenceFailures.Count != 0)
            throw new InvalidOperationException("The V2 certificate did not pass deterministic evidence gates.");
        if (analysis.CertificateId != certificate.CertificateId ||
            analysis.CertificateSha256 != certificate.CertificateSha256 ||
            analysis.StrategyId != certificate.StrategyId || analysis.DeterministicQualificationChanged)
            throw new InvalidDataException("The analysis does not match the certificate or changed qualification.");
        if (qualifiedAtUtc.Kind != DateTimeKind.Utc || qualifiedAtUtc < certificate.IssuedAtUtc ||
            qualifiedAtUtc >= certificate.ExpiresAtUtc)
            throw new ArgumentException("Qualification time is outside the certificate validity period.",
                nameof(qualifiedAtUtc));
        if (string.IsNullOrWhiteSpace(operatorApprovalReference) ||
            operatorApprovalReference.Trim().Length is < 3 or > 128)
            throw new ArgumentException("A 3-128 character operator approval reference is required.",
                nameof(operatorApprovalReference));
        if (string.IsNullOrWhiteSpace(policy.PolicyVersion) || policy.PolicyVersion.Trim().Length > 64 ||
            policy.MaximumResearchRank is < 1 or > 2 || certificate.Qualification.Rank < 1 ||
            certificate.Qualification.Rank > policy.MaximumResearchRank)
            throw new InvalidOperationException("Qualified strategy policy or research rank is invalid.");
        var seed = $"{policy.PolicyVersion}|{certificate.CertificateSha256}|{analysis.AnalysisSha256}|" +
            operatorApprovalReference.Trim();
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(seed))[..16]);
        return Seal(new(1, id, qualifiedAtUtc, certificate.ExpiresAtUtc, policy.PolicyVersion.Trim(),
            certificate.CertificateId, certificate.CertificateSha256, analysis.AnalysisId,
            analysis.AnalysisSha256, certificate.ResearchRunId, certificate.StrategyId,
            certificate.Qualification.Rank, analysis.Analysis.Recommendation,
            operatorApprovalReference.Trim(), true, false, true, false, false, string.Empty));
    }

    public static QualifiedStrategyArtifact Seal(QualifiedStrategyArtifact value)
    {
        var expectedId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{value.PolicyVersion}|{value.CertificateSha256}|{value.AnalysisSha256}|" +
            value.OperatorApprovalReference))[..16]);
        if (value.SchemaVersion != 1 || value.QualificationId == Guid.Empty ||
            value.QualificationId != expectedId ||
            value.CertificateId == Guid.Empty || value.AnalysisId == Guid.Empty ||
            value.ResearchRunId == Guid.Empty || value.QualifiedAtUtc.Kind != DateTimeKind.Utc ||
            value.ExpiresAtUtc.Kind != DateTimeKind.Utc || value.QualifiedAtUtc >= value.ExpiresAtUtc ||
            string.IsNullOrWhiteSpace(value.StrategyId) || string.IsNullOrWhiteSpace(value.PolicyVersion) ||
            string.IsNullOrWhiteSpace(value.OperatorApprovalReference) || !Hash(value.CertificateSha256) ||
            !Hash(value.AnalysisSha256) || !value.DeterministicEvidenceQualified ||
            value.AnalystChangedQualification || !value.EligibleForPaperQualification ||
            value.SemiLiveAuthorized || value.DirectLiveAuthorized ||
            value.ResearchRank is < 1 or > 2 || !Enum.IsDefined(value.AnalystRecommendation))
            throw new ArgumentException("Qualified strategy artifact is invalid.", nameof(value));
        var unsigned = value with { QualificationSha256 = string.Empty };
        return unsigned with { QualificationSha256 = Digest(unsigned) };
    }

    public static bool Verify(QualifiedStrategyArtifact value)
    {
        if (value is null || !Hash(value.QualificationSha256)) return false;
        try { return Seal(value).QualificationSha256 == value.QualificationSha256.ToLowerInvariant(); }
        catch (Exception exception) when (exception is ArgumentException or OverflowException) { return false; }
    }

    public static string Serialize(QualifiedStrategyArtifact value) => JsonSerializer.Serialize(value, Display);
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
