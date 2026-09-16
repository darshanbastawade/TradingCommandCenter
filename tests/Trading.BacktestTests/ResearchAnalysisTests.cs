using Trading.Backtesting;
using Trading.Backtesting.Costs;
using Trading.Backtesting.Metrics;
using Trading.Backtesting.Ranking;
using Trading.Backtesting.Reporting;
using Trading.Backtesting.Robustness;
using Trading.Domain.MarketData;
using Trading.Strategies.Contracts;
using Trading.Strategies.Regimes;

namespace Trading.BacktestTests;

public sealed class ResearchAnalysisTests
{
    private static readonly Guid InstrumentId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T09:15:00+05:30");
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone(
        "Research India", TimeSpan.FromMinutes(330), "Research India", "Research India");

    [Fact]
    public void Parameter_robustness_scores_the_declared_neighborhood_without_selecting_an_optimum()
    {
        var candles = new[] { Candle(0) };
        var scenarios = new[]
        {
            new ParameterScenario<decimal>("baseline", 10m, new Dictionary<string, decimal> { ["threshold"] = 20 }, true),
            new ParameterScenario<decimal>("nearby", 5m, new Dictionary<string, decimal> { ["threshold"] = 22 }),
            new ParameterScenario<decimal>("poor", -2m, new Dictionary<string, decimal> { ["threshold"] = 25 })
        };

        var report = ParameterRobustnessAnalyzer.Analyze(scenarios, pnl => Result(pnl), candles, India);

        Assert.Equal("baseline", report.BaselineScenarioId);
        Assert.Equal(3, report.ScenarioCount);
        Assert.Equal(2m / 3m, report.ProfitableScenarioRate);
        Assert.Equal(2m / 3m, report.AcceptableDegradationRate);
        Assert.Equal(5m, report.MedianExpectancy);
        Assert.Equal(66.67m, report.StabilityScore);
        Assert.False(report.Scenarios.Single(item => item.Id == "poor").MeetsDegradationLimit);
    }

    [Fact]
    public void Parameter_robustness_requires_one_baseline_and_bounded_unique_scenarios()
    {
        var scenarios = new[] { new ParameterScenario<int>("duplicate", 1, new Dictionary<string, decimal>()) };
        Assert.Throws<ArgumentException>(() => ParameterRobustnessAnalyzer.Analyze(
            scenarios, _ => Result(1), new[] { Candle(0) }, India));
    }

    [Fact]
    public void Cartesian_neighborhood_is_reproducible_and_marks_the_declared_baseline()
    {
        var scenarios = ParameterNeighborhood.Cartesian(
            [new("adx", [18m, 20m, 22m]), new("volume", [1m, 1.2m])],
            new Dictionary<string, decimal> { ["adx"] = 20m, ["volume"] = 1.2m },
            values => (Adx: values["adx"], Volume: values["volume"]));

        Assert.Equal(6, scenarios.Count);
        var baseline = Assert.Single(scenarios, item => item.IsBaseline);
        Assert.Equal((20m, 1.2m), baseline.Parameters);
        Assert.Equal("adx=20__volume=1.2", baseline.Id);
    }

    [Fact]
    public void Regime_report_groups_trades_by_point_in_time_labels()
    {
        var result = Result(3m, -1m);
        var regimes = new[]
        {
            Snapshot(result.Trades[0].SignalBarOpenTimeUtc, TrendRegime.Bullish, VolatilityRegime.Normal),
            Snapshot(result.Trades[1].SignalBarOpenTimeUtc, TrendRegime.Sideways, VolatilityRegime.High)
        };

        var report = RegimePerformanceAnalyzer.Analyze(result, regimes);

        Assert.Equal(5, report.Count);
        Assert.Equal(3m, report.Single(row => row.Dimension == "Trend" && row.Regime == "Bullish").NetPnl);
        Assert.Equal(-1m, report.Single(row => row.Dimension == "Volatility" && row.Regime == "High").NetPnl);
    }

    [Fact]
    public void Ranking_selects_only_strategies_that_pass_every_gate()
    {
        var metrics = BacktestMetricsCalculator.Calculate(Result(3m, -1m),
            new[] { Candle(0), Candle(1) }, India);
        var evaluations = new[]
        {
            new StrategyEvaluation("qualified", metrics, .8m, 85m, .75m),
            new StrategyEvaluation("unstable", metrics, .4m, 85m, .75m),
            new StrategyEvaluation("fragile", metrics, .8m, 40m, .75m)
        };
        var settings = new StrategyQualificationSettings
        {
            MinimumTrades = 2, MinimumAverageNetR = .1m, MinimumProfitFactor = 1.2m,
            MaximumDrawdownPercent = .20m, MinimumWalkForwardProfitableFoldRate = .6m,
            MinimumParameterStabilityScore = 70m, MinimumProfitableRegimeRate = .5m,
            MaximumSelections = 2
        };

        var ranking = StrategyRankingEngine.Rank(evaluations, settings);

        Assert.Equal(3, ranking.Rankings.Count);
        Assert.Equal("qualified", Assert.Single(ranking.SelectedStrategies).StrategyId);
        Assert.Contains("walk-forward-instability",
            ranking.Rankings.Single(item => item.StrategyId == "unstable").QualificationFailures);
        Assert.Contains("parameter-instability",
            ranking.Rankings.Single(item => item.StrategyId == "fragile").QualificationFailures);
        Assert.Contains("\"schemaVersion\":1", BacktestReportJson.Serialize(ranking, indented: false));
    }

    [Fact]
    public void Ranking_rejects_invalid_or_duplicate_evidence()
    {
        var metrics = BacktestMetricsCalculator.Calculate(Result(3m, -1m),
            new[] { Candle(0), Candle(1) }, India);
        var duplicate = new StrategyEvaluation("same", metrics, .8m, 80m, .8m);
        Assert.Throws<ArgumentException>(() => StrategyRankingEngine.Rank([duplicate, duplicate]));
        Assert.Throws<ArgumentException>(() => StrategyRankingEngine.Rank(
            [duplicate with { StrategyId = "bad", WalkForwardProfitableFoldRate = 2m }]));
    }

    [Fact]
    public void Ranking_weights_are_configurable_but_must_total_one_hundred()
    {
        var metrics = BacktestMetricsCalculator.Calculate(Result(3m, -1m),
            new[] { Candle(0), Candle(1) }, India);
        var evaluation = new StrategyEvaluation("weighted", metrics, .8m, 80m, .8m);
        var settings = new StrategyRankingSettings
        {
            Qualification = new() { MinimumTrades = 2, MinimumAverageNetR = 0,
                MinimumProfitFactor = 1, MaximumDrawdownPercent = .5m },
            Weights = new() { AverageNetR = 100, ProfitFactor = 0, Drawdown = 0,
                WalkForward = 0, ParameterRobustness = 0, RegimeBreadth = 0, SampleSize = 0 }
        };
        var result = StrategyRankingEngine.Rank([evaluation], settings);
        Assert.Equal(100m, result.Rankings[0].EdgeScore);
        Assert.Throws<ArgumentException>(() => StrategyRankingEngine.Rank([evaluation],
            settings with { Weights = settings.Weights with { SampleSize = 1 } }));
    }

    [Fact]
    public void Stress_reports_remove_best_trades_and_evaluate_execution_scenarios()
    {
        var result = Result(10m, 2m, -1m);
        var candles = new[] { Candle(0), Candle(1), Candle(2) };
        var removal = ResearchStressAnalyzer.RemoveBestTrades(result, candles, India, [1m, 5m]);
        Assert.All(removal.Scenarios, scenario => Assert.Equal(1, scenario.RemovedTradeCount));
        Assert.All(removal.Scenarios, scenario => Assert.Equal(1m, scenario.RemainingMetrics.NetPnl));

        var execution = ResearchStressAnalyzer.ExecutionSensitivity(
            [new("baseline", 5, 0, true), new("adverse", 10, 5)],
            scenario => Result(10m - scenario.AdditionalFixedCostPerSide), candles, India);
        Assert.Equal(2, execution.Scenarios.Count);
        Assert.True(execution.Scenarios[0].Metrics.NetPnl > execution.Scenarios[1].Metrics.NetPnl);
    }

    private static BacktestResult Result(params decimal[] pnl)
    {
        var capital = 1_000m;
        var trades = pnl.Select((value, index) =>
        {
            capital += value;
            var time = Start.AddDays(index).UtcDateTime;
            return new BacktestTrade("research-test", InstrumentId, TradeDirection.Long, time, time, time, 1,
                100m, 99m, 103m, 100m + value, value >= 0 ? BacktestExitReason.Target : BacktestExitReason.StopLoss,
                value, TradeCostBreakdown.None, 0, value, capital);
        }).ToArray();
        return new(1_000m, capital, Array.AsReadOnly(trades), Array.Empty<IgnoredCandidate>());
    }

    private static Candle Candle(int day) => new(InstrumentId, Timeframe.Minute5,
        Start.AddDays(day), 100, 101, 99, 100, 100);

    private static MarketRegimeSnapshot Snapshot(DateTime time, TrendRegime trend, VolatilityRegime volatility) =>
        new(time, trend, volatility, GapRegime.Normal, 1, 1, 1, 1, 1, 0);
}
