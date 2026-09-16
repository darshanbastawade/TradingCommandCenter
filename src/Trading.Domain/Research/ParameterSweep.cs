namespace Trading.Domain.Research;

public sealed class ParameterSweep
{
    private ParameterSweep() { }

    public ParameterSweep(Guid id, DateTimeOffset createdAt, string strategyId, string workerId,
        string workerVersion, string baseSpecificationSha256, string datasetSha256, string gridSha256,
        int evaluatedCandidates, int storedCandidates, string artifactSha256, string artifactJson)
    {
        if (id == Guid.Empty || evaluatedCandidates < 1 || storedCandidates < 1 ||
            storedCandidates > evaluatedCandidates) throw new ArgumentException("Parameter sweep counts are invalid.");
        Id = id; CreatedAtUtc = createdAt.UtcDateTime;
        StrategyId = Required(strategyId, 128, nameof(strategyId));
        WorkerId = Required(workerId, 64, nameof(workerId));
        WorkerVersion = Required(workerVersion, 64, nameof(workerVersion));
        BaseSpecificationSha256 = Hash(baseSpecificationSha256, nameof(baseSpecificationSha256));
        DatasetSha256 = Hash(datasetSha256, nameof(datasetSha256));
        GridSha256 = Hash(gridSha256, nameof(gridSha256));
        EvaluatedCandidates = evaluatedCandidates; StoredCandidates = storedCandidates;
        ArtifactSha256 = Hash(artifactSha256, nameof(artifactSha256));
        ArtifactJson = Json(artifactJson, nameof(artifactJson));
    }

    public Guid Id { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public string StrategyId { get; private set; } = string.Empty;
    public string WorkerId { get; private set; } = string.Empty;
    public string WorkerVersion { get; private set; } = string.Empty;
    public string BaseSpecificationSha256 { get; private set; } = string.Empty;
    public string DatasetSha256 { get; private set; } = string.Empty;
    public string GridSha256 { get; private set; } = string.Empty;
    public int EvaluatedCandidates { get; private set; }
    public int StoredCandidates { get; private set; }
    public string ArtifactSha256 { get; private set; } = string.Empty;
    public string ArtifactJson { get; private set; } = string.Empty;
    public bool NativeVerificationCompleted { get; private set; }

    public void MarkNativeVerificationCompleted()
    {
        if (NativeVerificationCompleted) throw new InvalidOperationException("Sweep verification is already complete.");
        NativeVerificationCompleted = true;
    }

    internal static string Required(string value, int maximum, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximum)
            throw new ArgumentException($"A value of up to {maximum} characters is required.", parameter);
        return value.Trim();
    }

    internal static string Hash(string value, string parameter)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A lowercase SHA-256 value is required.", parameter);
        return normalized;
    }

    internal static string Json(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("JSON evidence is required.", parameter);
        return value;
    }
}
