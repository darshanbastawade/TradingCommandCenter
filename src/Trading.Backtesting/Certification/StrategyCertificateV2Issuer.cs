using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trading.Backtesting.Certification;

public static class StrategyCertificateV2Issuer
{
    private static readonly JsonSerializerOptions Canonical = Options(false);
    private static readonly JsonSerializerOptions Display = Options(true);

    public static StrategyCertificateV2 Issue(StrategyCertificateV2Source source,
        StrategyCertificateV2Policy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        policy ??= new();
        Validate(source, policy);
        var failures = Failures(source, policy);
        var status = failures.Count == 0 ? StrategyCertificateV2Status.EvidenceQualified :
            StrategyCertificateV2Status.EvidenceRejected;
        var seed = $"{policy.PolicyVersion}|{source.ResearchRunId:D}|{source.StrategyId}|" +
            $"{source.SpecificationSha256}|{source.NativeResultSha256}|" +
            $"{source.CrossEngineComparisonSha256}|{source.RobustnessArtifactSha256}";
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(seed))[..16]);
        var unsigned = new StrategyCertificateV2(2, id, policy.PolicyVersion, status,
            source.ResearchRunId, source.StrategyId.Trim(), source.ResearchCreatedAtUtc,
            source.ResearchCreatedAtUtc.AddDays(policy.ValidityDays), source.SourceRevision.Trim(),
            source.DatasetSha256, source.ResearchArtifactSha256, source.SpecificationSha256,
            source.ParametersSha256, source.NativeResultSha256, source.LeanResultSha256,
            source.CrossEngineComparisonSha256, source.RobustnessArtifactSha256,
            source.Qualification, policy, failures, failures.Count == 0, false, false, true, string.Empty);
        return Seal(unsigned);
    }

    public static StrategyCertificateV2 Seal(StrategyCertificateV2 certificate)
    {
        ValidateCertificate(certificate);
        var unsigned = certificate with { CertificateSha256 = string.Empty };
        return unsigned with { CertificateSha256 = Digest(unsigned) };
    }

    public static bool Verify(StrategyCertificateV2 certificate)
    {
        if (certificate is null || !Hash(certificate.CertificateSha256)) return false;
        try { return Seal(certificate).CertificateSha256 == certificate.CertificateSha256.ToLowerInvariant(); }
        catch (Exception exception) when (exception is ArgumentException or OverflowException) { return false; }
    }

    public static string Serialize(StrategyCertificateV2 certificate) => JsonSerializer.Serialize(certificate, Display);

    private static IReadOnlyList<string> Failures(StrategyCertificateV2Source source,
        StrategyCertificateV2Policy policy)
    {
        var failures = new List<string>();
        if (!source.Qualification.Qualified || source.Qualification.QualificationFailures.Count != 0)
            failures.Add("research-ranking-not-qualified");
        if (!source.GenuineLeanValidation) failures.Add("genuine-lean-validation-missing");
        if (!source.CrossEnginePassed) failures.Add("cross-engine-comparison-failed");
        if (source.CrossEngineMatchedTradeRate < policy.MinimumCrossEngineMatchedTradeRate)
            failures.Add("cross-engine-match-rate-below-minimum");
        if (policy.RequireExactCrossEngineTimestamps &&
            (source.CrossEngineEntryTimestampMatchRate < 1 || source.CrossEngineExitTimestampMatchRate < 1))
            failures.Add("cross-engine-timestamp-mismatch");
        if (source.CrossEngineMaximumPriceDeviationBasisPoints >
            policy.MaximumCrossEnginePriceDeviationBasisPoints)
            failures.Add("cross-engine-price-deviation-exceeded");
        if (source.CrossEngineNetPnlDeviationPercent > policy.MaximumCrossEngineNetPnlDeviationPercent)
            failures.Add("cross-engine-pnl-deviation-exceeded");
        if (source.CrossEngineDrawdownDeviationPercentagePoints >
            policy.MaximumCrossEngineDrawdownDeviationPercentagePoints)
            failures.Add("cross-engine-drawdown-deviation-exceeded");
        if (source.MonteCarloDrawdownPercent > policy.MaximumMonteCarloDrawdownPercent)
            failures.Add("monte-carlo-drawdown-exceeded");
        if (source.BootstrapProbabilityOfLoss > policy.MaximumBootstrapProbabilityOfLoss)
            failures.Add("bootstrap-loss-probability-exceeded");
        if (source.BootstrapProbabilityOfRuin > policy.MaximumBootstrapProbabilityOfRuin)
            failures.Add("bootstrap-ruin-probability-exceeded");
        if (source.BootstrapFifthPercentileNetPnl < policy.MinimumBootstrapFifthPercentileNetPnl)
            failures.Add("bootstrap-tail-pnl-below-floor");
        if (source.SlippageStressNetPnl <= 0) failures.Add("slippage-stress-not-profitable");
        if (source.CostStressNetPnl <= 0) failures.Add("cost-stress-not-profitable");
        if (source.EntryDelayStressNetPnl <= 0) failures.Add("entry-delay-stress-not-profitable");
        if (source.MissedTradeMedianNetPnl <= 0) failures.Add("missed-trade-stress-not-profitable");
        return failures.AsReadOnly();
    }

    private static void Validate(StrategyCertificateV2Source source, StrategyCertificateV2Policy policy)
    {
        if (source.ResearchRunId == Guid.Empty || source.ResearchCreatedAtUtc.Kind != DateTimeKind.Utc ||
            string.IsNullOrWhiteSpace(source.StrategyId) || source.StrategyId.Trim().Length > 128 ||
            source.StrategyId != source.StrategyId.Trim() || string.IsNullOrWhiteSpace(source.SourceRevision) ||
            source.SourceRevision.Trim().Length > 128 || source.SourceRevision != source.SourceRevision.Trim() ||
            source.Qualification is null || source.Qualification.StrategyId != source.StrategyId ||
            source.CrossEngineMatchedTradeRate is < 0 or > 1 ||
            source.CrossEngineEntryTimestampMatchRate is < 0 or > 1 ||
            source.CrossEngineExitTimestampMatchRate is < 0 or > 1 ||
            source.CrossEngineMaximumPriceDeviationBasisPoints < 0 ||
            source.CrossEngineNetPnlDeviationPercent < 0 ||
            source.CrossEngineDrawdownDeviationPercentagePoints < 0 ||
            new[] { source.DatasetSha256, source.ResearchArtifactSha256, source.SpecificationSha256,
                source.ParametersSha256, source.NativeResultSha256, source.LeanResultSha256,
                source.CrossEngineComparisonSha256, source.RobustnessArtifactSha256 }.Any(value => !Hash(value)))
            throw new ArgumentException("Certificate V2 source identity or evidence is invalid.", nameof(source));
        if (string.IsNullOrWhiteSpace(policy.PolicyVersion) || policy.PolicyVersion.Trim().Length > 64 ||
            policy.PolicyVersion != policy.PolicyVersion.Trim() ||
            policy.ValidityDays is < 1 or > 365 || policy.MaximumMonteCarloDrawdownPercent is < 0 or > 100 ||
            policy.MinimumCrossEngineMatchedTradeRate is < 0 or > 1 ||
            policy.MaximumCrossEnginePriceDeviationBasisPoints < 0 ||
            policy.MaximumCrossEngineNetPnlDeviationPercent < 0 ||
            policy.MaximumCrossEngineDrawdownDeviationPercentagePoints < 0 ||
            policy.MaximumBootstrapProbabilityOfLoss is < 0 or > 1 ||
            policy.MaximumBootstrapProbabilityOfRuin is < 0 or > 1 ||
            policy.RequiredAdditionalSlippageBasisPointsPerSide < 0 || policy.RequiredCostMultiplier < 1 ||
            policy.RequiredEntryDelaySeconds < 0 || policy.RequiredMissedTradeProbability is < 0 or >= 1)
            throw new ArgumentException("Certificate V2 policy is invalid.", nameof(policy));
    }

    private static void ValidateCertificate(StrategyCertificateV2 value)
    {
        var expectedId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{value.PolicyVersion}|{value.ResearchRunId:D}|{value.StrategyId}|{value.SpecificationSha256}|" +
            $"{value.NativeResultSha256}|{value.CrossEngineComparisonSha256}|{value.RobustnessArtifactSha256}"))[..16]);
        if (value.SchemaVersion != 2 || value.CertificateId == Guid.Empty || value.CertificateId != expectedId ||
            value.ResearchRunId == Guid.Empty || !Enum.IsDefined(value.Status) ||
            value.IssuedAtUtc.Kind != DateTimeKind.Utc || value.ExpiresAtUtc.Kind != DateTimeKind.Utc ||
            value.ExpiresAtUtc <= value.IssuedAtUtc || value.EvidenceFailures is null || value.Policy is null ||
            value.Qualification is null || value.EligibleForPaperTrading || value.LiveTradingAuthorized ||
            value.Qualification.StrategyId != value.StrategyId ||
            value.Status == StrategyCertificateV2Status.EvidenceQualified &&
                (!value.Qualification.Qualified || value.Qualification.QualificationFailures.Count != 0) ||
            !value.HumanApprovalRequired || value.EligibleForQualificationPipeline !=
                (value.Status == StrategyCertificateV2Status.EvidenceQualified && value.EvidenceFailures.Count == 0) ||
            value.Status == StrategyCertificateV2Status.EvidenceRejected && value.EvidenceFailures.Count == 0 ||
            new[] { value.DatasetSha256, value.ResearchArtifactSha256, value.SpecificationSha256,
                value.ParametersSha256, value.NativeResultSha256, value.LeanResultSha256,
                value.CrossEngineComparisonSha256, value.RobustnessArtifactSha256 }.Any(hash => !Hash(hash)))
            throw new ArgumentException("Strategy certificate V2 is invalid.", nameof(value));
    }

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
