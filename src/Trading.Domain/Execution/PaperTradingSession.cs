namespace Trading.Domain.Execution;

public sealed class PaperTradingSession
{
    private PaperTradingSession() { }

    public PaperTradingSession(Guid id, Guid strategyCertificateId, Guid marketFeedCaptureId,
        DateTime createdAtUtc, string strategyId, decimal initialCash, decimal endingCash,
        decimal realizedNetPnl, int submittedOrders, int filledTrades, int rejectedOrders,
        string configurationSha256, string artifactSha256, string artifactJson,
        Guid? strategyQualificationId = null, string? strategyQualificationSha256 = null,
        Guid? qualificationCertificateId = null, string? qualificationCertificateSha256 = null,
        DateTime? qualificationStartedAtUtc = null)
    {
        if (id == Guid.Empty || strategyCertificateId == Guid.Empty || marketFeedCaptureId == Guid.Empty ||
            createdAtUtc.Kind != DateTimeKind.Utc || initialCash <= 0 || endingCash < 0 ||
            submittedOrders < 1 || filledTrades < 0 || rejectedOrders < 0 ||
            filledTrades + rejectedOrders != submittedOrders ||
            !ValidQualificationLink(strategyQualificationId, strategyQualificationSha256,
                qualificationCertificateId, qualificationCertificateSha256, qualificationStartedAtUtc,
                createdAtUtc))
            throw new ArgumentException("Paper-session identity, UTC time, cash or counts are invalid.");
        Id = id; StrategyCertificateId = strategyCertificateId; MarketFeedCaptureId = marketFeedCaptureId;
        CreatedAtUtc = createdAtUtc; StrategyId = Required(strategyId, 128); InitialCash = initialCash;
        EndingCash = endingCash; RealizedNetPnl = realizedNetPnl; SubmittedOrders = submittedOrders;
        FilledTrades = filledTrades; RejectedOrders = rejectedOrders; ArtifactSha256 = Hash(artifactSha256);
        ConfigurationSha256 = Hash(configurationSha256);
        ArtifactJson = string.IsNullOrWhiteSpace(artifactJson) ?
            throw new ArgumentException("Artifact JSON is required.", nameof(artifactJson)) : artifactJson;
        StrategyQualificationId = strategyQualificationId;
        StrategyQualificationSha256 = OptionalHash(strategyQualificationSha256);
        QualificationCertificateId = qualificationCertificateId;
        QualificationCertificateSha256 = OptionalHash(qualificationCertificateSha256);
        QualificationStartedAtUtc = qualificationStartedAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid StrategyCertificateId { get; private set; }
    public Guid MarketFeedCaptureId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public string StrategyId { get; private set; } = string.Empty;
    public decimal InitialCash { get; private set; }
    public decimal EndingCash { get; private set; }
    public decimal RealizedNetPnl { get; private set; }
    public int SubmittedOrders { get; private set; }
    public int FilledTrades { get; private set; }
    public int RejectedOrders { get; private set; }
    public string ArtifactSha256 { get; private set; } = string.Empty;
    public string ConfigurationSha256 { get; private set; } = string.Empty;
    public string ArtifactJson { get; private set; } = string.Empty;
    public Guid? StrategyQualificationId { get; private set; }
    public string? StrategyQualificationSha256 { get; private set; }
    public Guid? QualificationCertificateId { get; private set; }
    public string? QualificationCertificateSha256 { get; private set; }
    public DateTime? QualificationStartedAtUtc { get; private set; }

    private static string Required(string value, int maximum) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximum ?
            throw new ArgumentException("Strategy ID is required.", nameof(value)) : value.Trim();
    private static string Hash(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit) ?
        value.ToLowerInvariant() : throw new ArgumentException("A SHA-256 value is required.");
    private static string? OptionalHash(string? value) => value is null ? null : Hash(value);
    private static bool ValidQualificationLink(Guid? qualificationId, string? qualificationHash,
        Guid? certificateId, string? certificateHash, DateTime? startedAtUtc, DateTime createdAtUtc)
    {
        var supplied = qualificationId.HasValue || qualificationHash is not null || certificateId.HasValue ||
            certificateHash is not null || startedAtUtc.HasValue;
        return !supplied || qualificationId is { } q && q != Guid.Empty && certificateId is { } c &&
            c != Guid.Empty && qualificationHash?.Length == 64 && qualificationHash.All(Uri.IsHexDigit) &&
            certificateHash?.Length == 64 && certificateHash.All(Uri.IsHexDigit) &&
            startedAtUtc is { Kind: DateTimeKind.Utc } start && createdAtUtc >= start;
    }
}
