using Trading.Backtesting.Metrics;
using Trading.Backtesting.Validation;
using Trading.Domain.MarketData;

namespace Trading.Backtesting.Reporting;

public sealed record WalkForwardFoldReport(
    ValidationWindow Window,
    string SelectedStrategyId,
    int TrainingCandleCount,
    int TestingCandleCount,
    BacktestReport Report);

public sealed record WalkForwardTestArtifact(
    int SchemaVersion,
    string TestType,
    Guid InstrumentId,
    Timeframe Timeframe,
    string TrainingMode,
    int SourceCandleCount,
    int FoldCount,
    int UnusedTrailingSessionCount,
    int ProfitableFoldCount,
    decimal ProfitableFoldRate,
    IReadOnlyList<WalkForwardFoldReport> Folds,
    BacktestReport CombinedReport);

public static class WalkForwardTestArtifactBuilder
{
    public static WalkForwardTestArtifact Build(WalkForwardValidationResult result,
        IReadOnlyList<Candle> sourceCandles, TimeZoneInfo exchangeTimeZone, bool anchoredTraining,
        BacktestReportSettings? reportSettings = null, PerformanceMetricSettings? metricSettings = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(sourceCandles);
        ArgumentNullException.ThrowIfNull(exchangeTimeZone);
        if (result.Folds.Count == 0)
            throw new ArgumentException("Walk-forward validation must contain at least one fold.", nameof(result));

        var foldReports = result.Folds.Select(fold =>
        {
            var training = CandlesInRange(sourceCandles, fold.Window.TrainingStartSession,
                fold.Window.TrainingEndSession, exchangeTimeZone);
            var testing = CandlesInRange(sourceCandles, fold.Window.TestingStartSession,
                fold.Window.TestingEndSession, exchangeTimeZone);
            if (testing.Length == 0)
                throw new ArgumentException("A validation fold has no matching test candles.", nameof(sourceCandles));
            return new WalkForwardFoldReport(fold.Window, fold.SelectedStrategyId, training.Length, testing.Length,
                BacktestReportBuilder.Build(fold.OutOfSampleBacktest, testing, exchangeTimeZone,
                    reportSettings, metricSettings));
        }).ToArray();

        var allTesting = result.Folds.SelectMany(fold => CandlesInRange(sourceCandles,
            fold.Window.TestingStartSession, fold.Window.TestingEndSession, exchangeTimeZone)).ToArray();
        var combined = BacktestReportBuilder.Build(result.CombinedOutOfSampleBacktest, allTesting,
            exchangeTimeZone, reportSettings, metricSettings);
        return new(1, "chronological-walk-forward", allTesting[0].InstrumentId, allTesting[0].Timeframe,
            anchoredTraining ? "anchored" : "rolling", sourceCandles.Count, foldReports.Length,
            result.UnusedTrailingSessionCount, result.ProfitableFoldCount, result.ProfitableFoldRate,
            Array.AsReadOnly(foldReports), combined);
    }

    private static Candle[] CandlesInRange(IEnumerable<Candle> candles, DateOnly start, DateOnly end,
        TimeZoneInfo zone) => candles.Where(candle =>
    {
        var session = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(candle.OpenTimeUtc, zone));
        return session >= start && session <= end;
    }).ToArray();
}
