namespace Trading.Application.Execution;

public sealed record ControlledAutomationStateSnapshot(DateTime AsOfUtc, DateOnly ExchangeTradingDate,
    bool Available, int ConsumedDirectActionsToday, int SubmittedActionsToday,
    decimal ReconciledRealizedPnlToday, int UnresolvedBrokerSubmissions,
    int ActiveOpenBrokerPositions, string EvidenceSha256, string UnavailableReason);

public interface IControlledAutomationStateProvider
{
    Task<ControlledAutomationStateSnapshot> GetAsync(DateTime evaluatedAtUtc,
        CancellationToken cancellationToken = default);
}
