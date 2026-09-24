using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trading.Execution.Qualification;

public sealed record PaperQualificationPolicy
{
    public string PolicyVersion { get; init; } = "paper-qualification-v1";
    public int MinimumSessions { get; init; } = 10;
    public int MinimumDistinctTradingDays { get; init; } = 5;
    public int MinimumFilledTrades { get; init; } = 30;
    public decimal MinimumProfitableSessionRate { get; init; } = .60m;
    public decimal MaximumRejectedOrderRate { get; init; } = .25m;
    public bool RequirePositiveAggregateNetPnl { get; init; } = true;
}

public sealed record VerifiedPaperSession(Guid SessionId, DateTime CreatedAtUtc, DateOnly TradingDate, string StrategyId,
    string ArtifactSha256, int SubmittedOrders, int FilledTrades, int RejectedOrders, decimal RealizedNetPnl);

public sealed record RejectedPaperSession(Guid SessionId, string Reason);
public sealed record IncludedPaperSession(Guid SessionId, DateTime CreatedAtUtc, string ArtifactSha256);

public enum PaperQualificationStatus { Qualified = 1, Rejected = 2 }

public sealed record PaperQualificationArtifact(int SchemaVersion, Guid PaperQualificationId,
    DateTime EvaluatedAtUtc, DateTime ExpiresAtUtc, string PolicyVersion, Guid StrategyQualificationId,
    string StrategyQualificationSha256, Guid CertificateId, string CertificateSha256, string StrategyId,
    PaperQualificationStatus Status, int SessionCount, int DistinctTradingDays, int SubmittedOrders,
    int FilledTrades, int RejectedOrders, decimal AggregateNetPnl, decimal ProfitableSessionRate,
    decimal RejectedOrderRate, IReadOnlyList<string> FailureCodes, IReadOnlyList<string> SessionSha256,
    bool EligibleForLiveReconciliation, bool SemiLiveAuthorized, bool DirectLiveAuthorized,
    string PaperQualificationSha256, DateTime? QualificationObservationStartUtc = null,
    DateTime? QualificationCutoffUtc = null, DateTime? FirstEligibleSessionUtc = null,
    DateTime? LastEligibleSessionUtc = null, int? DiscoveredSessionCount = null,
    int? AcceptedSessionCount = null, IReadOnlyList<RejectedPaperSession>? RejectedSessions = null,
    IReadOnlyList<IncludedPaperSession>? IncludedSessions = null);

public static class PaperQualificationEngine
{
    private static readonly JsonSerializerOptions Canonical = Options(false);
    private static readonly JsonSerializerOptions Display = Options(true);

    public static PaperQualificationArtifact Evaluate(Guid strategyQualificationId,
        string strategyQualificationSha256, Guid certificateId, string certificateSha256,
        string strategyId, DateTime expiresAtUtc, IReadOnlyList<VerifiedPaperSession> sessions,
        DateTime evaluatedAtUtc, PaperQualificationPolicy? policy = null)
    {
        policy ??= new();
        ValidateInputs(strategyQualificationId, strategyQualificationSha256, certificateId,
            certificateSha256, strategyId, expiresAtUtc, sessions, evaluatedAtUtc, policy);
        var ordered = sessions.OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.SessionId).ToArray();
        var submitted = ordered.Sum(item => item.SubmittedOrders);
        var filled = ordered.Sum(item => item.FilledTrades);
        var rejected = ordered.Sum(item => item.RejectedOrders);
        var profitableRate = ordered.Length == 0 ? 0 :
            (decimal)ordered.Count(item => item.RealizedNetPnl > 0) / ordered.Length;
        var rejectedRate = submitted == 0 ? 1 : (decimal)rejected / submitted;
        var net = ordered.Sum(item => item.RealizedNetPnl);
        var distinctDays = ordered.Select(item => item.TradingDate).Distinct().Count();
        var failures = new List<string>();
        if (ordered.Length < policy.MinimumSessions) failures.Add("insufficient-sessions");
        if (distinctDays < policy.MinimumDistinctTradingDays) failures.Add("insufficient-trading-days");
        if (filled < policy.MinimumFilledTrades) failures.Add("insufficient-filled-trades");
        if (profitableRate < policy.MinimumProfitableSessionRate) failures.Add("profitable-session-rate-below-minimum");
        if (rejectedRate > policy.MaximumRejectedOrderRate) failures.Add("rejected-order-rate-above-maximum");
        if (policy.RequirePositiveAggregateNetPnl && net <= 0) failures.Add("aggregate-net-pnl-not-positive");
        var status = failures.Count == 0 ? PaperQualificationStatus.Qualified : PaperQualificationStatus.Rejected;
        var idSeed = $"{policy.PolicyVersion}|{strategyQualificationSha256}|{string.Join('|', ordered.Select(x => x.ArtifactSha256))}";
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(idSeed))[..16]);
        return Seal(new(1, id, evaluatedAtUtc, expiresAtUtc, policy.PolicyVersion.Trim(),
            strategyQualificationId, strategyQualificationSha256.ToLowerInvariant(), certificateId,
            certificateSha256.ToLowerInvariant(), strategyId.Trim(), status, ordered.Length, distinctDays,
            submitted, filled, rejected, net, profitableRate, rejectedRate, failures.AsReadOnly(),
            ordered.Select(item => item.ArtifactSha256.ToLowerInvariant()).ToArray(),
            status == PaperQualificationStatus.Qualified, false, false, string.Empty));
    }

    public static PaperQualificationArtifact EvaluateDurable(Guid strategyQualificationId,
        string strategyQualificationSha256, Guid certificateId, string certificateSha256,
        string strategyId, DateTime observationStartUtc, DateTime observationCutoffUtc,
        DateTime expiresAtUtc, int discoveredSessionCount, IReadOnlyList<VerifiedPaperSession> sessions,
        IReadOnlyList<RejectedPaperSession> rejectedSessions, PaperQualificationPolicy? policy = null)
    {
        policy ??= new();
        if (strategyQualificationId == Guid.Empty || certificateId == Guid.Empty ||
            !Hash(strategyQualificationSha256) || !Hash(certificateSha256) ||
            string.IsNullOrWhiteSpace(strategyId) || strategyId.Trim().Length > 128 ||
            observationStartUtc.Kind != DateTimeKind.Utc || observationCutoffUtc.Kind != DateTimeKind.Utc ||
            expiresAtUtc.Kind != DateTimeKind.Utc || observationStartUtc > observationCutoffUtc ||
            observationCutoffUtc >= expiresAtUtc || discoveredSessionCount < 0 || sessions is null ||
            rejectedSessions is null || discoveredSessionCount != sessions.Count + rejectedSessions.Count ||
            sessions.Count > 10_000 || rejectedSessions.Count > 10_000 ||
            sessions.Any(item => item.SessionId == Guid.Empty || item.CreatedAtUtc.Kind != DateTimeKind.Utc ||
                item.CreatedAtUtc < observationStartUtc || item.CreatedAtUtc > observationCutoffUtc ||
                item.TradingDate == default || item.StrategyId != strategyId || !Hash(item.ArtifactSha256) ||
                item.SubmittedOrders < 0 || item.FilledTrades < 0 || item.RejectedOrders < 0 ||
                item.FilledTrades + item.RejectedOrders > item.SubmittedOrders) ||
            rejectedSessions.Any(item => item.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(item.Reason)) ||
            sessions.Select(item => item.SessionId).Distinct().Count() != sessions.Count ||
            sessions.Select(item => item.ArtifactSha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sessions.Count)
            throw new ArgumentException("Durable paper qualification inputs are invalid.");
        ValidatePolicy(policy);
        var ordered = sessions.OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.SessionId).ToArray();
        var rejected = rejectedSessions.OrderBy(item => item.SessionId).ThenBy(item => item.Reason,
            StringComparer.Ordinal).ToArray();
        var submitted = ordered.Sum(item => item.SubmittedOrders);
        var filled = ordered.Sum(item => item.FilledTrades);
        var rejectedOrders = ordered.Sum(item => item.RejectedOrders);
        var profitableRate = ordered.Length == 0 ? 0 :
            (decimal)ordered.Count(item => item.RealizedNetPnl > 0) / ordered.Length;
        var rejectedRate = submitted == 0 ? 1 : (decimal)rejectedOrders / submitted;
        var net = ordered.Sum(item => item.RealizedNetPnl);
        var distinctDays = ordered.Select(item => item.TradingDate).Distinct().Count();
        var failures = Failures(policy, ordered.Length, distinctDays, filled, profitableRate, rejectedRate, net);
        if (rejected.Length > 0) failures.Add("rejected-session-evidence");
        var status = failures.Count == 0 ? PaperQualificationStatus.Qualified : PaperQualificationStatus.Rejected;
        var included = ordered.Select(item => new IncludedPaperSession(item.SessionId, item.CreatedAtUtc,
            item.ArtifactSha256.ToLowerInvariant())).ToArray();
        var idSeed = $"{policy.PolicyVersion}|{strategyQualificationSha256}|{observationStartUtc:O}|" +
            $"{observationCutoffUtc:O}|{string.Join('|', included.Select(x => $"{x.SessionId:D}:{x.ArtifactSha256}"))}|" +
            string.Join('|', rejected.Select(x => $"{x.SessionId:D}:{x.Reason}"));
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(idSeed))[..16]);
        return Seal(new(2, id, observationCutoffUtc, expiresAtUtc, policy.PolicyVersion.Trim(),
            strategyQualificationId, strategyQualificationSha256.ToLowerInvariant(), certificateId,
            certificateSha256.ToLowerInvariant(), strategyId.Trim(), status, ordered.Length, distinctDays,
            submitted, filled, rejectedOrders, net, profitableRate, rejectedRate, failures.AsReadOnly(),
            included.Select(item => item.ArtifactSha256).ToArray(), status == PaperQualificationStatus.Qualified,
            false, false, string.Empty, observationStartUtc, observationCutoffUtc,
            ordered.FirstOrDefault()?.CreatedAtUtc, ordered.LastOrDefault()?.CreatedAtUtc,
            discoveredSessionCount, ordered.Length, rejected, included));
    }

    public static PaperQualificationArtifact Seal(PaperQualificationArtifact value)
    {
        if (value.SchemaVersion is not (1 or 2) || value.PaperQualificationId == Guid.Empty ||
            value.StrategyQualificationId == Guid.Empty || value.CertificateId == Guid.Empty ||
            value.EvaluatedAtUtc.Kind != DateTimeKind.Utc || value.ExpiresAtUtc.Kind != DateTimeKind.Utc ||
            value.EvaluatedAtUtc >= value.ExpiresAtUtc || string.IsNullOrWhiteSpace(value.PolicyVersion) ||
            string.IsNullOrWhiteSpace(value.StrategyId) || !Hash(value.StrategyQualificationSha256) ||
            !Hash(value.CertificateSha256) || value.SessionSha256 is null || value.SessionSha256.Any(x => !Hash(x)) ||
            value.SessionSha256.Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.SessionSha256.Count ||
            value.SessionCount != value.SessionSha256.Count || value.SessionCount < 0 ||
            value.DistinctTradingDays < 0 || value.SubmittedOrders < 0 || value.FilledTrades < 0 ||
            value.RejectedOrders < 0 || value.FilledTrades + value.RejectedOrders > value.SubmittedOrders ||
            value.ProfitableSessionRate is < 0 or > 1 || value.RejectedOrderRate is < 0 or > 1 ||
            value.FailureCodes is null || value.FailureCodes.Any(string.IsNullOrWhiteSpace) ||
            value.FailureCodes.Distinct(StringComparer.Ordinal).Count() != value.FailureCodes.Count ||
            value.SemiLiveAuthorized || value.DirectLiveAuthorized ||
            !Enum.IsDefined(value.Status) ||
            (value.Status == PaperQualificationStatus.Qualified) != (value.FailureCodes.Count == 0) ||
            value.EligibleForLiveReconciliation != (value.Status == PaperQualificationStatus.Qualified) ||
            (value.SchemaVersion == 1 && (value.SessionCount < 1 || value.DistinctTradingDays < 1 ||
                value.QualificationObservationStartUtc is not null || value.QualificationCutoffUtc is not null ||
                value.FirstEligibleSessionUtc is not null || value.LastEligibleSessionUtc is not null ||
                value.DiscoveredSessionCount is not null || value.AcceptedSessionCount is not null ||
                value.RejectedSessions is not null || value.IncludedSessions is not null)) ||
            (value.SchemaVersion == 2 && !ValidDurableProvenance(value)))
            throw new ArgumentException("Paper qualification artifact is invalid.", nameof(value));
        var unsigned = value with { PaperQualificationSha256 = string.Empty };
        return unsigned with { PaperQualificationSha256 = Digest(unsigned) };
    }

    public static bool Verify(PaperQualificationArtifact value)
    {
        if (value is null || !Hash(value.PaperQualificationSha256)) return false;
        try { return Seal(value).PaperQualificationSha256 == value.PaperQualificationSha256.ToLowerInvariant(); }
        catch (Exception exception) when (exception is ArgumentException or OverflowException) { return false; }
    }

    public static string Serialize(PaperQualificationArtifact value) => JsonSerializer.Serialize(value, Display);

    private static void ValidateInputs(Guid qualificationId, string qualificationHash, Guid certificateId,
        string certificateHash, string strategyId, DateTime expiresAtUtc, IReadOnlyList<VerifiedPaperSession> sessions,
        DateTime evaluatedAtUtc, PaperQualificationPolicy policy)
    {
        if (qualificationId == Guid.Empty || certificateId == Guid.Empty || !Hash(qualificationHash) ||
            !Hash(certificateHash) || string.IsNullOrWhiteSpace(strategyId) || strategyId.Trim().Length > 128 ||
            evaluatedAtUtc.Kind != DateTimeKind.Utc || expiresAtUtc.Kind != DateTimeKind.Utc ||
            evaluatedAtUtc >= expiresAtUtc || sessions is null || sessions.Count is < 1 or > 10_000 ||
            sessions.Any(item => item.SessionId == Guid.Empty || item.CreatedAtUtc.Kind != DateTimeKind.Utc ||
                item.CreatedAtUtc > evaluatedAtUtc || item.TradingDate == default ||
                item.StrategyId != strategyId || !Hash(item.ArtifactSha256) ||
                item.SubmittedOrders < 0 || item.FilledTrades < 0 || item.RejectedOrders < 0 ||
                item.FilledTrades + item.RejectedOrders > item.SubmittedOrders) ||
            sessions.Select(x => x.SessionId).Distinct().Count() != sessions.Count ||
            sessions.Select(x => x.ArtifactSha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sessions.Count)
            throw new ArgumentException("Paper qualification inputs are invalid.");
        ValidatePolicy(policy);
    }

    private static List<string> Failures(PaperQualificationPolicy policy, int sessionCount, int distinctDays,
        int filled, decimal profitableRate, decimal rejectedRate, decimal net)
    {
        var failures = new List<string>();
        if (sessionCount < policy.MinimumSessions) failures.Add("insufficient-sessions");
        if (distinctDays < policy.MinimumDistinctTradingDays) failures.Add("insufficient-trading-days");
        if (filled < policy.MinimumFilledTrades) failures.Add("insufficient-filled-trades");
        if (profitableRate < policy.MinimumProfitableSessionRate) failures.Add("profitable-session-rate-below-minimum");
        if (rejectedRate > policy.MaximumRejectedOrderRate) failures.Add("rejected-order-rate-above-maximum");
        if (policy.RequirePositiveAggregateNetPnl && net <= 0) failures.Add("aggregate-net-pnl-not-positive");
        return failures;
    }

    private static void ValidatePolicy(PaperQualificationPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(policy.PolicyVersion) || policy.PolicyVersion.Trim().Length > 64 ||
            policy.MinimumSessions < 1 || policy.MinimumDistinctTradingDays < 1 || policy.MinimumFilledTrades < 1 ||
            policy.MinimumProfitableSessionRate is < 0 or > 1 || policy.MaximumRejectedOrderRate is < 0 or > 1)
            throw new ArgumentException("Paper qualification policy is invalid.", nameof(policy));
    }

    private static bool ValidDurableProvenance(PaperQualificationArtifact value)
    {
        if (value.QualificationObservationStartUtc?.Kind != DateTimeKind.Utc ||
            value.QualificationCutoffUtc?.Kind != DateTimeKind.Utc ||
            value.QualificationObservationStartUtc > value.QualificationCutoffUtc ||
            value.QualificationCutoffUtc != value.EvaluatedAtUtc || value.DiscoveredSessionCount is null or < 0 ||
            value.AcceptedSessionCount != value.SessionCount || value.RejectedSessions is null ||
            value.IncludedSessions is null ||
            value.DiscoveredSessionCount != value.IncludedSessions.Count + value.RejectedSessions.Count ||
            value.IncludedSessions.Count != value.SessionCount ||
            value.IncludedSessions.Any(item => item.SessionId == Guid.Empty ||
                item.CreatedAtUtc.Kind != DateTimeKind.Utc || !Hash(item.ArtifactSha256) ||
                item.CreatedAtUtc < value.QualificationObservationStartUtc ||
                item.CreatedAtUtc > value.QualificationCutoffUtc) ||
            value.IncludedSessions.Select(item => item.SessionId).Distinct().Count() != value.IncludedSessions.Count ||
            value.IncludedSessions.Select(item => item.ArtifactSha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.IncludedSessions.Count ||
            !value.SessionSha256.SequenceEqual(value.IncludedSessions.Select(item => item.ArtifactSha256),
                StringComparer.Ordinal) || value.RejectedSessions.Any(item => item.SessionId == Guid.Empty ||
                string.IsNullOrWhiteSpace(item.Reason)) ||
            (value.RejectedSessions.Count > 0) != value.FailureCodes.Contains("rejected-session-evidence",
                StringComparer.Ordinal)) return false;
        if (value.SessionCount == 0)
            return value.FirstEligibleSessionUtc is null && value.LastEligibleSessionUtc is null;
        return value.FirstEligibleSessionUtc == value.IncludedSessions[0].CreatedAtUtc &&
            value.LastEligibleSessionUtc == value.IncludedSessions[^1].CreatedAtUtc;
    }

    private static bool Hash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Canonical)))).ToLowerInvariant();
    private static JsonSerializerOptions Options(bool indented)
    {
        var value = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        value.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return value;
    }
}
