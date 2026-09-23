using Trading.Application.Backtesting;
using Trading.Backtesting.Robustness;

namespace Trading.BacktestTests;

public sealed class RobustnessSuiteAnalyzerTests
{
    private static readonly DateTime Created = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Suite_is_deterministic_and_covers_all_six_robustness_families()
    {
        var settings = new RobustnessSuiteSettings
        {
            Seed = 42, Iterations = 200,
            AdditionalSlippageBasisPointsPerSide = [0, 10],
            CostMultipliers = [1, 2], EntryDelaySeconds = [0, 30],
            EntryDelayPenaltyBasisPointsPerSecond = .1m,
            MissedTradeProbabilities = [0, .5m]
        };

        var first = RobustnessSuiteAnalyzer.Analyze(Run(), settings, Created);
        var second = RobustnessSuiteAnalyzer.Analyze(Run(), settings, Created);

        Assert.Equal(first.ArtifactSha256, second.ArtifactSha256);
        Assert.True(RobustnessSuiteArtifactCodec.Verify(first));
        Assert.Equal(15, first.MonteCarlo.FifthPercentileNetPnl);
        Assert.Equal(200, first.Bootstrap.Iterations);
        Assert.Equal(2, first.SlippageStress.Count);
        Assert.True(first.SlippageStress[1].NetPnl < first.SlippageStress[0].NetPnl);
        Assert.True(first.CostStress[1].NetPnl < first.CostStress[0].NetPnl);
        Assert.True(first.EntryDelayStress[1].NetPnl < first.EntryDelayStress[0].NetPnl);
        Assert.Equal(3, first.MissedTradeSimulation[0].Distribution.MedianIncludedTradeCount);
        Assert.True(first.MissedTradeSimulation[1].Distribution.MedianIncludedTradeCount < 3);
    }

    [Fact]
    public void Monte_carlo_preserves_total_pnl_but_exposes_sequence_drawdown()
    {
        var artifact = RobustnessSuiteAnalyzer.Analyze(Run(),
            new RobustnessSuiteSettings { Iterations = 300 }, Created);

        Assert.Equal(15, artifact.MonteCarlo.FifthPercentileNetPnl);
        Assert.Equal(15, artifact.MonteCarlo.MedianNetPnl);
        Assert.Equal(15, artifact.MonteCarlo.NinetyFifthPercentileNetPnl);
        Assert.True(artifact.MonteCarlo.NinetyFifthPercentileMaximumDrawdownPercent > 0);
        Assert.True(artifact.Bootstrap.FifthPercentileNetPnl < artifact.Bootstrap.NinetyFifthPercentileNetPnl);
    }

    [Fact]
    public void Suite_rejects_unverified_external_or_empty_runs_and_invalid_settings()
    {
        var run = Run();
        Assert.Throws<InvalidDataException>(() => RobustnessSuiteAnalyzer.Analyze(run with { NetPnl = 999 }));
        Assert.Throws<ArgumentException>(() => RobustnessSuiteAnalyzer.Analyze(
            BacktestRunCodec.Seal(run with { EngineId = "lean", EngineRole = BacktestEngineRole.IndependentValidation })));
        Assert.Throws<ArgumentException>(() => RobustnessSuiteAnalyzer.Analyze(
            BacktestRunCodec.Seal(run with
            {
                Trades = [], NetPnl = 0, FinalCapital = 10_000, WinningTrades = 0, LosingTrades = 0
            })));
        Assert.Throws<ArgumentException>(() => RobustnessSuiteAnalyzer.Analyze(run,
            new RobustnessSuiteSettings { Iterations = 99 }));
    }

    private static BacktestRun Run()
    {
        var instrument = Guid.Parse("389c7e66-c2cc-4a3a-a830-b986dd622f47");
        var start = new DateTime(2026, 1, 1, 4, 0, 0, DateTimeKind.Utc);
        var trades = new[]
        {
            Trade(instrument, start, 20, 2, 10_020),
            Trade(instrument, start.AddMinutes(30), -10, 1, 10_010),
            Trade(instrument, start.AddMinutes(60), 5, 1, 10_015)
        };
        return BacktestRunCodec.Seal(new(1, "native-csharp", "1", BacktestEngineRole.Authoritative,
            new string('a', 64), new string('b', 64), new string('c', 64), 10_000, 10_015,
            15, 2, 1, trades, [], string.Empty));
    }

    private static BacktestRunTrade Trade(Guid instrument, DateTime signal, decimal netPnl,
        decimal costs, decimal capital) => new("fixture", instrument, BacktestRunTradeDirection.Long,
        signal, signal.AddMinutes(5), signal.AddMinutes(10), 1, 100, 95, 110,
        100 + netPnl + costs, "fixture-exit", netPnl + costs, costs, netPnl, capital);
}
