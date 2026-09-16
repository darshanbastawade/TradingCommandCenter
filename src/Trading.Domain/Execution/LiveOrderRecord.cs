namespace Trading.Domain.Execution;

public sealed class LiveOrderRecord
{
    private LiveOrderRecord() { }
    public LiveOrderRecord(Guid id, Guid strategyCertificateId, Guid requestId, DateTime createdAtUtc,
        string mode, string status, string strategyId, string exchange, string tradingSymbol, int quantity,
        decimal limitPrice, decimal stopPrice, decimal targetPrice, string paperEvidenceSha256,
        string riskDecisionSha256, string brokerOrderId, string artifactSha256, string artifactJson)
    {
        if (id == Guid.Empty || strategyCertificateId == Guid.Empty || requestId == Guid.Empty ||
            createdAtUtc.Kind != DateTimeKind.Utc || quantity < 1 || limitPrice <= 0 || stopPrice <= 0 ||
            targetPrice <= 0 || stopPrice >= limitPrice || targetPrice <= limitPrice)
            throw new ArgumentException("Live-order identity, UTC time, quantity or prices are invalid.");
        Id = id; StrategyCertificateId = strategyCertificateId; RequestId = requestId; CreatedAtUtc = createdAtUtc;
        Mode = Required(mode, 16); Status = Required(status, 16); StrategyId = Required(strategyId, 128);
        Exchange = Required(exchange, 16); TradingSymbol = Required(tradingSymbol, 96); Quantity = quantity;
        LimitPrice = limitPrice; StopPrice = stopPrice; TargetPrice = targetPrice;
        PaperEvidenceSha256 = Hash(paperEvidenceSha256); RiskDecisionSha256 = Hash(riskDecisionSha256);
        BrokerOrderId = Optional(brokerOrderId, 64); ArtifactSha256 = Hash(artifactSha256);
        ArtifactJson = string.IsNullOrWhiteSpace(artifactJson) ? throw new ArgumentException("Artifact JSON is required.") : artifactJson;
    }
    public Guid Id { get; private set; }
    public Guid StrategyCertificateId { get; private set; }
    public Guid RequestId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public string Mode { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public string StrategyId { get; private set; } = string.Empty;
    public string Exchange { get; private set; } = string.Empty;
    public string TradingSymbol { get; private set; } = string.Empty;
    public int Quantity { get; private set; }
    public decimal LimitPrice { get; private set; }
    public decimal StopPrice { get; private set; }
    public decimal TargetPrice { get; private set; }
    public string PaperEvidenceSha256 { get; private set; } = string.Empty;
    public string RiskDecisionSha256 { get; private set; } = string.Empty;
    public string BrokerOrderId { get; private set; } = string.Empty;
    public string ArtifactSha256 { get; private set; } = string.Empty;
    public string ArtifactJson { get; private set; } = string.Empty;

    public void MarkSubmitted(string brokerOrderId, string artifactSha256, string artifactJson)
    {
        if (Status != "Prepared") throw new InvalidOperationException("Only a prepared order can be submitted.");
        BrokerOrderId = Required(brokerOrderId, 64); Status = "Submitted";
        ArtifactSha256 = Hash(artifactSha256);
        ArtifactJson = string.IsNullOrWhiteSpace(artifactJson) ? throw new ArgumentException("Artifact JSON is required.") : artifactJson;
    }
    private static string Required(string value, int maximum) => string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximum
        ? throw new ArgumentException("A required live-order value is invalid.") : value.Trim();
    private static string Optional(string value, int maximum) => value?.Trim().Length > maximum
        ? throw new ArgumentException("A live-order value is too long.") : value?.Trim() ?? string.Empty;
    private static string Hash(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit)
        ? value.ToLowerInvariant() : throw new ArgumentException("A SHA-256 value is required.");
}
