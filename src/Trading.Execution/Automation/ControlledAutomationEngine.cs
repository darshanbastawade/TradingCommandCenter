using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trading.Execution.Automation;

public enum ControlledAutomationMode { Observe = 1, SemiLive = 2, DirectLive = 3 }
public enum ControlledAutomationDecision { Blocked = 1, ObserveOnly = 2, ProposalEligible = 3, DirectSubmissionEligible = 4 }

public sealed record ControlledAutomationSettings
{
    public string PolicyVersion { get; init; } = "controlled-automation-v1";
    public bool Enabled { get; init; }
    public bool AllowSemiLive { get; init; }
    public bool AllowDirectLive { get; init; }
    public bool KillSwitchEngaged { get; init; } = true;
    public int MaximumActionsPerRun { get; init; } = 1;
    public int MaximumActionsPerDay { get; init; } = 1;
    public decimal MaximumDailyLoss { get; init; } = 1500m;
    public int MaximumReconciliationAgeSeconds { get; init; } = 30;
    public int AuthorizationLifetimeSeconds { get; init; } = 30;
}

public sealed record ControlledAutomationIntent(Guid ActionId, ControlledAutomationMode Mode,
    string ActionReference, int RequestedActions, int CompletedActionsToday, decimal RealizedLossToday,
    bool OperatorApproved, string? Confirmation);

public sealed record ControlledAutomationArtifact(int SchemaVersion, Guid AutomationDecisionId,
    DateTime EvaluatedAtUtc, DateTime ExpiresAtUtc, string PolicyVersion, Guid StrategyQualificationId,
    string StrategyQualificationSha256, Guid PaperQualificationId, string PaperQualificationSha256,
    Guid ReconciliationId, string ReconciliationSha256, string StrategyId, Guid ActionId,
    string ActionReference, ControlledAutomationMode RequestedMode, ControlledAutomationDecision Decision,
    IReadOnlyList<string> BlockCodes, int MaximumAuthorizedActions, bool BrokerSubmissionPerformed,
    string AutomationSha256);

public static class ControlledAutomationEngine
{
    private static readonly JsonSerializerOptions Canonical = Options(false);
    private static readonly JsonSerializerOptions Display = Options(true);

    public static ControlledAutomationArtifact Evaluate(Guid strategyQualificationId, string strategyQualificationHash,
        Guid paperQualificationId, string paperQualificationHash, Guid reconciliationId, string reconciliationHash,
        string strategyId, DateTime reconciliationAtUtc, ControlledAutomationIntent intent,
        DateTime evaluatedAtUtc, ControlledAutomationSettings? settings = null)
    {
        settings ??= new(); Validate(strategyQualificationId, strategyQualificationHash, paperQualificationId,
            paperQualificationHash, reconciliationId, reconciliationHash, strategyId, reconciliationAtUtc,
            intent, evaluatedAtUtc, settings);
        var blocks = new List<string>();
        if (!settings.Enabled) blocks.Add("automation-disabled");
        if (settings.KillSwitchEngaged) blocks.Add("kill-switch-engaged");
        if (evaluatedAtUtc - reconciliationAtUtc > TimeSpan.FromSeconds(settings.MaximumReconciliationAgeSeconds))
            blocks.Add("reconciliation-stale");
        if (intent.RequestedActions > settings.MaximumActionsPerRun) blocks.Add("run-action-limit-exceeded");
        if (intent.CompletedActionsToday + intent.RequestedActions > settings.MaximumActionsPerDay)
            blocks.Add("daily-action-limit-exceeded");
        if (intent.RealizedLossToday >= settings.MaximumDailyLoss) blocks.Add("daily-loss-limit-reached");
        if (intent.Mode == ControlledAutomationMode.SemiLive && !settings.AllowSemiLive)
            blocks.Add("semi-live-disabled");
        if (intent.Mode == ControlledAutomationMode.DirectLive)
        {
            if (!settings.AllowDirectLive) blocks.Add("direct-live-disabled");
            if (!intent.OperatorApproved) blocks.Add("operator-approval-required");
            if (intent.Confirmation != "ALLOW-CONTROLLED-AUTOMATION") blocks.Add("confirmation-required");
        }
        var decision = blocks.Count > 0 ? ControlledAutomationDecision.Blocked : intent.Mode switch
        {
            ControlledAutomationMode.Observe => ControlledAutomationDecision.ObserveOnly,
            ControlledAutomationMode.SemiLive => ControlledAutomationDecision.ProposalEligible,
            ControlledAutomationMode.DirectLive => ControlledAutomationDecision.DirectSubmissionEligible,
            _ => throw new ArgumentOutOfRangeException(nameof(intent))
        };
        var idSeed = $"{settings.PolicyVersion}|{strategyQualificationHash}|{paperQualificationHash}|{reconciliationHash}|{intent.ActionId:D}";
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(idSeed))[..16]);
        return Seal(new(1, id, evaluatedAtUtc,
            evaluatedAtUtc.AddSeconds(settings.AuthorizationLifetimeSeconds), settings.PolicyVersion.Trim(),
            strategyQualificationId, strategyQualificationHash.ToLowerInvariant(), paperQualificationId,
            paperQualificationHash.ToLowerInvariant(), reconciliationId, reconciliationHash.ToLowerInvariant(),
            strategyId.Trim(), intent.ActionId, intent.ActionReference.Trim(), intent.Mode, decision,
            blocks.AsReadOnly(), decision is ControlledAutomationDecision.ProposalEligible or
                ControlledAutomationDecision.DirectSubmissionEligible ? intent.RequestedActions : 0,
            false, string.Empty));
    }

    public static ControlledAutomationArtifact Seal(ControlledAutomationArtifact value)
    {
        if (value.SchemaVersion != 1 || value.AutomationDecisionId == Guid.Empty ||
            value.StrategyQualificationId == Guid.Empty || value.PaperQualificationId == Guid.Empty ||
            value.ReconciliationId == Guid.Empty || value.ActionId == Guid.Empty ||
            !Hash(value.StrategyQualificationSha256) || !Hash(value.PaperQualificationSha256) ||
            !Hash(value.ReconciliationSha256) || value.EvaluatedAtUtc.Kind != DateTimeKind.Utc ||
            value.ExpiresAtUtc.Kind != DateTimeKind.Utc || value.EvaluatedAtUtc >= value.ExpiresAtUtc ||
            string.IsNullOrWhiteSpace(value.PolicyVersion) || string.IsNullOrWhiteSpace(value.StrategyId) ||
            string.IsNullOrWhiteSpace(value.ActionReference) || !Enum.IsDefined(value.RequestedMode) ||
            !Enum.IsDefined(value.Decision) || value.BlockCodes is null || value.BlockCodes.Any(string.IsNullOrWhiteSpace) ||
            value.BlockCodes.Distinct(StringComparer.Ordinal).Count() != value.BlockCodes.Count ||
            (value.Decision == ControlledAutomationDecision.Blocked) != (value.BlockCodes.Count > 0) ||
            value.MaximumAuthorizedActions is < 0 or > 1 || value.BrokerSubmissionPerformed)
            throw new ArgumentException("Controlled automation artifact is invalid.", nameof(value));
        var unsigned = value with { AutomationSha256 = string.Empty };
        return unsigned with { AutomationSha256 = Digest(unsigned) };
    }

    public static bool Verify(ControlledAutomationArtifact value)
    {
        if (value is null || !Hash(value.AutomationSha256)) return false;
        try { return Seal(value).AutomationSha256 == value.AutomationSha256.ToLowerInvariant(); }
        catch (Exception exception) when (exception is ArgumentException or OverflowException) { return false; }
    }
    public static string Serialize(ControlledAutomationArtifact value) => JsonSerializer.Serialize(value, Display);

    private static void Validate(Guid strategyId, string strategyHash, Guid paperId, string paperHash,
        Guid reconciliationId, string reconciliationHash, string strategyName, DateTime reconciliationAt,
        ControlledAutomationIntent intent, DateTime now, ControlledAutomationSettings settings)
    {
        if (strategyId == Guid.Empty || paperId == Guid.Empty || reconciliationId == Guid.Empty ||
            !Hash(strategyHash) || !Hash(paperHash) || !Hash(reconciliationHash) ||
            string.IsNullOrWhiteSpace(strategyName) || reconciliationAt.Kind != DateTimeKind.Utc ||
            now.Kind != DateTimeKind.Utc || reconciliationAt > now || intent is null || intent.ActionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(intent.ActionReference) || intent.ActionReference.Trim().Length > 128 ||
            !Enum.IsDefined(intent.Mode) || intent.RequestedActions < 1 || intent.CompletedActionsToday < 0 ||
            intent.RealizedLossToday < 0 || string.IsNullOrWhiteSpace(settings.PolicyVersion) ||
            settings.MaximumActionsPerRun != 1 || settings.MaximumActionsPerDay < 1 ||
            settings.MaximumDailyLoss <= 0 || settings.MaximumReconciliationAgeSeconds is < 1 or > 300 ||
            settings.AuthorizationLifetimeSeconds is < 1 or > 300)
            throw new ArgumentException("Controlled automation input or settings are invalid.");
    }

    private static bool Hash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Canonical)))).ToLowerInvariant();
    private static JsonSerializerOptions Options(bool indented)
    { var value = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = indented };
      value.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false)); return value; }
}
