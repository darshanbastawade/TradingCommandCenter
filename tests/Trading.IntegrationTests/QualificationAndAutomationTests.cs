using Trading.Execution.Automation;
using Trading.Execution.Qualification;
using Trading.Execution.Reconciliation;
using Trading.Application.Execution;

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
    public void DurablePaperQualification_uses_every_session_and_is_deterministic_for_cutoff()
    {
        var qualificationId = Guid.NewGuid(); var start = Now.AddDays(-10); var cutoff = Now;
        var sessions = Enumerable.Range(0, 10).Select(index => new VerifiedPaperSession(
            Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}"), start.AddDays(index),
            DateOnly.FromDateTime(start.AddDays(index)), "strategy-v1", Hash(index + 10), 4, 4, 0,
            index == 9 ? -1_000m : 100m)).ToArray();

        var first = PaperQualificationEngine.EvaluateDurable(qualificationId, HashA, Guid.NewGuid(), HashB,
            "strategy-v1", start, cutoff, Now.AddDays(30), sessions.Length, sessions, []);
        var second = PaperQualificationEngine.EvaluateDurable(qualificationId, HashA, first.CertificateId, HashB,
            "strategy-v1", start, cutoff, Now.AddDays(30), sessions.Length, sessions.Reverse().ToArray(), []);

        Assert.Equal(10, first.DiscoveredSessionCount);
        Assert.Equal(10, first.AcceptedSessionCount);
        Assert.Equal(-100m, first.AggregateNetPnl);
        Assert.Equal(sessions.Select(item => item.ArtifactSha256).OrderBy(hash => hash),
            first.IncludedSessions!.Select(item => item.ArtifactSha256).OrderBy(hash => hash));
        Assert.Equal(first.PaperQualificationSha256, second.PaperQualificationSha256);
        Assert.Equal(PaperQualificationStatus.Rejected, first.Status);
    }

    [Fact]
    public void DurablePaperQualification_rejects_duplicates_and_altered_evidence()
    {
        var start = Now.AddDays(-2);
        var duplicate = new VerifiedPaperSession(Guid.NewGuid(), start.AddHours(1), new(2026, 9, 21),
            "strategy-v1", Hash(20), 4, 4, 0, 100m);
        Assert.Throws<ArgumentException>(() => PaperQualificationEngine.EvaluateDurable(Guid.NewGuid(), HashA,
            Guid.NewGuid(), HashB, "strategy-v1", start, Now, Now.AddDays(30), 2,
            [duplicate, duplicate], []));

        var accepted = Enumerable.Range(0, 9).Select(index => new VerifiedPaperSession(Guid.NewGuid(),
            start.AddHours(index + 1), new(2026, 9, 21), "strategy-v1", Hash(index + 30), 4, 4, 0, 100m)).ToArray();
        var alteredId = Guid.NewGuid();
        var result = PaperQualificationEngine.EvaluateDurable(Guid.NewGuid(), HashA, Guid.NewGuid(), HashB,
            "strategy-v1", start, Now, Now.AddDays(30), 10, accepted,
            [new(alteredId, "artifact-hash-invalid")]);

        Assert.Equal(10, result.DiscoveredSessionCount);
        Assert.Equal(9, result.AcceptedSessionCount);
        Assert.Equal(alteredId, Assert.Single(result.RejectedSessions!).SessionId);
        Assert.Contains("rejected-session-evidence", result.FailureCodes);
        Assert.False(result.EligibleForLiveReconciliation);
    }

    [Fact]
    public void DurablePaperQualification_rejects_pre_M34_session_input()
    {
        var start = Now.AddDays(-1);
        var session = new VerifiedPaperSession(Guid.NewGuid(), start.AddTicks(-1), new(2026, 9, 21),
            "strategy-v1", Hash(50), 4, 4, 0, 100m);
        Assert.Throws<ArgumentException>(() => PaperQualificationEngine.EvaluateDurable(Guid.NewGuid(), HashA,
            Guid.NewGuid(), HashB, "strategy-v1", start, Now, Now.AddDays(30), 1, [session], []));
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
            "signal-001", 1, true, "ALLOW-CONTROLLED-AUTOMATION");
        var settings = new ControlledAutomationSettings { Enabled = true, AllowDirectLive = true,
            KillSwitchEngaged = false };

        var result = ControlledAutomationEngine.Evaluate(Guid.NewGuid(), HashA, Guid.NewGuid(), HashB,
            Guid.NewGuid(), HashC, "strategy-v1", Now.AddSeconds(-1), intent, State(), Now, settings);

        Assert.Equal(ControlledAutomationDecision.DirectSubmissionEligible, result.Decision);
        Assert.Equal(1, result.MaximumAuthorizedActions);
        Assert.False(result.BrokerSubmissionPerformed);
        Assert.True(ControlledAutomationEngine.Verify(result));
    }

    [Fact]
    public void ControlledAutomation_BlocksByDefaultAndReportsEveryFailedGate()
    {
        var intent = new ControlledAutomationIntent(Guid.NewGuid(), ControlledAutomationMode.DirectLive,
            "signal-002", 1, false, null);

        var result = ControlledAutomationEngine.Evaluate(Guid.NewGuid(), HashA, Guid.NewGuid(), HashB,
            Guid.NewGuid(), HashC, "strategy-v1", Now.AddSeconds(-31), intent,
            State(consumed: 1, pnl: -1500m), Now);

        Assert.Equal(ControlledAutomationDecision.Blocked, result.Decision);
        Assert.Contains("automation-disabled", result.BlockCodes);
        Assert.Contains("kill-switch-engaged", result.BlockCodes);
        Assert.Contains("reconciliation-stale", result.BlockCodes);
        Assert.Contains("daily-action-limit-exceeded", result.BlockCodes);
        Assert.Contains("daily-loss-limit-reached", result.BlockCodes);
        Assert.Contains("operator-approval-required", result.BlockCodes);
        Assert.Equal(0, result.MaximumAuthorizedActions);
    }

    [Fact]
    public void Durable_state_blocks_loss_unresolved_position_stale_and_unavailable_paths()
    {
        var intent = new ControlledAutomationIntent(Guid.NewGuid(), ControlledAutomationMode.DirectLive,
            "signal-state", 1, true, "ALLOW-CONTROLLED-AUTOMATION");
        var settings = new ControlledAutomationSettings { Enabled = true, AllowDirectLive = true,
            KillSwitchEngaged = false };
        ControlledAutomationArtifact Evaluate(ControlledAutomationStateSnapshot state) =>
            ControlledAutomationEngine.Evaluate(Guid.NewGuid(), HashA, Guid.NewGuid(), HashB,
                Guid.NewGuid(), HashC, "strategy-v1", Now.AddSeconds(-1), intent, state, Now, settings);

        Assert.Contains("daily-action-limit-exceeded", Evaluate(State(consumed: 1)).BlockCodes);
        Assert.Contains("daily-loss-limit-reached", Evaluate(State(pnl: -1500m)).BlockCodes);
        Assert.Contains("unresolved-broker-submission", Evaluate(State(unresolved: 1)).BlockCodes);
        Assert.Contains("active-broker-position", Evaluate(State(openPositions: 1)).BlockCodes);
        Assert.Contains("execution-state-stale", Evaluate(State() with { AsOfUtc = Now.AddSeconds(-31) }).BlockCodes);
        Assert.Contains("execution-state-unavailable", Evaluate(State() with
            { Available = false, EvidenceSha256 = string.Empty, UnavailableReason = "missing" }).BlockCodes);
    }

    [Fact]
    public void Positive_realized_pnl_does_not_increase_action_limit()
    {
        var intent = new ControlledAutomationIntent(Guid.NewGuid(), ControlledAutomationMode.DirectLive,
            "signal-profit", 1, true, "ALLOW-CONTROLLED-AUTOMATION");
        var settings = new ControlledAutomationSettings { Enabled = true, AllowDirectLive = true,
            KillSwitchEngaged = false };
        var result = ControlledAutomationEngine.Evaluate(Guid.NewGuid(), HashA, Guid.NewGuid(), HashB,
            Guid.NewGuid(), HashC, "strategy-v1", Now.AddSeconds(-1), intent,
            State(consumed: 1, pnl: 100_000m), Now, settings);
        Assert.Contains("daily-action-limit-exceeded", result.BlockCodes);
        Assert.DoesNotContain("daily-loss-limit-reached", result.BlockCodes);
    }

    private static string Hash(int index) => index.ToString("x64");
    private static ControlledAutomationStateSnapshot State(int consumed = 0, decimal pnl = 0,
        int unresolved = 0, int openPositions = 0) =>
        new(Now.AddSeconds(-1), new(2026, 9, 23), true, consumed, consumed, pnl, unresolved, openPositions,
            new string('d', 64), string.Empty);
}
