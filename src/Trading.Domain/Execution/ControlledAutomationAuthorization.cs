namespace Trading.Domain.Execution;

public sealed class ControlledAutomationAuthorization
{
    private ControlledAutomationAuthorization() { }

    public ControlledAutomationAuthorization(Guid automationDecisionId, string automationSha256,
        Guid actionId, string actionReference, string strategyId, DateTime evaluatedAtUtc,
        DateTime expiresAtUtc, string decision, int maximumAuthorizedActions,
        DateTime consumedAtUtc, Guid relatedLiveOrderId)
    {
        if (automationDecisionId == Guid.Empty || actionId == Guid.Empty || relatedLiveOrderId == Guid.Empty ||
            evaluatedAtUtc.Kind != DateTimeKind.Utc || expiresAtUtc.Kind != DateTimeKind.Utc ||
            consumedAtUtc.Kind != DateTimeKind.Utc || evaluatedAtUtc > consumedAtUtc ||
            consumedAtUtc >= expiresAtUtc || maximumAuthorizedActions != 1)
            throw new ArgumentException("Authorization identity, validity, or consumption is invalid.");
        AutomationDecisionId = automationDecisionId; AutomationSha256 = Hash(automationSha256);
        ActionId = actionId; ActionReference = Required(actionReference, 128);
        StrategyId = Required(strategyId, 128); EvaluatedAtUtc = evaluatedAtUtc;
        ExpiresAtUtc = expiresAtUtc; Decision = decision == "DirectSubmissionEligible" ? decision :
            throw new ArgumentException("Only direct-submission eligibility can be consumed.", nameof(decision));
        MaximumAuthorizedActions = maximumAuthorizedActions; ConsumedActions = 1;
        FirstConsumedAtUtc = consumedAtUtc; LastConsumedAtUtc = consumedAtUtc;
        RelatedLiveOrderId = relatedLiveOrderId;
    }

    public Guid AutomationDecisionId { get; private set; }
    public string AutomationSha256 { get; private set; } = string.Empty;
    public Guid ActionId { get; private set; }
    public string ActionReference { get; private set; } = string.Empty;
    public string StrategyId { get; private set; } = string.Empty;
    public DateTime EvaluatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public string Decision { get; private set; } = string.Empty;
    public int MaximumAuthorizedActions { get; private set; }
    public int ConsumedActions { get; private set; }
    public DateTime FirstConsumedAtUtc { get; private set; }
    public DateTime LastConsumedAtUtc { get; private set; }
    public Guid RelatedLiveOrderId { get; private set; }
    private static string Required(string value, int maximum) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximum ?
            throw new ArgumentException("A required authorization value is invalid.") : value.Trim();
    private static string Hash(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit) ?
        value.ToLowerInvariant() : throw new ArgumentException("A SHA-256 value is required.");
}
