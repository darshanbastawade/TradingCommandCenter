namespace Trading.Domain.MarketData;

public sealed class MarketFeedCapture
{
    private MarketFeedCapture() { }
    public MarketFeedCapture(Guid id, DateTime createdAtUtc, string source, string quoteMode, int tickCount,
        DateTime firstReceivedAtUtc, DateTime lastReceivedAtUtc, string artifactSha256, string artifactJson)
    {
        if (id == Guid.Empty || createdAtUtc.Kind != DateTimeKind.Utc || firstReceivedAtUtc.Kind != DateTimeKind.Utc ||
            lastReceivedAtUtc.Kind != DateTimeKind.Utc || tickCount < 1 || firstReceivedAtUtc > lastReceivedAtUtc)
            throw new ArgumentException("Feed capture identity, UTC timestamps or tick count is invalid.");
        Id = id; CreatedAtUtc = createdAtUtc; Source = Required(source, 32, nameof(source));
        QuoteMode = Required(quoteMode, 16, nameof(quoteMode)); TickCount = tickCount;
        FirstReceivedAtUtc = firstReceivedAtUtc; LastReceivedAtUtc = lastReceivedAtUtc;
        ArtifactSha256 = Hash(artifactSha256);
        ArtifactJson = string.IsNullOrWhiteSpace(artifactJson) ? throw new ArgumentException("Artifact JSON is required.") : artifactJson;
    }
    public Guid Id { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public string Source { get; private set; } = string.Empty;
    public string QuoteMode { get; private set; } = string.Empty;
    public int TickCount { get; private set; }
    public DateTime FirstReceivedAtUtc { get; private set; }
    public DateTime LastReceivedAtUtc { get; private set; }
    public string ArtifactSha256 { get; private set; } = string.Empty;
    public string ArtifactJson { get; private set; } = string.Empty;
    private static string Required(string value, int maximum, string name) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximum ? throw new ArgumentException("Value is required.", name) : value.Trim();
    private static string Hash(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit)
        ? value.ToLowerInvariant() : throw new ArgumentException("A SHA-256 value is required.");
}
