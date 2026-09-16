using Trading.Domain.MarketData;

namespace Trading.Domain.Research;

public sealed class ResearchRun
{
    private ResearchRun() { }

    public ResearchRun(Guid id, DateTimeOffset createdAt, Guid instrumentId, Timeframe timeframe,
        DateTimeOffset from, DateTimeOffset to, string dataSource, string dataVersion, string calendarId,
        string datasetSha256, string configurationSha256, string artifactSha256,
        string sourceRevision, string artifactJson)
    {
        if (id == Guid.Empty || instrumentId == Guid.Empty || !Enum.IsDefined(timeframe) || from >= to)
            throw new ArgumentException("Research run identity and range are invalid.");
        Id = id;
        CreatedAtUtc = createdAt.UtcDateTime;
        InstrumentId = instrumentId;
        Timeframe = timeframe;
        FromUtc = from.UtcDateTime;
        ToUtc = to.UtcDateTime;
        DataSource = Required(dataSource, 128, nameof(dataSource));
        DataVersion = Required(dataVersion, 128, nameof(dataVersion));
        CalendarId = Required(calendarId, 128, nameof(calendarId));
        DatasetSha256 = Hash(datasetSha256, nameof(datasetSha256));
        ConfigurationSha256 = Hash(configurationSha256, nameof(configurationSha256));
        ArtifactSha256 = Hash(artifactSha256, nameof(artifactSha256));
        SourceRevision = Required(sourceRevision, 128, nameof(sourceRevision));
        if (string.IsNullOrWhiteSpace(artifactJson)) throw new ArgumentException("Artifact JSON is required.", nameof(artifactJson));
        ArtifactJson = artifactJson;
    }

    public Guid Id { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public Guid InstrumentId { get; private set; }
    public Timeframe Timeframe { get; private set; }
    public DateTime FromUtc { get; private set; }
    public DateTime ToUtc { get; private set; }
    public string DataSource { get; private set; } = string.Empty;
    public string DataVersion { get; private set; } = string.Empty;
    public string CalendarId { get; private set; } = string.Empty;
    public string DatasetSha256 { get; private set; } = string.Empty;
    public string ConfigurationSha256 { get; private set; } = string.Empty;
    public string ArtifactSha256 { get; private set; } = string.Empty;
    public string SourceRevision { get; private set; } = string.Empty;
    public string ArtifactJson { get; private set; } = string.Empty;

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
