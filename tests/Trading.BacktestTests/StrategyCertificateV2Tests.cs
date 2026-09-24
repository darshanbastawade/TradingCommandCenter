using Trading.Application.AI;
using Trading.Backtesting.Certification;
using Trading.Backtesting.Ranking;

namespace Trading.BacktestTests;

public sealed class StrategyCertificateV2Tests
{
    private static readonly DateTime Issued = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Passing_evidence_creates_stable_verified_certificate_that_is_not_yet_paper_eligible()
    {
        var first = StrategyCertificateV2Issuer.Issue(Source());
        var second = StrategyCertificateV2Issuer.Issue(Source());

        Assert.Equal(first.CertificateId, second.CertificateId);
        Assert.Equal(first.CertificateSha256, second.CertificateSha256);
        Assert.Equal(StrategyCertificateV2Status.EvidenceQualified, first.Status);
        Assert.True(first.EligibleForQualificationPipeline);
        Assert.False(first.EligibleForPaperTrading);
        Assert.False(first.LiveTradingAuthorized);
        Assert.True(StrategyCertificateV2Issuer.Verify(first));
    }

    [Fact]
    public void Failed_robustness_is_recorded_without_manufacturing_eligibility()
    {
        var certificate = StrategyCertificateV2Issuer.Issue(Source() with
        { BootstrapProbabilityOfLoss = .75m, CostStressNetPnl = -1 });

        Assert.Equal(StrategyCertificateV2Status.EvidenceRejected, certificate.Status);
        Assert.False(certificate.EligibleForQualificationPipeline);
        Assert.Contains("bootstrap-loss-probability-exceeded", certificate.EvidenceFailures);
        Assert.Contains("cost-stress-not-profitable", certificate.EvidenceFailures);
        Assert.True(StrategyCertificateV2Issuer.Verify(certificate));
    }

    [Fact]
    public void Synthetic_cross_engine_pass_cannot_become_evidence_qualified()
    {
        var certificate = StrategyCertificateV2Issuer.Issue(Source() with { GenuineLeanValidation = false });

        Assert.Equal(StrategyCertificateV2Status.EvidenceRejected, certificate.Status);
        Assert.Contains("genuine-lean-validation-missing", certificate.EvidenceFailures);
        Assert.False(certificate.EligibleForQualificationPipeline);
    }

    [Fact]
    public void Qualified_pipeline_requires_matching_analysis_and_operator_approval_but_ignores_ai_opinion()
    {
        var certificate = StrategyCertificateV2Issuer.Issue(Source());
        var analysis = Analysis(certificate, ResearchAnalystV2Recommendation.Reject);

        var qualified = QualifiedStrategyPipeline.Qualify(certificate, analysis, "review-2026-001",
            Issued.AddDays(1));

        Assert.True(qualified.EligibleForPaperQualification);
        Assert.Equal(ResearchAnalystV2Recommendation.Reject, qualified.AnalystRecommendation);
        Assert.False(qualified.AnalystChangedQualification);
        Assert.False(qualified.SemiLiveAuthorized);
        Assert.False(qualified.DirectLiveAuthorized);
        Assert.True(QualifiedStrategyPipeline.Verify(qualified));
        Assert.Throws<ArgumentException>(() => QualifiedStrategyPipeline.Qualify(certificate, analysis, "x",
            Issued.AddDays(1)));
    }

    private static StrategyCertificateV2Source Source() => new(
        Guid.Parse("32323232-3232-3232-3232-323232323232"), Issued, "strategy-v1", "revision-1",
        new string('a', 64), new string('b', 64), new string('c', 64), new string('d', 64),
        new string('e', 64), new string('f', 64), new string('1', 64), new string('2', 64),
        new StrategyScore(1, "strategy-v1", 90, true, [], 18, 14, 12, 18, 14, 9, 5),
        true, true, 1, 1, 1, 0, 0, 0, 10, .10m, 0, 100, 50, 40, 30, 20);

    private static AstraResearchAnalysisV2Artifact Analysis(StrategyCertificateV2 certificate,
        ResearchAnalystV2Recommendation recommendation)
    {
        var output = new AstraResearchAnalystV2Output(2, certificate.CertificateId,
            certificate.StrategyId, "Summary", ["research"], ["engines"], ["robustness"],
            ["caveat"], ["test"], ["warning"], recommendation);
        return AstraResearchAnalysisV2Codec.Seal(new(2,
            Guid.Parse("34343434-3434-3434-3434-343434343434"), Issued.AddHours(1),
            certificate.CertificateId, certificate.StrategyId, certificate.CertificateSha256,
            new string('3', 64), "astra", "gpt-6-astra", "resp-test",
            "astra-research-analyst-v2", new string('4', 64), 100, 50, output, false, string.Empty));
    }
}
