using Trading.Application.Backtesting;

namespace Trading.UnitTests;

public sealed class BacktestRunCodecTests
{
    [Fact]
    public void Run_evidence_is_hash_bound_and_tampering_is_detected()
    {
        var run = BacktestRunCodec.Seal(new(1, "native-csharp", "1", BacktestEngineRole.Authoritative,
            new string('a', 64), new string('b', 64), new string('c', 64), 100_000, 100_100, 100,
            1, 0, [new("strategy-v1", Guid.NewGuid(), BacktestRunTradeDirection.Long,
                new DateTime(2026, 1, 1, 3, 45, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 1, 3, 50, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 1, 4, 0, 0, DateTimeKind.Utc), 50, 100, 98, 106, 103,
                "target", 150, 50, 100, 100_100)], [], string.Empty));

        Assert.True(BacktestRunCodec.Verify(run));
        Assert.False(BacktestRunCodec.Verify(run with { NetPnl = 101 }));
        Assert.False(BacktestRunCodec.Verify(run with
        {
            Trades = [run.Trades[0] with { CapitalAfterTrade = 100_099 }]
        }));
        Assert.Equal(64, run.ResultSha256.Length);
    }
}
