namespace Trading.AI;

/// <summary>Azure OpenAI Responses API settings. Secrets remain outside tracked configuration.</summary>
public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";
    public string Endpoint { get; set; } = string.Empty;
    public string Deployment { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 120;
    public int MaximumInputCharacters { get; set; } = 750_000;
    public int MaximumOutputTokens { get; set; } = 4_000;
    public string ReasoningEffort { get; set; } = "low";
}
