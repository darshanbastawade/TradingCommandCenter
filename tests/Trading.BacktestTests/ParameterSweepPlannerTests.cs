using System.Text.Json;
using Trading.Application.Backtesting;
using Trading.Backtesting.Research;

namespace Trading.BacktestTests;

public sealed class ParameterSweepPlannerTests
{
    [Fact]
    public void Grid_expands_cartesian_product_into_sealed_candidate_specifications()
    {
        var baseline = Specification();
        var grid = ParameterSweepPlanner.Deserialize("""
            {"schemaVersion":1,"topCandidates":2,"parameters":{"fastEmaPeriod":[10,20],"minimumAdx":[20,25]}}
            """);

        var plan = ParameterSweepPlanner.Create(baseline, grid);

        Assert.Equal(4, plan.Candidates.Count);
        Assert.All(plan.Candidates, item => Assert.True(BacktestSpecificationCodec.Verify(item)));
        Assert.Equal(4, plan.Candidates.Select(item => item.SpecificationSha256).Distinct().Count());
        Assert.Equal(64, plan.GridSha256.Length);
    }

    [Fact]
    public void Grid_rejects_unknown_duplicate_and_excessive_dimensions()
    {
        var baseline = Specification();
        Assert.Throws<ArgumentException>(() => ParameterSweepPlanner.Create(baseline,
            new(1, 1, new Dictionary<string, IReadOnlyList<decimal>> { ["unknown"] = [1] })));
        Assert.Throws<ArgumentException>(() => ParameterSweepPlanner.Create(baseline,
            new(1, 1, new Dictionary<string, IReadOnlyList<decimal>> { ["minimumAdx"] = [20, 20] })));
        Assert.Throws<JsonException>(() => ParameterSweepPlanner.Deserialize(
            "{\"schemaVersion\":1,\"schemaVersion\":1,\"topCandidates\":1,\"parameters\":{\"minimumAdx\":[20]}}"));
    }

    private static SealedBacktestSpecification Specification() => BacktestSpecificationCodec.Seal(new(1,
        "vwap-ema-trend-breakout-v1", new(Guid.NewGuid(), "NSE", "TEST", BacktestAssetClass.Equity,
            "INR", 1, .05m), new(new DateTime(2026, 1, 1, 3, 45, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 3, 45, 0, DateTimeKind.Utc), 5, "India Standard Time", "nse-v1",
            "fixture", "v1", new string('a', 64), BacktestMarketDataMode.OhlcvBars),
        new(100_000, 750, 30_000, 5), new(3, 0, "none", new TimeOnly(15, 25),
            BacktestSignalTiming.CompletedBar, BacktestEntryFillPolicy.NextObservedBarOpen,
            BacktestAmbiguousBarPolicy.StopFirst, BacktestEndOfDataPolicy.CloseLastObserved),
        new Dictionary<string, decimal> { ["fastEmaPeriod"] = 20, ["minimumAdx"] = 25 }));
}
