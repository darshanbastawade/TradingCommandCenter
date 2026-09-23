using Trading.Application.Backtesting;
using Trading.Backtesting.Comparison;

namespace Trading.BacktestTests;

public sealed class CrossEngineTradeComparerTests
{
    private static readonly Guid Instrument = Guid.Parse("5388e29e-d5df-4ed7-b0f3-e28f2d611aac");
    private static readonly DateTime Start = new(2026, 1, 5, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Equal_runs_match_every_trade_and_seal_comparison()
    {
        var native = Run("native-csharp", BacktestEngineRole.Authoritative, Trade(0, 10, 10_010),
            Trade(30, -5, 10_005));
        var lean = Run("lean", BacktestEngineRole.IndependentValidation, Trade(0, 10, 10_010),
            Trade(30, -5, 10_005));

        var comparison = CrossEngineTradeComparer.Compare(native, lean);

        Assert.Equal(CrossEngineVerdict.Pass, comparison.Verdict);
        Assert.Equal(2, comparison.MatchedTradeCount);
        Assert.Equal(1, comparison.MatchedTradeRate);
        Assert.Equal(1, comparison.EntryTimestampMatchRate);
        Assert.Equal(0, comparison.NetPnlDeviationPercent);
        Assert.True(CrossEngineComparisonCodec.Verify(comparison));
        Assert.All(comparison.Trades, item => Assert.Empty(item.Differences));
    }

    [Fact]
    public void Missing_trade_does_not_shift_later_match()
    {
        var native = Run("native-csharp", BacktestEngineRole.Authoritative,
            Trade(0, 10, 10_010), Trade(30, -5, 10_005));
        var lean = Run("lean", BacktestEngineRole.IndependentValidation, Trade(30, -5, 9_995));

        var comparison = CrossEngineTradeComparer.Compare(native, lean);

        Assert.Equal(CrossEngineVerdict.Divergent, comparison.Verdict);
        Assert.Equal(1, comparison.MatchedTradeCount);
        Assert.Equal(.5m, comparison.MatchedTradeRate);
        Assert.Contains(comparison.Trades, item => item.NativeIndex == 0 && item.LeanIndex is null);
        Assert.Contains(comparison.Trades, item => item.NativeIndex == 1 && item.LeanIndex == 0);
    }

    [Fact]
    public void Exact_entry_match_is_reserved_before_nearby_match()
    {
        var native = Run("native-csharp", BacktestEngineRole.Authoritative,
            Trade(0, 10, 10_010), Trade(2, -5, 10_005));
        var lean = Run("lean", BacktestEngineRole.IndependentValidation, Trade(2, -5, 9_995));

        var comparison = CrossEngineTradeComparer.Compare(native, lean);

        Assert.Contains(comparison.Trades, item => item.NativeIndex == 0 && item.LeanIndex is null);
        Assert.Contains(comparison.Trades, item => item.NativeIndex == 1 && item.LeanIndex == 0);
    }

    [Fact]
    public void Timestamp_and_price_drift_are_reported_and_fail_default_tolerance()
    {
        var native = Run("native-csharp", BacktestEngineRole.Authoritative, Trade(0, 10, 10_010));
        var changed = Trade(0, 10, 10_010) with
        {
            EntryTimeUtc = Start.AddMinutes(1).AddSeconds(30),
            ExitTimeUtc = Start.AddMinutes(6),
            EntryPrice = 101
        };
        var lean = Run("lean", BacktestEngineRole.IndependentValidation, changed);

        var comparison = CrossEngineTradeComparer.Compare(native, lean);

        Assert.Equal(CrossEngineVerdict.Divergent, comparison.Verdict);
        Assert.Equal(0, comparison.EntryTimestampMatchRate);
        Assert.Equal(0, comparison.ExitTimestampMatchRate);
        Assert.Equal(100, comparison.MaximumEntryPriceDeviationBasisPoints);
        Assert.Contains("entryTime", comparison.Trades[0].Differences);
        Assert.Contains("entryPrice", comparison.Trades[0].Differences);
    }

    [Fact]
    public void Different_consumed_data_or_unverified_run_is_rejected()
    {
        var native = Run("native-csharp", BacktestEngineRole.Authoritative, Trade(0, 10, 10_010));
        var lean = Run("lean", BacktestEngineRole.IndependentValidation, Trade(0, 10, 10_010));

        Assert.Throws<InvalidDataException>(() => CrossEngineTradeComparer.Compare(native,
            BacktestRunCodec.Seal(lean with { ConsumedMarketDataSha256 = new string('d', 64) })));
        Assert.Throws<InvalidDataException>(() => CrossEngineTradeComparer.Compare(native,
            lean with { NetPnl = 999 }));
    }

    [Fact]
    public void Empty_runs_are_inconclusive()
    {
        var native = Run("native-csharp", BacktestEngineRole.Authoritative);
        var lean = Run("lean", BacktestEngineRole.IndependentValidation);
        Assert.Equal(CrossEngineVerdict.NoTrades, CrossEngineTradeComparer.Compare(native, lean).Verdict);
    }

    [Fact]
    public void Different_exit_reason_is_reported_even_when_summary_matches()
    {
        var native = Run("native-csharp", BacktestEngineRole.Authoritative, Trade(0, 10, 10_010));
        var lean = Run("lean", BacktestEngineRole.IndependentValidation,
            Trade(0, 10, 10_010) with { ExitReason = "stop" });

        var comparison = CrossEngineTradeComparer.Compare(native, lean);

        Assert.Equal(CrossEngineVerdict.Divergent, comparison.Verdict);
        Assert.Contains("exitReason", comparison.Trades[0].Differences);
    }

    private static BacktestRunTrade Trade(int minute, decimal pnl, decimal capitalAfter) =>
        new("fixture-strategy", Instrument, BacktestRunTradeDirection.Long,
            Start.AddMinutes(minute), Start.AddMinutes(minute + 1), Start.AddMinutes(minute + 5),
            1, 100, 95, 110, 100 + pnl, "target", pnl, 0, pnl, capitalAfter);

    private static BacktestRun Run(string engine, BacktestEngineRole role,
        params BacktestRunTrade[] trades) => BacktestRunCodec.Seal(new(1, engine, "1", role,
        new string('a', 64), new string('b', 64), new string('c', 64),
        10_000, trades.Length == 0 ? 10_000 : trades[^1].CapitalAfterTrade,
        trades.Sum(item => item.NetPnl), trades.Count(item => item.NetPnl > 0),
        trades.Count(item => item.NetPnl < 0), trades, [], string.Empty));
}
