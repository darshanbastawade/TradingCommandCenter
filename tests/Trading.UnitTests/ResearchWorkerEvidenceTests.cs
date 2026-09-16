using Trading.Application.Backtesting;
using Trading.Application.Research;

namespace Trading.UnitTests;

public sealed class ResearchWorkerEvidenceTests
{
    [Fact]
    public void Request_and_exploratory_results_are_hash_bound()
    {
        var from = new DateTime(2026, 1, 1, 3, 45, 0, DateTimeKind.Utc);
        var request = ResearchWorkerEvidenceCodec.Seal(new(1, "request-1", string.Empty,
            new string('a', 64), "strategy-v1", 5, "India Standard Time", 100_000, 1,
            new TimeOnly(15, 25), 1,
            [new(from, 100, 101, 99, 100, 10), new(from.AddMinutes(5), 100, 102, 99, 101, 20)],
            [new(new string('b', 64), new Dictionary<string, decimal> { ["period"] = 20 })]));
        var result = ResearchWorkerEvidenceCodec.Seal(new(1, "vectorbt", "1.1.0",
            BacktestEngineRole.ResearchExploration, request.RequestId, request.RequestSha256,
            [new(new string('b', 64), new(10, 12, 4, 7, 60, 1.2m))], string.Empty), request);

        Assert.Equal(64, request.RequestSha256.Length);
        Assert.True(ResearchWorkerEvidenceCodec.Verify(result, request));
        Assert.False(ResearchWorkerEvidenceCodec.Verify(result with
            { Candidates = [result.Candidates[0] with { Metrics = result.Candidates[0].Metrics with { Score = 11 } }] }, request));
    }
}
