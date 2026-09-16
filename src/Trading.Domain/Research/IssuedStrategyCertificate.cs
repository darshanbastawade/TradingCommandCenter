namespace Trading.Domain.Research;

public sealed class IssuedStrategyCertificate
{
    private IssuedStrategyCertificate() { }

    public IssuedStrategyCertificate(Guid id, Guid researchRunId, string strategyId, DateTime issuedAtUtc,
        DateTime expiresAtUtc, string status, string researchArtifactSha256, string certificateSha256,
        string certificateJson)
    {
        if (id == Guid.Empty || researchRunId == Guid.Empty || issuedAtUtc.Kind != DateTimeKind.Utc ||
            expiresAtUtc.Kind != DateTimeKind.Utc || issuedAtUtc >= expiresAtUtc)
            throw new ArgumentException("Certificate identity and UTC validity window are invalid.");
        Id = id;
        ResearchRunId = researchRunId;
        StrategyId = Required(strategyId, 128, nameof(strategyId));
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        Status = Required(status, 32, nameof(status));
        ResearchArtifactSha256 = Hash(researchArtifactSha256, nameof(researchArtifactSha256));
        CertificateSha256 = Hash(certificateSha256, nameof(certificateSha256));
        CertificateJson = string.IsNullOrWhiteSpace(certificateJson)
            ? throw new ArgumentException("Certificate JSON is required.", nameof(certificateJson)) : certificateJson;
    }

    public Guid Id { get; private set; }
    public Guid ResearchRunId { get; private set; }
    public string StrategyId { get; private set; } = string.Empty;
    public DateTime IssuedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public string ResearchArtifactSha256 { get; private set; } = string.Empty;
    public string CertificateSha256 { get; private set; } = string.Empty;
    public string CertificateJson { get; private set; } = string.Empty;

    private static string Required(string value, int maximum, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximum)
            throw new ArgumentException($"A value of up to {maximum} characters is required.", parameter);
        return value.Trim();
    }

    private static string Hash(string value, string parameter)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A lowercase SHA-256 value is required.", parameter);
        return normalized;
    }
}
