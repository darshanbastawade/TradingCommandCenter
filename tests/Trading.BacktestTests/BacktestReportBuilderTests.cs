using Trading.Backtesting;
using Trading.Backtesting.Costs;
using Trading.Backtesting.Reporting;
using Trading.Backtesting.Metrics;
using Trading.Backtesting.Validation;
using Trading.Domain.MarketData;
using Trading.Strategies.Contracts;

namespace Trading.BacktestTests;

public sealed class BacktestReportBuilderTests
{
    private static readonly Guid InstrumentId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-29T09:15:00+05:30");
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone(
        "Reporting India", TimeSpan.FromMinutes(330), "Reporting India", "Reporting India");

    [Fact]
    public void Builds_outcome_period_weekday_and_time_bucket_sections()
    {
        var trades = new[]
        {
            Trade(0, 5, 3m, 1_003m, BacktestExitReason.Target, 5),
            Trade(1, 45, -1m, 1_002m, BacktestExitReason.StopLoss, 10),
            Trade(2, 165, 3m, 1_005m, BacktestExitReason.Target, 15),
            Trade(3, 285, 0m, 1_005m, BacktestExitReason.SessionExit, 0)
        };
        var report = Build(new(1_000m, 1_005m, trades, []), Sessions(4));

        Assert.Equal(1, report.SchemaVersion);
        Assert.Equal(["report-test"], report.StrategyIds);
        Assert.Equal(InstrumentId, report.InstrumentId);
        Assert.Equal(Timeframe.Minute5, report.Timeframe);
        Assert.Equal(5m, report.Outcomes.TotalNetR);
        Assert.Equal(1.5m, report.Outcomes.MedianNetR);
        Assert.Equal(1m, report.Outcomes.MaximumDrawdownR);
        Assert.Equal(7.5m, report.Outcomes.AverageDurationMinutes);
        Assert.Equal(7.5m, report.Outcomes.MedianDurationMinutes);
        Assert.Equal(0.5m, report.Outcomes.TargetExitRate);
        Assert.Equal(0.25m, report.Outcomes.StopLossExitRate);
        Assert.Equal(0.25m, report.Outcomes.SessionExitRate);
        Assert.Equal(0m, report.Outcomes.EndOfDataExitRate);

        Assert.Collection(report.Monthly,
            september => { Assert.Equal("2026-09", september.Key); Assert.Equal(2m, september.NetPnl); },
            october => { Assert.Equal("2026-10", october.Key); Assert.Equal(3m, october.NetPnl); });
        Assert.Equal(4, report.Weekdays.Count);
        Assert.Equal(new[] { "Open", "Morning", "Midday", "Afternoon" },
            report.TimeBuckets.Select(bucket => bucket.Key));
        Assert.All(report.TimeBuckets, bucket => Assert.Equal(1, bucket.TotalTrades));
    }

    [Fact]
    public void Uses_entry_time_for_custom_buckets_and_reports_uncovered_trades()
    {
        var trades = new[]
        {
            Trade(0, 5, 3m, 1_003m, BacktestExitReason.Target, 5),
            Trade(1, 45, -1m, 1_002m, BacktestExitReason.StopLoss, 5)
        };
        var settings = new BacktestReportSettings
        {
            TimeBuckets = [new("First 30 minutes", new(9, 15), new(9, 45))]
        };
        var report = BacktestReportBuilder.Build(new(1_000m, 1_002m, trades, []),
            Sessions(2), India, settings);

        Assert.Collection(report.TimeBuckets,
            covered => { Assert.Equal("First 30 minutes", covered.Key); Assert.Equal(1, covered.TotalTrades); },
            outside => { Assert.Equal("Outside configured buckets", outside.Key); Assert.Equal(1, outside.TotalTrades); });
    }

    [Fact]
    public void Empty_report_preserves_observed_equity_and_nullable_trade_statistics()
    {
        var report = Build(new(1_000m, 1_000m, [], []), Sessions(2));

        Assert.Empty(report.StrategyIds);
        Assert.Equal(2, report.Summary.EquityCurve.Count);
        Assert.Null(report.Outcomes.TotalNetR);
        Assert.Null(report.Outcomes.MedianDurationMinutes);
        Assert.Empty(report.Monthly);
        Assert.Empty(report.Weekdays);
        Assert.Equal(4, report.TimeBuckets.Count);
        Assert.All(report.TimeBuckets, bucket => Assert.Equal(0, bucket.TotalTrades));
    }

    [Fact]
    public void Invalid_overlapping_or_duplicate_time_buckets_are_rejected()
    {
        var overlap = new BacktestReportSettings
        {
            TimeBuckets =
            [
                new("One", new(9, 15), new(10, 0)),
                new("Two", new(9, 45), new(10, 30))
            ]
        };
        Assert.Throws<ArgumentException>(() => BacktestReportBuilder.Build(
            new(1_000m, 1_000m, [], []), Sessions(1), India, overlap));

        var duplicate = new BacktestReportSettings
        {
            TimeBuckets =
            [
                new("Morning", new(9, 15), new(10, 0)),
                new("morning", new(10, 0), new(11, 0))
            ]
        };
        Assert.Throws<ArgumentException>(() => BacktestReportBuilder.Build(
            new(1_000m, 1_000m, [], []), Sessions(1), India, duplicate));
    }

    [Fact]
    public void Json_export_is_versioned_camel_case_and_strict()
    {
        var report = Build(new(1_000m, 1_000m, [], []), Sessions(1));
        var json = BacktestReportJson.Serialize(report, indented: false);

        Assert.Contains("\"schemaVersion\":1", json);
        Assert.Contains("\"timeBuckets\"", json);
        Assert.DoesNotContain("NaN", json);
        Assert.DoesNotContain("Infinity", json);
    }

    [Fact]
    public void Oos_artifact_reports_the_validation_boundary_and_test_candles_only()
    {
        var candles = Sessions(3);
        var empty = new BacktestResult(1_000m, 1_000m, [], []);
        var testMetrics = BacktestMetricsCalculator.Calculate(empty, [candles[2]], India);
        var fold = new ValidationFold(new(0,
            DateOnly.FromDateTime(Start.Date), DateOnly.FromDateTime(Start.AddDays(1).Date),
            DateOnly.FromDateTime(Start.AddDays(2).Date), DateOnly.FromDateTime(Start.AddDays(2).Date),
            2, 0, 1), "report-test", empty, testMetrics);

        var artifact = OosTestArtifactBuilder.Build(fold, candles, India);

        Assert.Equal("chronological-holdout", artifact.TestType);
        Assert.Equal(3, artifact.SourceCandleCount);
        Assert.Equal(2, artifact.TrainingCandleCount);
        Assert.Equal(1, artifact.TestingCandleCount);
        Assert.Equal(fold.Window.TestingStartSession, artifact.Report.StartSession);
        Assert.Contains("\"testType\":\"chronological-holdout\"",
            BacktestReportJson.Serialize(artifact, indented: false));
    }

    private static BacktestReport Build(BacktestResult result, IReadOnlyList<Candle> candles) =>
        BacktestReportBuilder.Build(result, candles, India);

    private static BacktestTrade Trade(int day, int entryMinutes, decimal pnl, decimal capital,
        BacktestExitReason reason, int durationMinutes)
    {
        var signal = Start.AddDays(day).AddMinutes(entryMinutes - 5).UtcDateTime;
        var entry = Start.AddDays(day).AddMinutes(entryMinutes).UtcDateTime;
        var exit = entry.AddMinutes(durationMinutes);
        var exitPrice = reason switch
        {
            BacktestExitReason.Target => 103m,
            BacktestExitReason.StopLoss => 99m,
            _ => 100m
        };
        return new("report-test", InstrumentId, TradeDirection.Long, signal, entry, exit, 1,
            100m, 99m, 103m, exitPrice, reason, pnl, TradeCostBreakdown.None, 0m, pnl, capital);
    }

    private static Candle[] Sessions(int count) => Enumerable.Range(0, count).Select(day =>
        new Candle(InstrumentId, Timeframe.Minute5, Start.AddDays(day), 100, 103, 99, 100, 100)).ToArray();
}
