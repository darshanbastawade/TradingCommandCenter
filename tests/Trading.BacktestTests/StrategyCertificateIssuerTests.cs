using Trading.Backtesting.Certification;
using Trading.Backtesting.Ranking;

namespace Trading.BacktestTests;

public sealed class StrategyCertificateIssuerTests
{
    [Fact]
    public void Issues_only_selected_qualified_strategies_with_stable_identity_and_hash()
    {
        var selected = Score(1, "qualified", 91, true);
        var rejected = Score(2, "rejected", 58, false, "insufficient-trades");
        var source = Source([selected, rejected], [selected]);

        var first = Assert.Single(StrategyCertificateIssuer.Issue(source));
        var second = Assert.Single(StrategyCertificateIssuer.Issue(source));

        Assert.Equal(first, second);
        Assert.Equal("qualified", first.StrategyId);
        Assert.Equal(StrategyCertificateStatus.ResearchQualified, first.Status);
        Assert.True(first.EligibleForPaperTrading);
        Assert.False(first.LiveTradingAuthorized);
        Assert.True(first.HumanApprovalRequired);
        Assert.Equal(64, first.CertificateSha256.Length);
        Assert.Equal(source.ResearchCreatedAtUtc.AddDays(90), first.ExpiresAtUtc);
    }

    [Fact]
    public void Returns_no_certificate_when_no_strategy_qualifies()
    {
        var rejected = Score(1, "rejected", 58, false, "profit-factor-below-minimum");
        Assert.Empty(StrategyCertificateIssuer.Issue(Source([rejected], [])));
    }

    [Fact]
    public void Rejects_a_selected_score_without_matching_evidence()
    {
        var selected = Score(1, "qualified", 91, true);
        var source = Source([selected], [selected]) with { EvidenceStrategyIds = [] };
        Assert.Throws<ArgumentException>(() => StrategyCertificateIssuer.Issue(source));
    }

    [Fact]
    public void Rejects_more_than_two_certificates_per_run()
    {
        var scores = Enumerable.Range(1, 3).Select(rank => Score(rank, $"s{rank}", 90 - rank, true)).ToArray();
        Assert.Throws<ArgumentException>(() => StrategyCertificateIssuer.Issue(Source(scores, scores)));
    }

    private static StrategyCertificateSource Source(IReadOnlyList<StrategyScore> rankings,
        IReadOnlyList<StrategyScore> selected) => new(
        Guid.Parse("19191919-1919-1919-1919-191919191919"),
        new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc),
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 5,
        new DateTime(2025, 1, 1, 3, 45, 0, DateTimeKind.Utc),
        new DateTime(2026, 1, 1, 3, 45, 0, DateTimeKind.Utc),
        new string('a', 64), new string('b', 64), new string('c', 64), "revision-1",
        new(750, 100_000, 5, 5, "options-costs-v1"),
        rankings.Select(item => item.StrategyId).ToArray(), new(1, rankings, selected));

    private static StrategyScore Score(int rank, string id, decimal score, bool qualified,
        params string[] failures) => new(rank, id, score, qualified, failures,
        18, 14, 12, 18, 14, 10, 5);
}
