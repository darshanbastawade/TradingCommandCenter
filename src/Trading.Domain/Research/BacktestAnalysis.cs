namespace Trading.Domain.Research;

public sealed class BacktestAnalysis
{
    private BacktestAnalysis() { }

    public BacktestAnalysis(Guid id, Guid researchRunId, DateTime createdAtUtc, string deployment,
        string responseModel, string providerResponseId, string promptVersion, string promptSha256,
        string researchArtifactSha256, int inputTokens, int outputTokens, string analysisSha256,
        string analysisJson)
    {
        if (id == Guid.Empty || researchRunId == Guid.Empty || createdAtUtc.Kind != DateTimeKind.Utc ||
            inputTokens < 0 || outputTokens < 0)
            throw new ArgumentException("Analysis identity, UTC time or token usage is invalid.");
        Id = id;
        ResearchRunId = researchRunId;
        CreatedAtUtc = createdAtUtc;
        Deployment = Required(deployment, 128, nameof(deployment));
        ResponseModel = Required(responseModel, 128, nameof(responseModel));
        ProviderResponseId = Required(providerResponseId, 128, nameof(providerResponseId));
        PromptVersion = Required(promptVersion, 64, nameof(promptVersion));
        PromptSha256 = Hash(promptSha256, nameof(promptSha256));
        ResearchArtifactSha256 = Hash(researchArtifactSha256, nameof(researchArtifactSha256));
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        AnalysisSha256 = Hash(analysisSha256, nameof(analysisSha256));
        AnalysisJson = string.IsNullOrWhiteSpace(analysisJson)
            ? throw new ArgumentException("Analysis JSON is required.", nameof(analysisJson)) : analysisJson;
    }

    public Guid Id { get; private set; }
    public Guid ResearchRunId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public string Deployment { get; private set; } = string.Empty;
    public string ResponseModel { get; private set; } = string.Empty;
    public string ProviderResponseId { get; private set; } = string.Empty;
    public string PromptVersion { get; private set; } = string.Empty;
    public string PromptSha256 { get; private set; } = string.Empty;
    public string ResearchArtifactSha256 { get; private set; } = string.Empty;
    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public string AnalysisSha256 { get; private set; } = string.Empty;
    public string AnalysisJson { get; private set; } = string.Empty;

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
