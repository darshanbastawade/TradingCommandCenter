namespace Trading.Domain.Execution;

public sealed class ReconciledExecutionState
{
    private ReconciledExecutionState() { }
    public ReconciledExecutionState(Guid id, DateTime asOfUtc, DateOnly exchangeTradingDate,
        decimal realizedPnlToday, int unresolvedBrokerSubmissions, int activeOpenBrokerPositions,
        Guid reconciliationId, string reconciliationSha256, string sourceRevision, bool authoritative)
    {
        if (id == Guid.Empty || reconciliationId == Guid.Empty || asOfUtc.Kind != DateTimeKind.Utc ||
            exchangeTradingDate == default || unresolvedBrokerSubmissions < 0 || activeOpenBrokerPositions < 0)
            throw new ArgumentException("Reconciled execution-state identity or values are invalid.");
        Id = id; AsOfUtc = asOfUtc; ExchangeTradingDate = exchangeTradingDate;
        RealizedPnlToday = realizedPnlToday; UnresolvedBrokerSubmissions = unresolvedBrokerSubmissions;
        ActiveOpenBrokerPositions = activeOpenBrokerPositions; ReconciliationId = reconciliationId;
        ReconciliationSha256 = Hash(reconciliationSha256); SourceRevision = Required(sourceRevision, 128);
        Authoritative = authoritative;
    }
    public Guid Id { get; private set; }
    public DateTime AsOfUtc { get; private set; }
    public DateOnly ExchangeTradingDate { get; private set; }
    public decimal RealizedPnlToday { get; private set; }
    public int UnresolvedBrokerSubmissions { get; private set; }
    public int ActiveOpenBrokerPositions { get; private set; }
    public Guid ReconciliationId { get; private set; }
    public string ReconciliationSha256 { get; private set; } = string.Empty;
    public string SourceRevision { get; private set; } = string.Empty;
    public bool Authoritative { get; private set; }
    private static string Required(string value, int maximum) => string.IsNullOrWhiteSpace(value) ||
        value.Trim().Length > maximum ? throw new ArgumentException("Source revision is invalid.") : value.Trim();
    private static string Hash(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit) ?
        value.ToLowerInvariant() : throw new ArgumentException("A SHA-256 value is required.");
}
