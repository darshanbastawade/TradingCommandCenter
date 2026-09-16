using Trading.Backtesting.Validation;
using Trading.Backtesting.Metrics;
using Trading.Domain.MarketData;

namespace Trading.Backtesting.Reporting;

public sealed record OosTestArtifact(
    int SchemaVersion,
    string TestType,
    Guid InstrumentId,
    Timeframe Timeframe,
    ValidationWindow Window,
    string SelectedStrategyId,
    int SourceCandleCount,
    int TrainingCandleCount,
    int TestingCandleCount,
    BacktestReport Report);

public static class OosTestArtifactBuilder
{
    public static OosTestArtifact Build(ValidationFold fold, IReadOnlyList<Candle> sourceCandles,
        TimeZoneInfo exchangeTimeZone, BacktestReportSettings? reportSettings = null,
        PerformanceMetricSettings? metricSettings = null)
    {
        ArgumentNullException.ThrowIfNull(fold);
        ArgumentNullException.ThrowIfNull(sourceCandles);
        ArgumentNullException.ThrowIfNull(exchangeTimeZone);
        var trainingCandles = sourceCandles.Count(candle => InRange(candle.OpenTimeUtc,
            fold.Window.TrainingStartSession, fold.Window.TrainingEndSession, exchangeTimeZone));
        var testingCandles = sourceCandles.Where(candle => InRange(candle.OpenTimeUtc,
            fold.Window.TestingStartSession, fold.Window.TestingEndSession, exchangeTimeZone)).ToArray();
        if (testingCandles.Length == 0)
            throw new ArgumentException("The validation fold has no matching test candles.", nameof(sourceCandles));
        var report = BacktestReportBuilder.Build(fold.OutOfSampleBacktest, testingCandles,
            exchangeTimeZone, reportSettings, metricSettings);
        return new(1, "chronological-holdout", testingCandles[0].InstrumentId, testingCandles[0].Timeframe,
            fold.Window, fold.SelectedStrategyId, sourceCandles.Count, trainingCandles,
            testingCandles.Length, report);
    }

    private static bool InRange(DateTime utc, DateOnly start, DateOnly end, TimeZoneInfo zone)
    {
        var session = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));
        return session >= start && session <= end;
    }
}
