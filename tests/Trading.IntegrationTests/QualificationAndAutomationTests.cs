using Trading.Execution.Automation;
using Trading.Execution.Qualification;
using Trading.Execution.Reconciliation;

namespace Trading.IntegrationTests;

public sealed class QualificationAndAutomationTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 6, 0, 0, DateTimeKind.Utc);
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);
    private static readonly string HashC = new('c', 64);

    [Fact]
    public void PaperQualification_QualifiesCompleteVerifiedEvidence()
    {
        var sessions = Enumerable.Range(0, 10).Select(index => new VerifiedPaperSession(
            Guid.NewGuid(), Now.AddDays(-10 + index), DateOnly.FromDateTime(Now.AddDays(-10 + index)),
            "strategy-v1", Hash(index), 4, 4, 0, 100m)).ToArray();

        var result = PaperQualificationEngine.Evaluate(Guid.NewGuid(), HashA, Guid.NewGuid(), HashB,
            "strategy-v1", Now.AddDays(30), sessions, Now);

        Assert.Equal(PaperQualificationStatus.Qualified, result.Status);
        Assert.True(result.EligibleForLiveReconciliation);
        Assert.False(result.SemiLiveAuthorized);
        Assert.True(PaperQualificationEngine.Verify(result));
    }

    [Fact]
    public void PaperQualification_RejectsWeakPaperPerformance()
    {
        var sessions = Enumerable.Range(0, 10).Select(index => new VerifiedPaperSession(
            Guid.NewGuid(), Now.AddDays(-10 + index), DateOnly.FromDateTime(Now.AddDays(-10 + index)),
            "strategy-v1", Hash(index), 4, 3, 1,
            index < 4 ? 10m : -20m)).ToArray();

        var result = PaperQualificationEngine.Evaluate(Guid.NewGuid(), HashA, Guid.NewGuid(), HashB,
            "strategy-v1", Now.AddDays(30), sessions, Now);

        Assert.Equal(PaperQualificationStatus.Rejected, result.Status);
        Assert.Contains("profitable-session-rate-below-minimum", result.FailureCodes);
        Assert.Contains("aggregate-net-pnl-not-positive", result.FailureCodes);
        Assert.False(result.EligibleForLiveReconciliation);
    }

    [Fact]
    public void LiveReconciliation_DetectsPositionAndOrderDivergence()
    {
        var request = Guid.NewGuid();
        var internalOrder = new ReconciliationOrder(request, "broker-1", 123, "NFO", "NIFTY",
            ReconciledOrderStatus.Submitted, 50, 0, null);
        var brokerOrder = internalOrder with { Status = ReconciledOrderStatus.Filled, FilledQuantity = 50,
            AverageFillPrice = 125m };
        var snapshot = new LiveReconciliationSnapshot(Now.AddSeconds(-1), 30_000m, 29_000m, 1m,
            [], [new(123, "NFO", "NIFTY", "MIS", 50)], [internalOrder], [brokerOrder]);

        var result = LiveReconciliationEngine.Reconcile(Guid.NewGuid(), HashA, "strategy-v1", snapshot, Now);

        Assert.Equal(LiveReconciliationStatus.Divergent, result.Status);
        Assert.Contains("cash-difference-exceeds-tolerance", result.DiscrepancyCodes);
        Assert.Contains("unexpected-broker-position", result.DiscrepancyCodes);
        Assert.Contains("order-status-mismatch", result.DiscrepancyCodes);
        Assert.False(result.EligibleForControlledAutomation);
        Assert.True(LiveReconciliationEngine.Verify(result));
    }

    [Fact]
    public void ControlledAutomation_AllowsOneConfirmedDirectActionWhenEveryGateIsOpen()
    {
        var intent = new ControlledAutomationIntent(Guid.NewGuid(), ControlledAutomationMode.DirectLive,
            "signal-001", 1, 0, 0, true, "ALLOW-CONTROLLED-AUTOMATION");
        var settings = new ControlledAutomationSettings { Enabled = true, AllowDirectLive = true,
            KillSwitchEngaged = false };

        var result = ControlledAutomationEngine.Evaluate(Guid.NewGuid(), HashA, Guid.NewGuid(), HashB,
            Guid.NewGuid(), HashC, "strategy-v1", Now.AddSeconds(-1), intent, Now, settings);

        Assert.Equal(ControlledAutomationDecision.DirectSubmissionEligible, result.Decision);
        Assert.Equal(1, result.MaximumAuthorizedActions);
        Assert.False(result.BrokerSubmissionPerformed);
        Assert.True(ControlledAutomationEngine.Verify(result));
    }

    [Fact]
    public void ControlledAutomation_BlocksByDefaultAndReportsEveryFailedGate()
    {
        var intent = new ControlledAutomationIntent(Guid.NewGuid(), ControlledAutomationMode.DirectLive,
            "signal-002", 1, 1, 1500m, false, null);

        var result = ControlledAutomationEngine.Evaluate(Guid.NewGuid(), HashA, Guid.NewGuid(), HashB,
            Guid.NewGuid(), HashC, "strategy-v1", Now.AddSeconds(-31), intent, Now);

        Assert.Equal(ControlledAutomationDecision.Blocked, result.Decision);
        Assert.Contains("automation-disabled", result.BlockCodes);
        Assert.Contains("kill-switch-engaged", result.BlockCodes);
        Assert.Contains("reconciliation-stale", result.BlockCodes);
        Assert.Contains("daily-action-limit-exceeded", result.BlockCodes);
        Assert.Contains("daily-loss-limit-reached", result.BlockCodes);
        Assert.Contains("operator-approval-required", result.BlockCodes);
        Assert.Equal(0, result.MaximumAuthorizedActions);
    }

    private static string Hash(int index) => index.ToString("x64");
}
