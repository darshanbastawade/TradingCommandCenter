using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Backtesting.Ranking;

namespace Trading.Backtesting.Certification;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StrategyCertificateStatus { ResearchQualified = 1 }

public sealed record StrategyCertificateSettings
{
    public string PolicyVersion { get; init; } = "strategy-certificate-v1";
    public int ValidityDays { get; init; } = 90;
    public int MaximumCertificatesPerRun { get; init; } = 2;
}

public sealed record CertifiedResearchConstraints(decimal AllowedRisk, decimal MaximumCapital,
    int? MaximumLots, decimal SlippageBasisPoints, string CostProfile);

public sealed record StrategyCertificateSource(Guid ResearchRunId, DateTime ResearchCreatedAtUtc,
    Guid InstrumentId, int TimeframeMinutes, DateTime ResearchFromUtc, DateTime ResearchToUtc,
    string DatasetSha256, string ConfigurationSha256, string ResearchArtifactSha256,
    string SourceRevision, CertifiedResearchConstraints Constraints,
    IReadOnlyList<string> EvidenceStrategyIds, StrategyRankingResult Ranking);

public sealed record StrategyCertificate(
    int SchemaVersion,
    Guid CertificateId,
    string PolicyVersion,
    StrategyCertificateStatus Status,
    Guid ResearchRunId,
    string StrategyId,
    DateTime IssuedAtUtc,
    DateTime ExpiresAtUtc,
    Guid InstrumentId,
    int TimeframeMinutes,
    DateTime ResearchFromUtc,
    DateTime ResearchToUtc,
    string DatasetSha256,
    string ConfigurationSha256,
    string ResearchArtifactSha256,
    string SourceRevision,
    CertifiedResearchConstraints Constraints,
    StrategyScore Qualification,
    bool EligibleForPaperTrading,
    bool LiveTradingAuthorized,
    bool HumanApprovalRequired,
    string CertificateSha256);

/// <summary>Issues deterministic certificates from an already completed, qualified research ranking.</summary>
public static class StrategyCertificateIssuer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<StrategyCertificate> Issue(StrategyCertificateSource source,
        StrategyCertificateSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        settings ??= new();
        Validate(source, settings);
        var issuedAt = DateTime.SpecifyKind(source.ResearchCreatedAtUtc, DateTimeKind.Utc);
        var expiresAt = issuedAt.AddDays(settings.ValidityDays);
        var result = new List<StrategyCertificate>();
        foreach (var selected in source.Ranking.SelectedStrategies.OrderBy(item => item.Rank))
        {
            var seed = $"{settings.PolicyVersion}|{source.ResearchRunId:D}|{selected.StrategyId}";
            var idBytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed))[..16];
            var id = new Guid(idBytes);
            var unsigned = new StrategyCertificate(1, id, settings.PolicyVersion,
                StrategyCertificateStatus.ResearchQualified, source.ResearchRunId, selected.StrategyId,
                issuedAt, expiresAt, source.InstrumentId, source.TimeframeMinutes,
                Utc(source.ResearchFromUtc), Utc(source.ResearchToUtc), source.DatasetSha256,
                source.ConfigurationSha256, source.ResearchArtifactSha256, source.SourceRevision.Trim(),
                source.Constraints, selected, true, false, true, string.Empty);
            var hash = Sha256(JsonSerializer.Serialize(unsigned, Json));
            result.Add(unsigned with { CertificateSha256 = hash });
        }
        return result.AsReadOnly();
    }

    private static void Validate(StrategyCertificateSource source, StrategyCertificateSettings settings)
    {
        if (source.ResearchRunId == Guid.Empty || source.InstrumentId == Guid.Empty ||
            source.TimeframeMinutes <= 0 || source.ResearchFromUtc >= source.ResearchToUtc ||
            source.ResearchCreatedAtUtc.Kind != DateTimeKind.Utc ||
            source.ResearchFromUtc.Kind != DateTimeKind.Utc || source.ResearchToUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Certificate source identity, UTC timestamps, timeframe or range is invalid.", nameof(source));
        if (string.IsNullOrWhiteSpace(settings.PolicyVersion) || settings.PolicyVersion.Trim().Length > 64 ||
            settings.ValidityDays < 1 || settings.MaximumCertificatesPerRun is < 1 or > 2)
            throw new ArgumentException("Certificate settings are invalid.", nameof(settings));
        if (!Hash(source.DatasetSha256) || !Hash(source.ConfigurationSha256) || !Hash(source.ResearchArtifactSha256) ||
            string.IsNullOrWhiteSpace(source.SourceRevision) || source.SourceRevision.Trim().Length > 128)
            throw new ArgumentException("Certificate source hashes or revision are invalid.", nameof(source));
        if (source.Constraints is null || source.Constraints.AllowedRisk <= 0 ||
            source.Constraints.MaximumCapital <= 0 || source.Constraints.MaximumLots is <= 0 ||
            source.Constraints.SlippageBasisPoints < 0 || string.IsNullOrWhiteSpace(source.Constraints.CostProfile))
            throw new ArgumentException("Certified research constraints are invalid.", nameof(source));
        if (source.EvidenceStrategyIds is null || source.Ranking is null ||
            source.EvidenceStrategyIds.Distinct(StringComparer.Ordinal).Count() != source.EvidenceStrategyIds.Count ||
            source.Ranking.SelectedStrategies.Count > settings.MaximumCertificatesPerRun)
            throw new ArgumentException("Certificate strategy evidence is invalid.", nameof(source));
        foreach (var selected in source.Ranking.SelectedStrategies)
        {
            var ranked = source.Ranking.Rankings.SingleOrDefault(item => item.StrategyId == selected.StrategyId);
            if (!selected.Qualified || selected.QualificationFailures.Count != 0 || ranked is null ||
                !Equivalent(ranked, selected) ||
                !source.EvidenceStrategyIds.Contains(selected.StrategyId, StringComparer.Ordinal))
                throw new ArgumentException("Only selected, qualified strategies with matching evidence can be certified.", nameof(source));
        }
    }

    private static bool Hash(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool Equivalent(StrategyScore left, StrategyScore right) =>
        left.Rank == right.Rank && left.StrategyId == right.StrategyId && left.Score == right.Score &&
        left.Qualified == right.Qualified && left.QualificationFailures.SequenceEqual(right.QualificationFailures) &&
        left.EdgeScore == right.EdgeScore && left.ProfitFactorScore == right.ProfitFactorScore &&
        left.DrawdownScore == right.DrawdownScore && left.WalkForwardScore == right.WalkForwardScore &&
        left.RobustnessScore == right.RobustnessScore && left.RegimeScore == right.RegimeScore &&
        left.SampleSizeScore == right.SampleSizeScore;
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
