namespace Trading.Domain.Execution;

public sealed class InternalTradingLedgerSnapshot
{
    private InternalTradingLedgerSnapshot() { }
    public InternalTradingLedgerSnapshot(Guid id, DateTime asOfUtc, string strategyId,
        string revision, string artifactSha256, string artifactJson)
    {
        if (id == Guid.Empty || asOfUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Internal ledger identity or UTC timestamp is invalid.");
        Id = id; AsOfUtc = asOfUtc; StrategyId = Required(strategyId, 128);
        Revision = Required(revision, 128); ArtifactSha256 = Hash(artifactSha256);
        ArtifactJson = string.IsNullOrWhiteSpace(artifactJson) ?
            throw new ArgumentException("Internal ledger artifact JSON is required.") : artifactJson;
    }
    public Guid Id { get; private set; }
    public DateTime AsOfUtc { get; private set; }
    public string StrategyId { get; private set; } = string.Empty;
    public string Revision { get; private set; } = string.Empty;
    public string ArtifactSha256 { get; private set; } = string.Empty;
    public string ArtifactJson { get; private set; } = string.Empty;
    private static string Required(string value, int maximum) => string.IsNullOrWhiteSpace(value) ||
        value.Trim().Length > maximum ? throw new ArgumentException("Internal ledger text is invalid.") : value.Trim();
    private static string Hash(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit) ?
        value.ToLowerInvariant() : throw new ArgumentException("A SHA-256 value is required.");
}
