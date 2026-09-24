namespace Trading.Application.Execution;

public sealed record ControlledAutomationReservation(Guid AutomationDecisionId, string AutomationSha256,
    Guid ActionId, string ActionReference, string StrategyId, DateTime EvaluatedAtUtc,
    DateTime ExpiresAtUtc, string Decision, int MaximumAuthorizedActions, Guid RelatedLiveOrderId);

public interface IControlledAutomationAuthorizationStore
{
    Task<bool> TryConsumeAsync(ControlledAutomationReservation reservation, DateTime consumedAtUtc,
        CancellationToken cancellationToken = default);
    Task<bool> IsConsumedAsync(Guid automationDecisionId, CancellationToken cancellationToken = default);
}
